using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Task = System.Threading.Tasks.Task;

namespace ClaudeVS.Chat
{
    /// <summary>
    /// Pont entre l'interface web du panneau et le processus claude :
    /// relaie les messages, gère les permissions et ouvre les modifications proposées dans le diff de VS.
    /// </summary>
    internal sealed class ChatController : IDisposable
    {
        private static readonly string[] EditTools = { "Edit", "MultiEdit", "Write" };

        private const int HistoryBatchSize = 50;        // messages par lot envoyé au panneau
        private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan UsageRefreshInterval = TimeSpan.FromMinutes(5);  // consommation du forfait, au repos

        private readonly ClaudeVSPackage _package;
        private readonly IIdeContext _ide;
        private readonly Action<JObject> _post;
        private readonly PermissionBroker _permissions;
        private ClaudeProcess _claude;
        private string _model;            // null = modèle par défaut du CLI
        private string _effort;           // null = effort par défaut du modèle
        private readonly ControlRequests _controls;
        // tool_use_id des outils d'édition vus dans les messages assistant du tour : seuls leurs résultats déclenchent le formatage auto
        private readonly HashSet<string> _editToolUses = new HashSet<string>();
        private string _permissionMode = "default";
        private bool _attachSelection = true;
        private bool _autoFormat;                 // Ctrl+K Ctrl+D après chaque fichier modifié par Claude
        private bool _autoAnalyzeExceptions = true; // arrêt sur exception dans VS → analyse demandée à Claude
        private JObject _lastExceptionState;
        private bool _busy;
        private bool _preloaded;                  // le CLI a déjà été lancé à l'ouverture du panneau
        private bool _cliReady;                  // le processus courant a répondu (control_response ou system/init)
        private string _mcpConfigPath;            // fichier temporaire contenant le jeton du serveur MCP
        private int _restartSeq;                  // invalide un chargement d'historique dépassé par un autre Restart
        private string _claudeCwd;                // dossier de travail du processus en cours (fixé au lancement)
        private readonly EventHandler<Microsoft.VisualStudio.Shell.Events.OpenSolutionEventArgs> _onSolutionOpened;
        private readonly EventHandler<Microsoft.VisualStudio.Shell.Events.FolderEventArgs> _onFolderOpened;
        private readonly EventHandler _onSolutionClosed;
        private bool _cwdSwitchPending;           // solution changée pendant un tour : bascule de dossier à la fin du tour

        public ChatController(ClaudeVSPackage package, Action<JObject> post)
            : this(package, new VsIdeContext(package), post)
        {
        }

        internal ChatController(ClaudeVSPackage package, IIdeContext ide, Action<JObject> post)
        {
            _package = package;
            _ide = ide;
            _post = post;
            _permissions = new PermissionBroker(ide, post, (id, response) => TrySend(() => _claude?.SendControlResponse(id, response)), RunOnUi);
            _controls = new ControlRequests(SendControlRequest, post, ide.Log);
            SelectionTracker.Changed += OnSelectionChanged;
            DebugMonitor.BreakOnException += OnExceptionBreak;
            _onSolutionOpened = (s, e) => OnSolutionOpened();
            _onFolderOpened = (s, e) => OnSolutionOpened();
            _onSolutionClosed = (s, e) => RunOnUi(PostSettings, "solution");
            Microsoft.VisualStudio.Shell.Events.SolutionEvents.OnAfterOpenSolution += _onSolutionOpened;
            Microsoft.VisualStudio.Shell.Events.SolutionEvents.OnAfterOpenFolder += _onFolderOpened;
            // Fermeture seule : pas de bascule vers le dossier utilisateur, le CLI reste là où il est.
            // Une ouverture de solution (qui suit toujours la fermeture de la précédente) fera la bascule.
            Microsoft.VisualStudio.Shell.Events.SolutionEvents.OnAfterCloseSolution += _onSolutionClosed;
        }

        /// <summary>
        /// Une solution (ou un dossier) vient d'être ouverte : le CLI doit travailler dans son dossier.
        /// Un processus ne peut pas changer de dossier de travail : on le relance dans le nouveau. Si un tour est
        /// en cours, la bascule attend sa fin pour ne pas couper Claude au milieu d'une modification.
        /// </summary>
        private void OnSolutionOpened()
        {
            RunOnUi(() =>
            {
                PostSettings();
                PreloadCli();
                if (!NeedsCwdSwitch()) return;
                if (_busy)
                {
                    _cwdSwitchPending = true;
                    PostBanner($"La solution a changé : Claude passera dans {_ide.SolutionDirectory()} à la fin de sa réponse en cours.");
                    return;
                }
                SwitchToSolutionDirectory();
            }, "solution");
        }

        /// <summary>
        /// Lance le CLI dès l'ouverture du panneau : sa réponse à initialize fournit la liste des modèles, sans coût de
        /// jetons (aucun message n'est envoyé). Seulement quand une solution est ouverte, pour que le processus
        /// démarre dans le bon dossier ; sinon la liste attend le premier message.
        /// </summary>
        private void PreloadCli()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_claude != null || _preloaded) return;
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.Equals(_ide.SolutionDirectory(), userProfile, StringComparison.OrdinalIgnoreCase)) return;
            _preloaded = true; // une seule fois : après « Nouvelle conversation », le processus ne repart qu'au prochain message
            EnsureStarted(null);
        }

        private bool NeedsCwdSwitch()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return _claude?.IsRunning == true && _claudeCwd != null
                && !string.Equals(_ide.SolutionDirectory(), _claudeCwd, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Relance le CLI dans le dossier de la solution courante (nouvelle conversation).</summary>
        private void SwitchToSolutionDirectory()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _cwdSwitchPending = false;
            if (!NeedsCwdSwitch()) return;
            var previous = _claudeCwd;
            Restart(null); // arrête le processus et vide le fil : les conversations sont rangées par dossier
            var current = _ide.SolutionDirectory();
            PostBanner($"Solution changée : Claude travaille maintenant dans {current}.\n"
                     + $"La conversation précédente reste disponible dans l'historique de {previous}.");
            EnsureStarted(null);
            PostSettings(); // le dossier affiché suit le nouveau processus (avant la relance, il montrait encore l'ancien)
        }

        /// <summary>Dossier de référence des chemins : celui du CLI s'il tourne, sinon celui de la solution courante.</summary>
        private string EffectiveCwd()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return _claude?.IsRunning == true && _claudeCwd != null ? _claudeCwd : _ide.SolutionDirectory();
        }

        // ---- Exécution protégée sur le thread UI --------------------------------------------

        /// <summary>
        /// Bascule sur le thread UI et exécute le corps sous try/catch : une exception est journalisée avec sa pile
        /// et signalée dans le chat, au lieu de partir dans la télémétrie de VS en laissant le panneau figé.
        /// </summary>
        private void RunOnUi(Func<Task> body, string name)
        {
            _package.JoinableTaskFactory.RunAsync(async () =>
            {
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                try { await body(); }
                catch (Exception ex)
                {
                    _ide.Log.Error(name + " : " + ex);
                    PostError($"Erreur interne ({name}) : {ex.Message}");
                }
            }).FileAndForget("ClaudeVS/" + name);
        }

        private void RunOnUi(Action body, string name) => RunOnUi(() => { body(); return Task.CompletedTask; }, name);

        // ---- Messages venant de l'interface -----------------------------------------------

        public void HandleUiMessage(JObject msg)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try { DispatchUiMessage(msg); }
            catch (Exception ex)
            {
                _ide.Log.Error($"Message de l'interface ({msg["type"]}) : {ex}");
                PostError($"Erreur interne ({msg["type"]}) : {ex.Message}");
            }
        }

        private void DispatchUiMessage(JObject msg)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            switch ((string)msg["type"])
            {
                case ToHost.Ready:
                    PostStatus(_claude?.IsRunning == true ? (_busy ? "busy" : "ready") : "idle");
                    PostSettings();
                    OnSelectionChanged(SelectionTracker.Latest);
                    PreloadCli();
                    // Interface rechargée (plantage WebView2) : les demandes de permission encore ouvertes sont réaffichées
                    _permissions.RepostPending();
                    break;
                case ToHost.Send:
                    Send((string)msg["text"], msg["images"] as JArray);
                    break;
                case ToHost.SearchFiles:
                    SearchFiles((string)msg["query"], (int?)msg["seq"] ?? 0);
                    break;
                case ToHost.Interrupt:
                    // Réponse consommée ici : un refus du CLI (tour déjà terminé) n'a pas à s'afficher en erreur
                    _controls.Send(new JObject { ["subtype"] = "interrupt" }, _ => { });
                    break;
                case ToHost.Permission:
                    _permissions.Resolve((string)msg["requestId"], (JObject)msg["response"]);
                    break;
                case ToHost.ShowDiff:
                    var req = (string)msg["requestId"];
                    if (_permissions.TryGet(req, out var request)) _permissions.ShowEditDiff(req, request);
                    break;
                case ToHost.SetMode:
                {
                    var mode = (string)msg["mode"] ?? "default";
                    if (!ClaudeSettings.IsValidMode(mode)) { RejectSetting("mode", mode); break; }
                    _permissionMode = mode;
                    ApplySetting("mode", new JObject { ["subtype"] = "set_permission_mode", ["mode"] = _permissionMode });
                    PostSettings();
                    break;
                }
                case ToHost.SetModel:
                {
                    var model = (string)msg["model"];
                    if (!string.IsNullOrEmpty(model) && !ClaudeSettings.IsValidModel(model)) { RejectSetting("modèle", model); break; }
                    _model = string.IsNullOrEmpty(model) ? null : model;
                    ApplySetting("model", new JObject { ["subtype"] = "set_model", ["model"] = _model });
                    _controls.RequestApplied(); // le défaut d'effort dépend du modèle
                    PostSettings();
                    break;
                }
                case ToHost.SetEffort:
                {
                    var effort = (string)msg["effort"];
                    if (!string.IsNullOrEmpty(effort) && !ClaudeSettings.IsValidEffort(effort)) { RejectSetting("effort", effort); break; }
                    _effort = string.IsNullOrEmpty(effort) ? null : effort;
                    // À chaud : réglage de session (non persisté) ; au démarrage : --effort
                    if (_effort != null)
                        ApplySetting("effort", new JObject
                        {
                            ["subtype"] = "apply_flag_settings",
                            ["settings"] = new JObject { ["effortLevel"] = _effort },
                        });
                    else if (_claude?.IsRunning == true)
                        // Le CLI n'a pas de requête pour revenir à l'effort par défaut : il garde le réglage précédent
                        PostBanner("Effort par défaut : il s'appliquera à la prochaine conversation.\nLa conversation en cours garde l'effort actuel.");
                    else
                        PostSettingLater("effort");
                    PostSettings();
                    break;
                }
                case ToHost.Rewind:
                    _controls.Rewind((string)msg["uuid"], (bool?)msg["dryRun"] ?? false);
                    break;
                case ToHost.SetAttachSelection:
                    _attachSelection = (bool?)msg["value"] ?? true;
                    PostSettings();
                    break;
                case ToHost.SetAutoFormat:
                    _autoFormat = (bool?)msg["value"] ?? false;
                    PostSettings();
                    break;
                case ToHost.SetAutoAnalyze:
                    _autoAnalyzeExceptions = (bool?)msg["value"] ?? true;
                    PostSettings();
                    break;
                case ToHost.AnalyzeException:
                    if (_lastExceptionState != null) Send(IdePrompts.ExceptionPrompt(_lastExceptionState), null);
                    break;
                case ToHost.NewChat:
                    Restart(null);
                    break;
                case ToHost.ListSessions:
                    ListSessions();
                    break;
                case ToHost.Resume:
                    Restart((string)msg["sessionId"]);
                    break;
                case ToHost.OpenFile:
                    OpenFile((string)msg["path"], (int?)msg["line"]);
                    break;
            }
        }

        /// <summary>Valeur inattendue venant de l'interface : refusée, et les réglages réels sont renvoyés pour resynchroniser les sélecteurs.</summary>
        private void RejectSetting(string what, string value)
        {
            var shown = value.Length > 60 ? value.Substring(0, 60) + "…" : value;
            PostError($"Valeur de {what} refusée : « {shown} ».");
            PostSettings();
        }

        private void Send(string text, JArray images)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var hasImages = images != null && images.Count > 0;
            if (string.IsNullOrWhiteSpace(text) && !hasImages) return;
            if (!EnsureStarted(null)) return;

            var content = new JArray();
            var context = _attachSelection ? IdePrompts.SelectionContext(SelectionTracker.Latest) : null;
            if (context != null) content.Add(new JObject { ["type"] = "text", ["text"] = context });
            if (hasImages)
                foreach (var img in images)
                    content.Add(new JObject
                    {
                        ["type"] = "image",
                        ["source"] = new JObject { ["type"] = "base64", ["media_type"] = img["mediaType"], ["data"] = img["data"] },
                    });
            if (!string.IsNullOrWhiteSpace(text))
                content.Add(new JObject { ["type"] = "text", ["text"] = text });
            // busy seulement si la mise en file a réussi : sinon l'erreur est affichée et le statut reste cohérent
            if (!TrySend(() => _claude.SendUserMessage(content))) return;
            _busy = true;
            PostStatus("busy");
        }

        /// <summary>Envoi vers le CLI ; un échec (pipe fermé) est affiché et traité comme une mort du processus.</summary>
        private bool TrySend(Action send)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try { send(); return true; }
            catch (Exception ex) { HandleWriteFailure(_claude, ex); return false; }
        }

        /// <summary>Requête de contrôle vers le CLI : son request_id, ou null si le CLI ne tourne pas ou si l'envoi a échoué.</summary>
        private string SendControlRequest(JObject request)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_claude?.IsRunning != true) return null;
            string id = null;
            return TrySend(() => id = _claude.SendControlRequest(request)) ? id : null;
        }

        /// <summary>Réglage de la barre d'outils : envoyé au CLI s'il tourne (sa réponse est confirmée à l'interface), sinon appliqué à son démarrage.</summary>
        private void ApplySetting(string setting, JObject request)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_claude?.IsRunning != true) PostSettingLater(setting);
            else _controls.ApplySetting(setting, request); // échec d'envoi : déjà affiché par TrySend
        }

        /// <summary>CLI arrêté : le réglage passera par la ligne de commande au prochain démarrage.</summary>
        private void PostSettingLater(string setting) =>
            _post(new JObject { ["type"] = FromHost.SettingApplied, ["setting"] = setting, ["ok"] = true, ["later"] = true });

        private void HandleWriteFailure(ClaudeProcess source, Exception ex)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (source == null || source != _claude) return;
            _ide.Log.Error("Envoi vers claude : " + ex);
            var stderr = source.StderrTail;
            PostError($"Envoi vers claude impossible : {ex.Message}{(string.IsNullOrWhiteSpace(stderr) ? "" : "\n" + stderr.Trim())}");
            StopProcess();
            PostStatus("idle");
        }

        private readonly FileIndex _files = new FileIndex();

        /// <summary>Autocomplétion des @-mentions ; seq permet à l'interface d'ignorer les réponses périmées.</summary>
        private void SearchFiles(string query, int seq)
        {
            var root = EffectiveCwd();
            RunOnUi(async () =>
            {
                var files = await _files.SearchAsync(root, query);
                _post(new JObject { ["type"] = FromHost.Files, ["seq"] = seq, ["files"] = new JArray(files) });
            }, "files");
        }

        /// <summary>Insère une @-mention dans la zone de saisie (commande « Envoyer la sélection à Claude »).</summary>
        public void InsertMention(string path, int? lineStart, int? lineEnd)
        {
            var root = EffectiveCwd();
            var rel = path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase) ? path.Substring(root.Length + 1) : path;
            var mention = "@" + rel.Replace('\\', '/');
            if (lineStart.HasValue)
                mention += lineEnd.HasValue && lineEnd != lineStart ? $"#L{lineStart + 1}-{lineEnd + 1}" : $"#L{lineStart + 1}";
            _post(new JObject { ["type"] = FromHost.Insert, ["text"] = mention + " " });
        }

        // ---- Cycle de vie du processus ------------------------------------------------------

        private bool EnsureStarted(string resumeSessionId)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_claude?.IsRunning == true) return true;
            // Processus mort dont la sortie n'a pas encore été traitée : on libère tout avant de relancer
            if (_claude != null) StopProcess();

            var cmd = ClaudeLauncher.FindClaude();
            if (cmd == null)
            {
                PostError(ClaudeLauncher.NotFoundMessage);
                return false;
            }

            var args = ClaudeArguments.Build(cmd.PrefixArgs, new ClaudeOptions
            {
                PermissionMode = _permissionMode,
                Model = _model,
                Effort = _effort,
                ResumeSessionId = resumeSessionId,
                McpConfigPath = WriteIdeMcpConfig(),
            });

            ClaudeProcess claude = null;
            try
            {
                // Sauvegarde des fichiers avant chaque modification, pour le retour arrière
                var env = new Dictionary<string, string> { ["CLAUDE_CODE_ENABLE_SDK_FILE_CHECKPOINTING"] = "true" };
                _ide.Log.Info("CLI : " + cmd);
                var cwd = _ide.SolutionDirectory();
                claude = new ClaudeProcess(cmd.FileName, cwd, args, env, _ide.Log);
                claude.MessageReceived += m => OnCliMessage(claude, m);
                claude.Exited += (code, stderr) => OnCliExited(claude, code, stderr);
                claude.WriteFailed += ex => RunOnUi(() => HandleWriteFailure(claude, ex), "write");
                _claude = claude;
                _claudeCwd = cwd;
                _cliReady = false;
                claude.SendControlRequest(new JObject { ["subtype"] = "initialize" });
                PostStatus("starting");
                WatchStartup(claude);
                return true;
            }
            catch (Exception ex)
            {
                _ide.Log.Error("Lancement de claude : " + ex);
                PostError("Impossible de lancer claude : " + ex.Message);
                if (_claude == claude) StopProcess();
                else claude?.Dispose();
                return false;
            }
        }

        /// <summary>Si le CLI n'a pas répondu à initialize au bout de 30 s, on le dit plutôt que de laisser « Démarrage… » sans fin.</summary>
        private void WatchStartup(ClaudeProcess claude)
        {
            RunOnUi(async () =>
            {
                await Task.Delay(StartupTimeout);
                if (claude != _claude || _cliReady || !claude.IsRunning) return;
                PostBanner($"Claude ne répond pas depuis {StartupTimeout.TotalSeconds:0} s (démarrage en attente).\n"
                         + "Vérifiez le volet Sortie « Claude » ; le bouton Nouvelle conversation (+) relance le processus.", sticky: true);
            }, "startup");
        }

        private void Restart(string resumeSessionId)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var seq = ++_restartSeq;
            StopProcess();
            _post(new JObject { ["type"] = FromHost.Cleared });
            PostStatus("idle"); // y compris pendant le chargement d'un historique : pas de « Claude travaille… » résiduel
            if (resumeSessionId == null) return;

            var cwd = _ide.SolutionDirectory();
            RunOnUi(async () =>
            {
                // Lecture du .jsonl hors thread UI : les sessions font facilement plusieurs dizaines de Mo
                var messages = await Task.Run(() => SessionStore.Load(cwd, resumeSessionId, _ide.Log));
                if (seq != _restartSeq) return; // une autre conversation a été lancée entre-temps
                // Par lots, pour ne pas sérialiser un seul message WebView géant ; « last » marque le dernier
                for (var i = 0; i < messages.Count || i == 0; i += HistoryBatchSize)
                {
                    var batch = new JArray(messages.Skip(i).Take(HistoryBatchSize));
                    var last = i + HistoryBatchSize >= messages.Count;
                    _post(new JObject { ["type"] = FromHost.History, ["messages"] = batch, ["last"] = last });
                }
                if (_claude != null)
                {
                    // Un message envoyé pendant le chargement a déjà lancé un processus, sans --resume
                    PostBanner("Un message a été envoyé pendant le chargement : la conversation affichée n'a pas été reprise par Claude.\n"
                             + "Rouvrez-la avec le bouton Conversations précédentes (🕘) pour la poursuivre.", sticky: true);
                    return;
                }
                if (!EnsureStarted(resumeSessionId)) PostStatus("idle");
            }, "resume");
        }

        private void ListSessions()
        {
            var cwd = _ide.SolutionDirectory();
            RunOnUi(async () =>
            {
                var sessions = await Task.Run(() => SessionStore.List(cwd, _ide.Log));
                _post(new JObject { ["type"] = FromHost.Sessions, ["sessions"] = sessions });
            }, "sessions");
        }

        private void StopProcess()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var old = _claude;
            _claude = null;
            _claudeCwd = null;
            _cwdSwitchPending = false;
            _busy = false;
            _cliReady = false;
            _permissions.ClearAll();
            _controls.Clear();
            _editToolUses.Clear();
            old?.Dispose();
            DeleteIdeMcpConfig();
        }

        /// <summary>
        /// Le serveur de l'extension est passé au CLI comme serveur MCP « visualstudio » :
        /// getDiagnostics, éditeurs ouverts, sélection… (le type ws-ide est ignoré hors mode interactif).
        /// </summary>
        private string WriteIdeMcpConfig()
        {
            var server = _package.Server;
            if (server == null) return null;
            var path = Path.Combine(Path.GetTempPath(), "ClaudeVS", $"mcp-ide-{server.Port}.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var config = ClaudeArguments.McpConfig(server.Port, server.AuthToken);
            File.WriteAllText(path, config.ToString(Formatting.None));
            _mcpConfigPath = path;
            return path;
        }

        /// <summary>Le fichier contient le jeton du serveur : il ne survit pas au processus.</summary>
        private void DeleteIdeMcpConfig()
        {
            var path = _mcpConfigPath;
            _mcpConfigPath = null;
            if (path == null) return;
            try { File.Delete(path); }
            catch (Exception ex) { _ide.Log.Info($"Suppression de {path} : {ex.Message}"); }
        }

        // ---- Messages venant du CLI (thread d'arrière-plan) --------------------------------

        private void OnCliMessage(ClaudeProcess source, JObject msg)
        {
            RunOnUi(() =>
            {
                if (source != _claude) return; // message d'un processus déjà remplacé

                var type = (string)msg["type"];
                string editRequestId = null;
                if (type == "control_request" && (string)msg["request"]?["subtype"] == "can_use_tool")
                {
                    var id = (string)msg["request_id"];
                    var request = (JObject)msg["request"];
                    _permissions.Add(id, request);
                    if (EditTools.Contains((string)request["tool_name"])) editRequestId = id;
                }
                else if (type == "control_request")
                {
                    // Requête non gérée (hook_callback, elicitation…) : on répond une erreur pour ne pas bloquer le CLI
                    TrySend(() => source.Send(new JObject
                    {
                        ["type"] = "control_response",
                        ["response"] = new JObject
                        {
                            ["subtype"] = "error", ["request_id"] = msg["request_id"],
                            ["error"] = "Non pris en charge par ClaudeVS",
                        },
                    }));
                    return;
                }
                else if (type == "control_cancel_request")
                {
                    // Le CLI retire une demande (tour interrompu…) : la carte et le diff n'ont plus d'objet
                    var id = (string)msg["request_id"];
                    if (id != null) _permissions.Cancel(id);
                }
                else if (type == "control_response")
                {
                    var response = msg["response"] as JObject;
                    var id = (string)response?["request_id"];
                    // Toute réponse de contrôle (à commencer par celle d'« initialize ») prouve que le CLI est vivant.
                    // system/init, lui, n'arrive qu'avec le premier message : un CLI relancé à vide (changement de
                    // solution) déclencherait sinon une fausse alerte « ne répond pas depuis 30 s ».
                    _cliReady = true;
                    if (!_busy) PostStatus("ready");
                    // Réponse à initialize (seule à porter la liste des modèles) : on demande ce que le CLI applique par défaut
                    // Elle déclenche aussi la consommation du forfait, rafraîchie ensuite toutes les 5 min au repos : elle
                    // bouge aussi avec les autres sessions (terminal, autre instance), pas seulement à la fin de nos tours
                    if (response?["response"]?["models"] != null)
                    {
                        _controls.RequestApplied();
                        _controls.RequestUsage();
                        RunOnUi(async () =>
                        {
                            while (true)
                            {
                                await Task.Delay(UsageRefreshInterval);
                                if (source != _claude || !source.IsRunning) return;
                                if (!_busy) _controls.RequestUsage();
                            }
                        }, "usage");
                    }
                    // Le gestionnaire produit son propre message vers l'interface (rewindPreview, context…) :
                    // pas de relais brut, sinon une erreur s'afficherait deux fois
                    if (_controls.TryComplete(id, response)) return;
                }
                else if (type == "system" && (string)msg["subtype"] == "init")
                {
                    _cliReady = true;
                    if (!_busy) PostStatus("ready");
                    _controls.RequestContextUsage();
                }
                else if (type == "stream_event" && !_busy && (msg["parent_tool_use_id"]?.Type ?? JTokenType.Null) == JTokenType.Null)
                {
                    // Tour démarré par un message mis en file (ex. consigne après un refus). Le flux d'un sous-agent en
                    // arrière-plan, qui continue après la fin du tour principal, n'en démarre pas un.
                    _busy = true;
                    PostStatus("busy");
                }
                else if (type == "result")
                {
                    _busy = false;
                    // Les permissions encore ouvertes restent valables : un agent en arrière-plan continue après la fin du
                    // tour et attend sa réponse. Le CLI retire lui-même une demande abandonnée (control_cancel_request).
                    _editToolUses.Clear();
                    PostStatus("ready");
                    // Bascule de dossier reportée : planifiée après ce traitement, pour que le « result » soit d'abord affiché
                    if (_cwdSwitchPending) RunOnUi(SwitchToSolutionDirectory, "solution");
                    else
                    {
                        _controls.RequestContextUsage();
                        _controls.RequestUsage();
                    }
                }
                else if (type == "assistant")
                {
                    TrackEditToolUses(msg["message"]?["content"] as JArray);
                }
                // tool_use_result est un objet pour les outils intégrés (Edit/Write…), mais une simple chaîne quand un outil
                // MCP échoue : l'indexer directement levait une exception et le message n'était jamais relayé à l'interface
                else if (type == "user" && (msg["tool_use_result"] as JObject)?["filePath"] is JValue fp)
                {
                    // Fichier écrit par Claude (Edit/Write/MultiEdit) : passage de Mettre en forme le document
                    var results = msg["message"]?["content"] as JArray;
                    var block = results?.FirstOrDefault(c => (string)c["type"] == "tool_result");
                    var toolUseId = (string)block?["tool_use_id"];
                    if (toolUseId != null && _editToolUses.Remove(toolUseId) && _autoFormat && (bool?)block["is_error"] != true)
                        AutoFormat((string)fp);
                }

                // Relais avant tout traitement faillible : la carte de permission doit s'afficher même si le diff échoue
                _post(new JObject { ["type"] = FromHost.Cli, ["msg"] = msg });
                if (editRequestId != null && _permissions.TryGet(editRequestId, out var editRequest))
                    _permissions.ShowEditDiff(editRequestId, editRequest);
            }, "cli");
        }

        private void TrackEditToolUses(JArray content)
        {
            if (content == null) return;
            foreach (var c in content)
                if ((string)c["type"] == "tool_use" && EditTools.Contains((string)c["name"]) && (string)c["id"] is string id)
                    _editToolUses.Add(id);
        }

        private void AutoFormat(string path)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var r = _ide.FormatDocument(path);
                if (r != null && (bool?)r["changed"] == true) PostNotice("✨ Formaté : " + Path.GetFileName(path));
            }
            catch (Exception ex) { _ide.Log.Info($"Formatage auto de {path} : {ex.Message}"); }
        }

        // ---- Événements du débogueur -----------------------------------------------------------

        private void OnExceptionBreak(JObject state)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _lastExceptionState = state;
            var auto = _autoAnalyzeExceptions && _claude?.IsRunning == true && !_busy;
            _post(new JObject { ["type"] = FromHost.DebugEvent, ["state"] = state, ["auto"] = auto });
            if (auto) Send(IdePrompts.ExceptionPrompt(state), null);
        }

        private void OnCliExited(ClaudeProcess source, int code, string stderr)
        {
            RunOnUi(() =>
            {
                if (source != _claude) return;
                var wasBusy = _busy;
                StopProcess(); // libère Process/StreamWriter, ferme les diffs, expire les permissions
                PostStatus("idle");
                if (code != 0)
                    PostError($"Claude s'est arrêté de façon inattendue (code {code}). Envoyez un message pour le relancer."
                            + (string.IsNullOrWhiteSpace(stderr) ? "" : "\n" + stderr.Trim()));
                else if (wasBusy)
                    PostBanner("Claude s'est arrêté avant de terminer sa réponse.\nEnvoyez un nouveau message pour le relancer.", sticky: true);
            }, "exit");
        }

        // ---- Contexte IDE -----------------------------------------------------------------------

        private void OnSelectionChanged(JObject sel)
        {
            if (sel == null) { _post(new JObject { ["type"] = FromHost.Selection, ["label"] = null }); return; }
            _post(new JObject { ["type"] = FromHost.Selection, ["label"] = IdePrompts.SelectionLabel(sel) });
        }

        private void OpenFile(string path, int? line)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (string.IsNullOrEmpty(path)) return;
            if (!Path.IsPathRooted(path)) path = Path.Combine(EffectiveCwd(), path);
            if (!File.Exists(path)) return;
            _ide.OpenFile(path, line);
        }

        // ---- Vers l'interface ---------------------------------------------------------------------

        private void PostStatus(string state) => _post(new JObject { ["type"] = FromHost.Status, ["state"] = state });
        private void PostError(string text) => _post(new JObject { ["type"] = FromHost.Error, ["text"] = text });
        private void PostNotice(string text) => _post(new JObject { ["type"] = FromHost.Notice, ["text"] = text });
        /// <summary>Annonce importante, hors du fil : bandeau orange sous la barre d'outils, jusqu'à ce que l'utilisateur le ferme.</summary>
        /// <param name="sticky">Vrai : pas de fermeture automatique (l'annonce demande une action de l'utilisateur).</param>
        private void PostBanner(string text, bool sticky = false) => _post(new JObject { ["type"] = FromHost.Banner, ["text"] = text, ["sticky"] = sticky });

        private void PostSettings() => _post(new JObject
        {
            ["type"] = FromHost.Settings,
            ["model"] = _model ?? "",
            ["effort"] = _effort ?? "",
            ["mode"] = _permissionMode,
            ["attachSelection"] = _attachSelection,
            ["autoFormat"] = _autoFormat,
            ["autoAnalyze"] = _autoAnalyzeExceptions,
            ["cwd"] = EffectiveCwd(),
        });

        public void Dispose()
        {
            SelectionTracker.Changed -= OnSelectionChanged;
            DebugMonitor.BreakOnException -= OnExceptionBreak;
            Microsoft.VisualStudio.Shell.Events.SolutionEvents.OnAfterOpenSolution -= _onSolutionOpened;
            Microsoft.VisualStudio.Shell.Events.SolutionEvents.OnAfterOpenFolder -= _onFolderOpened;
            Microsoft.VisualStudio.Shell.Events.SolutionEvents.OnAfterCloseSolution -= _onSolutionClosed;
            StopProcess();
        }
    }
}
