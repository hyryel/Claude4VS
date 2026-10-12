using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
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
        // Outils VS sans effet sur l'exécution du programme : autorisés sans confirmation.
        // Démarrer/arrêter/continuer le débogage et évaluer une expression passent par la carte de permission.
        private static readonly string[] ReadOnlyVsTools =
        {
            "getDiagnostics", "getOpenEditors", "getWorkspaceFolders", "getCurrentSelection", "checkDocumentDirty",
            "build", "debugState", "debugLocals", "breakpointAdd", "breakpointRemove", "breakpointList", "getOutput",
            "runTests", "formatDocument", "bookmarkList", "bookmarkToggle",
            "findSymbol", "symbolInfo", "findReferences", "documentSymbols", "semanticDiagnostics", // renameSymbol demande confirmation
        };

        // Valeurs acceptées de l'interface (elles finissent sur la ligne de commande du CLI)
        private static readonly string[] PermissionModes = { "default", "acceptEdits", "plan", "bypassPermissions", "dontAsk" };
        private static readonly string[] EffortLevels = { "low", "medium", "high", "xhigh", "max" };
        private static readonly Regex ModelPattern = new Regex(@"^[A-Za-z0-9][A-Za-z0-9._:/\[\]-]{0,199}$");

        private const int HistoryBatchSize = 50;        // messages par lot envoyé au panneau
        private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Consigne ajoutée au prompt système : les outils de l'IDE sont moins coûteux et plus fiables que
        /// grep + lecture + édition fichier par fichier ; le modèle doit les préférer quand ils s'appliquent.
        /// </summary>
        private const string VsToolsGuidance =
            "You are running inside Visual Studio (ClaudeVS panel). The mcp__visualstudio__* tools operate the IDE itself and " +
            "are always available; prefer them over shell commands, grep and manual file edits whenever they apply, because they " +
            "are exact and cost far fewer tokens:\n" +
            "- Rename any C#/VB symbol (class, method, property, variable...) with renameSymbol: it updates every reference in the " +
            "solution like F2 in the IDE. Never rename by grep + Edit.\n" +
            "- Navigate code with findSymbol, symbolInfo (go to definition, signature, docs), findReferences (all usages) and " +
            "documentSymbols (file outline) instead of reading whole files or grepping.\n" +
            "- Check for errors with semanticDiagnostics (no build needed) or build; run tests with runTests.\n" +
            "- Run and debug the program with debugStart/debugContinue/debugStep/debugLocals/debugEvaluate/getOutput instead of " +
            "dotnet run; use debugWait when the program is still running.\n" +
            "- Format a file with formatDocument; manage bookmarks with bookmarkList/bookmarkToggle.\n" +
            "Files changed through the IDE (renameSymbol, formatDocument) are modified on disk by Visual Studio: re-read them " +
            "before editing them with Edit. The user's current editor selection is attached to messages as context.\n" +
            "This is a default, not a rule: when the user explicitly asks for another way (grep, manual edits, 'without the IDE " +
            "tools', dotnet CLI...), do exactly what the user asks.";

        private readonly ClaudeVSPackage _package;
        private readonly Action<JObject> _post;
        private readonly Dictionary<string, JObject> _pendingPermissions = new Dictionary<string, JObject>();
        private ClaudeProcess _claude;
        private string _model;            // null = modèle par défaut du CLI
        private string _effort;           // null = effort par défaut du modèle
        private readonly Dictionary<string, Action<JObject>> _pendingControl = new Dictionary<string, Action<JObject>>();
        // tool_use_id des outils d'édition vus dans les messages assistant du tour : seuls leurs résultats déclenchent le formatage auto
        private readonly HashSet<string> _editToolUses = new HashSet<string>();
        private string _permissionMode = "default";
        private bool _attachSelection = true;
        private bool _autoFormat;                 // Ctrl+K Ctrl+D après chaque fichier modifié par Claude
        private bool _autoAnalyzeExceptions = true; // arrêt sur exception dans VS → analyse demandée à Claude
        private JObject _lastExceptionState;
        private bool _busy;
        private bool _cliReady;                   // system/init reçu du processus courant
        private string _mcpConfigPath;            // fichier temporaire contenant le jeton du serveur MCP
        private int _restartSeq;                  // invalide un chargement d'historique dépassé par un autre Restart

        public ChatController(ClaudeVSPackage package, Action<JObject> post)
        {
            _package = package;
            _post = post;
            SelectionTracker.Changed += OnSelectionChanged;
            DebugMonitor.BreakOnException += OnExceptionBreak;
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
                    _package.Log.Error(name + " : " + ex);
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
                _package.Log.Error($"Message de l'interface ({msg["type"]}) : {ex}");
                PostError($"Erreur interne ({msg["type"]}) : {ex.Message}");
            }
        }

        private void DispatchUiMessage(JObject msg)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            switch ((string)msg["type"])
            {
                case "ready":
                    PostStatus(_claude?.IsRunning == true ? (_busy ? "busy" : "ready") : "idle");
                    PostSettings();
                    OnSelectionChanged(SelectionTracker.Latest);
                    // Interface rechargée (plantage WebView2) : les demandes de permission encore ouvertes sont réaffichées
                    foreach (var kv in _pendingPermissions)
                        _post(new JObject { ["type"] = "cli", ["msg"] = new JObject { ["type"] = "control_request", ["request_id"] = kv.Key, ["request"] = kv.Value } });
                    break;
                case "send":
                    Send((string)msg["text"], msg["images"] as JArray);
                    break;
                case "searchFiles":
                    SearchFiles((string)msg["query"], (int?)msg["seq"] ?? 0);
                    break;
                case "interrupt":
                    // Réponse consommée ici : un refus du CLI (tour déjà terminé) n'a pas à s'afficher en erreur
                    Control(new JObject { ["subtype"] = "interrupt" }, _ => { });
                    break;
                case "permission":
                    ResolvePermission((string)msg["requestId"], (JObject)msg["response"]);
                    break;
                case "showDiff":
                    var req = (string)msg["requestId"];
                    if (_pendingPermissions.TryGetValue(req, out var request)) ShowEditDiff(req, request);
                    break;
                case "setMode":
                {
                    var mode = (string)msg["mode"] ?? "default";
                    if (!PermissionModes.Contains(mode)) { RejectSetting("mode", mode); break; }
                    _permissionMode = mode;
                    TrySend(() => _claude?.SendControlRequest(new JObject { ["subtype"] = "set_permission_mode", ["mode"] = _permissionMode }));
                    PostSettings();
                    break;
                }
                case "setModel":
                {
                    var model = (string)msg["model"];
                    if (!string.IsNullOrEmpty(model) && !ModelPattern.IsMatch(model)) { RejectSetting("modèle", model); break; }
                    _model = string.IsNullOrEmpty(model) ? null : model;
                    TrySend(() => _claude?.SendControlRequest(new JObject { ["subtype"] = "set_model", ["model"] = _model }));
                    PostSettings();
                    break;
                }
                case "setEffort":
                {
                    var effort = (string)msg["effort"];
                    if (!string.IsNullOrEmpty(effort) && !EffortLevels.Contains(effort)) { RejectSetting("effort", effort); break; }
                    _effort = string.IsNullOrEmpty(effort) ? null : effort;
                    // À chaud : réglage de session (non persisté) ; au démarrage : --effort
                    if (_effort != null)
                        TrySend(() => _claude?.SendControlRequest(new JObject
                        {
                            ["subtype"] = "apply_flag_settings",
                            ["settings"] = new JObject { ["effortLevel"] = _effort },
                        }));
                    else if (_claude?.IsRunning == true)
                        // Le CLI n'a pas de requête pour revenir à l'effort par défaut : il garde le réglage précédent
                        PostNotice("Effort par défaut : s'appliquera à la prochaine conversation (la session en cours garde l'effort actuel).");
                    PostSettings();
                    break;
                }
                case "rewind":
                    Rewind((string)msg["uuid"], (bool?)msg["dryRun"] ?? false);
                    break;
                case "setAttachSelection":
                    _attachSelection = (bool?)msg["value"] ?? true;
                    PostSettings();
                    break;
                case "setAutoFormat":
                    _autoFormat = (bool?)msg["value"] ?? false;
                    PostSettings();
                    break;
                case "setAutoAnalyze":
                    _autoAnalyzeExceptions = (bool?)msg["value"] ?? true;
                    PostSettings();
                    break;
                case "analyzeException":
                    if (_lastExceptionState != null) Send(ExceptionPrompt(_lastExceptionState), null);
                    break;
                case "newChat":
                    Restart(null);
                    break;
                case "listSessions":
                    ListSessions();
                    break;
                case "resume":
                    Restart((string)msg["sessionId"]);
                    break;
                case "openFile":
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
            var context = _attachSelection ? SelectionContext(SelectionTracker.Latest) : null;
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

        private void HandleWriteFailure(ClaudeProcess source, Exception ex)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (source == null || source != _claude) return;
            _package.Log.Error("Envoi vers claude : " + ex);
            var stderr = source.StderrTail;
            PostError($"Envoi vers claude impossible : {ex.Message}{(string.IsNullOrWhiteSpace(stderr) ? "" : "\n" + stderr.Trim())}");
            StopProcess();
            PostStatus("idle");
        }

        private readonly FileIndex _files = new FileIndex();

        /// <summary>Autocomplétion des @-mentions ; seq permet à l'interface d'ignorer les réponses périmées.</summary>
        private void SearchFiles(string query, int seq)
        {
            var root = WorkingDirectory();
            RunOnUi(async () =>
            {
                var files = await _files.SearchAsync(root, query);
                _post(new JObject { ["type"] = "files", ["seq"] = seq, ["files"] = new JArray(files) });
            }, "files");
        }

        /// <summary>Insère une @-mention dans la zone de saisie (commande « Envoyer la sélection à Claude »).</summary>
        public void InsertMention(string path, int? lineStart, int? lineEnd)
        {
            var root = WorkingDirectory();
            var rel = path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase) ? path.Substring(root.Length + 1) : path;
            var mention = "@" + rel.Replace('\\', '/');
            if (lineStart.HasValue)
                mention += lineEnd.HasValue && lineEnd != lineStart ? $"#L{lineStart + 1}-{lineEnd + 1}" : $"#L{lineStart + 1}";
            _post(new JObject { ["type"] = "insert", ["text"] = mention + " " });
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

            var args = new List<string>(cmd.PrefixArgs)
            {
                "--output-format", "stream-json", "--verbose",
                "--input-format", "stream-json",
                "--include-partial-messages",
                "--permission-prompt-tool", "stdio",
                "--permission-mode", _permissionMode,
            };
            if (_model != null) args.AddRange(new[] { "--model", _model });
            if (_effort != null) args.AddRange(new[] { "--effort", _effort });
            if (resumeSessionId != null) args.AddRange(new[] { "--resume", resumeSessionId });
            // Le CLI renvoie nos messages avec leur uuid : nécessaire pour le retour arrière (rewind_files)
            args.Add("--replay-user-messages");
            var mcpConfig = WriteIdeMcpConfig();
            if (mcpConfig != null)
            {
                args.AddRange(new[] { "--mcp-config", mcpConfig });
                args.AddRange(new[] { "--append-system-prompt", VsToolsGuidance });
                // Outils VS en lecture seule : pas de demande de permission
                args.AddRange(new[] { "--allowedTools", string.Join(",", ReadOnlyVsTools.Select(t => "mcp__visualstudio__" + t)) });
            }

            ClaudeProcess claude = null;
            try
            {
                // Sauvegarde des fichiers avant chaque modification, pour le retour arrière
                var env = new Dictionary<string, string> { ["CLAUDE_CODE_ENABLE_SDK_FILE_CHECKPOINTING"] = "true" };
                _package.Log.Info("CLI : " + cmd);
                claude = new ClaudeProcess(cmd.FileName, WorkingDirectory(), args, env, _package.Log);
                claude.MessageReceived += m => OnCliMessage(claude, m);
                claude.Exited += (code, stderr) => OnCliExited(claude, code, stderr);
                claude.WriteFailed += ex => RunOnUi(() => HandleWriteFailure(claude, ex), "write");
                _claude = claude;
                _cliReady = false;
                claude.SendControlRequest(new JObject { ["subtype"] = "initialize" });
                PostStatus("starting");
                WatchStartup(claude);
                return true;
            }
            catch (Exception ex)
            {
                _package.Log.Error("Lancement de claude : " + ex);
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
                PostNotice($"claude ne répond pas depuis {StartupTimeout.TotalSeconds:0} s (initialisation en attente). "
                         + "Vérifiez le volet Sortie « Claude » ; « Nouvelle conversation » relance le processus.");
            }, "startup");
        }

        private void Restart(string resumeSessionId)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var seq = ++_restartSeq;
            StopProcess();
            _post(new JObject { ["type"] = "cleared" });
            PostStatus("idle"); // y compris pendant le chargement d'un historique : pas de « Claude travaille… » résiduel
            if (resumeSessionId == null) return;

            var cwd = WorkingDirectory();
            RunOnUi(async () =>
            {
                // Lecture du .jsonl hors thread UI : les sessions font facilement plusieurs dizaines de Mo
                var messages = await Task.Run(() => SessionStore.Load(cwd, resumeSessionId, _package.Log));
                if (seq != _restartSeq) return; // une autre conversation a été lancée entre-temps
                // Par lots, pour ne pas sérialiser un seul message WebView géant ; « last » marque le dernier
                for (var i = 0; i < messages.Count || i == 0; i += HistoryBatchSize)
                {
                    var batch = new JArray(messages.Skip(i).Take(HistoryBatchSize));
                    var last = i + HistoryBatchSize >= messages.Count;
                    _post(new JObject { ["type"] = "history", ["messages"] = batch, ["last"] = last });
                }
                if (_claude != null)
                {
                    // Un message envoyé pendant le chargement a déjà lancé un processus, sans --resume
                    PostNotice("Un message a été envoyé pendant le chargement : la conversation affichée n'a pas été reprise par le CLI. Rouvrez-la depuis l'historique pour la poursuivre.");
                    return;
                }
                if (!EnsureStarted(resumeSessionId)) PostStatus("idle");
            }, "resume");
        }

        private void ListSessions()
        {
            var cwd = WorkingDirectory();
            RunOnUi(async () =>
            {
                var sessions = await Task.Run(() => SessionStore.List(cwd, _package.Log));
                _post(new JObject { ["type"] = "sessions", ["sessions"] = sessions });
            }, "sessions");
        }

        private void StopProcess()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var old = _claude;
            _claude = null;
            _busy = false;
            _cliReady = false;
            ClearPendingPermissions();
            _pendingControl.Clear();
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
            var config = new JObject
            {
                ["mcpServers"] = new JObject
                {
                    ["visualstudio"] = new JObject
                    {
                        ["type"] = "ws",
                        ["url"] = $"ws://127.0.0.1:{server.Port}/tools",
                        ["headers"] = new JObject { ["x-claude-code-ide-authorization"] = server.AuthToken },
                        // Sans cela, les outils sont « différés » : le modèle ne les voit qu'après un ToolSearch, et ne les utilise pas spontanément
                        ["alwaysLoad"] = true,
                    },
                },
            };
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
            catch (Exception ex) { _package.Log.Info($"Suppression de {path} : {ex.Message}"); }
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
                    _pendingPermissions[id] = request;
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
                    if (id != null) CancelPermission(id);
                }
                else if (type == "control_response")
                {
                    var response = msg["response"] as JObject;
                    var id = (string)response?["request_id"];
                    if (!_busy) PostStatus("ready");
                    if (id != null && _pendingControl.TryGetValue(id, out var handler))
                    {
                        _pendingControl.Remove(id);
                        // Le gestionnaire produit son propre message vers l'interface (rewindPreview, context…) :
                        // pas de relais brut, sinon une erreur s'afficherait deux fois
                        handler(response);
                        return;
                    }
                }
                else if (type == "system" && (string)msg["subtype"] == "init")
                {
                    _cliReady = true;
                    if (!_busy) PostStatus("ready");
                    RequestContextUsage();
                }
                else if (type == "stream_event" && !_busy)
                {
                    // Tour démarré par un message mis en file (ex. consigne après un refus)
                    _busy = true;
                    PostStatus("busy");
                }
                else if (type == "result")
                {
                    _busy = false;
                    // Les permissions encore ouvertes ne recevront plus de réponse du CLI
                    ClearPendingPermissions();
                    _editToolUses.Clear();
                    PostStatus("ready");
                    RequestContextUsage();
                }
                else if (type == "assistant")
                {
                    TrackEditToolUses(msg["message"]?["content"] as JArray);
                }
                else if (type == "user" && msg["tool_use_result"]?["filePath"] is JValue fp)
                {
                    // Fichier écrit par Claude (Edit/Write/MultiEdit) : passage de Mettre en forme le document
                    var results = msg["message"]?["content"] as JArray;
                    var block = results?.FirstOrDefault(c => (string)c["type"] == "tool_result");
                    var toolUseId = (string)block?["tool_use_id"];
                    if (toolUseId != null && _editToolUses.Remove(toolUseId) && _autoFormat && (bool?)block["is_error"] != true)
                        AutoFormat((string)fp);
                }

                // Relais avant tout traitement faillible : la carte de permission doit s'afficher même si le diff échoue
                _post(new JObject { ["type"] = "cli", ["msg"] = msg });
                if (editRequestId != null) ShowEditDiff(editRequestId, _pendingPermissions[editRequestId]);
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
                var r = _package.Tools?.Editor.FormatDocument(path, closeIfNotOpen: true);
                if (r != null && (bool?)r["changed"] == true) PostNotice("✨ Formaté : " + Path.GetFileName(path));
            }
            catch (Exception ex) { _package.Log.Info($"Formatage auto de {path} : {ex.Message}"); }
        }

        // ---- Événements du débogueur -----------------------------------------------------------

        private void OnExceptionBreak(JObject state)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _lastExceptionState = state;
            var auto = _autoAnalyzeExceptions && _claude?.IsRunning == true && !_busy;
            _post(new JObject { ["type"] = "debugEvent", ["state"] = state, ["auto"] = auto });
            if (auto) Send(ExceptionPrompt(state), null);
        }

        private static string ExceptionPrompt(JObject state)
        {
            // Première trame avec un fichier source (la tête de pile est souvent dans le framework)
            var loc = (state["callStack"] as JArray)?.FirstOrDefault(f => f["file"] != null) ?? state["location"];
            var ex = state["exception"];
            var sb = new System.Text.StringBuilder();
            sb.Append("[Événement Visual Studio] Le débogueur s'est arrêté sur une exception (")
              .Append((string)state["breakReason"]).Append(')');
            if (ex != null) sb.Append(" : ").Append((string)ex["type"]).Append(" — ").Append((string)ex["value"]);
            sb.Append(".\n");
            if (loc != null) sb.Append($"Position : {loc["file"]}:{loc["line"]} dans {loc["function"]}.\n");
            if (state["callStack"] is JArray frames && frames.Count > 0)
            {
                sb.Append("Pile :\n");
                foreach (var f in frames)
                    sb.Append("- ").Append((string)f["function"])
                      .Append(f["file"] != null ? $" ({f["file"]}:{f["line"]})" : "").Append('\n');
            }
            sb.Append("Analyse la cause avec les outils mcp__visualstudio__ (debugState, debugLocals, debugEvaluate si utile), "
                    + "explique-la et propose une correction. Ne continue ni n'arrête l'exécution sans me le demander.");
            return sb.ToString();
        }

        /// <summary>Requête de contrôle dont la réponse est attendue (get_context_usage, rewind_files…). Faux si le CLI ne tourne pas ou si l'envoi a échoué.</summary>
        private bool Control(JObject request, Action<JObject> onResponse)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_claude?.IsRunning != true) return false;
            string id = null;
            if (!TrySend(() => id = _claude.SendControlRequest(request))) return false;
            _pendingControl[id] = onResponse;
            return true;
        }

        /// <summary>Remplissage du contexte : catégories renvoyées par le CLI, résumées en « utilisé / total ».</summary>
        private void RequestContextUsage()
        {
            Control(new JObject { ["subtype"] = "get_context_usage" }, r =>
            {
                var categories = r["response"]?["categories"] as JArray;
                if (categories == null) return;
                long total = 0, used = 0;
                var details = new JArray();
                foreach (var c in categories)
                {
                    var tokens = (long?)c["tokens"] ?? 0;
                    var kind = (string)c["kind"];
                    total += tokens;
                    if (kind == "used" || kind == "deferred") used += tokens;
                    if (kind != "free") details.Add(new JObject { ["name"] = c["name"], ["tokens"] = tokens });
                }
                _post(new JObject { ["type"] = "context", ["used"] = used, ["total"] = total, ["details"] = details });
            });
        }

        /// <summary>Retour arrière des fichiers à l'état précédant un message utilisateur (dry run = aperçu).</summary>
        private void Rewind(string uuid, bool dryRun)
        {
            if (string.IsNullOrEmpty(uuid)) return;
            var request = new JObject { ["subtype"] = "rewind_files", ["user_message_id"] = uuid };
            if (dryRun) request["dry_run"] = true;
            var sent = Control(request, r =>
            {
                var resp = r["response"] as JObject ?? new JObject();
                var error = (string)r["error"] ?? (string)resp["error"];
                _post(new JObject
                {
                    ["type"] = dryRun ? "rewindPreview" : "rewindDone",
                    ["uuid"] = uuid,
                    ["ok"] = (string)r["subtype"] == "success" && error == null,
                    ["error"] = error,
                    ["canRewind"] = resp["canRewind"],
                    ["files"] = resp["filesChanged"] ?? new JArray(),
                    ["insertions"] = resp["insertions"],
                    ["deletions"] = resp["deletions"],
                });
                if (!dryRun) _package.Log.Info($"Retour arrière des fichiers ({uuid}) : {(error ?? "ok")}");
            });
            // Sans processus, la carte de retour arrière explique pourquoi au lieu d'un bouton muet
            if (!sent)
                _post(new JObject
                {
                    ["type"] = dryRun ? "rewindPreview" : "rewindDone", ["uuid"] = uuid, ["ok"] = false,
                    ["error"] = "claude n'est pas démarré : reprenez la conversation (Reprendre) pour restaurer les fichiers.",
                });
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
                    PostError($"claude s'est arrêté (code {code}).{(string.IsNullOrWhiteSpace(stderr) ? "" : "\n" + stderr.Trim())}");
                else if (wasBusy)
                    PostNotice("claude s'est arrêté avant de terminer sa réponse. Envoyez un nouveau message pour le relancer.");
            }, "exit");
        }

        // ---- Permissions ----------------------------------------------------------------------

        private void ResolvePermission(string requestId, JObject response)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (requestId == null || response == null || !_pendingPermissions.TryGetValue(requestId, out var request)) return;

            _package.Log.Info($"→ permission {requestId} : {response["behavior"]}");
            response["toolUseID"] = request["tool_use_id"];
            if ((string)response["behavior"] == "allow" && response["updatedInput"] == null)
                response["updatedInput"] = request["input"];
            // En cas d'échec d'envoi, la demande est encore en attente : HandleWriteFailure la marque « cancelled » avec les autres
            if (!TrySend(() => _claude?.SendControlResponse(requestId, response))) return;
            _pendingPermissions.Remove(requestId);
            DiffManager.GetInstance(_package).Close(DiffTab(requestId));
            _post(new JObject { ["type"] = "permissionResolved", ["requestId"] = requestId, ["behavior"] = response["behavior"] });
        }

        /// <summary>Demande abandonnée (processus arrêté, tour terminé, annulation du CLI) : ferme le diff et expire la carte.</summary>
        private void CancelPermission(string requestId)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!_pendingPermissions.Remove(requestId)) return;
            DiffManager.GetInstance(_package).Close(DiffTab(requestId));
            _post(new JObject { ["type"] = "permissionResolved", ["requestId"] = requestId, ["behavior"] = "cancelled" });
        }

        private void ClearPendingPermissions()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            foreach (var id in _pendingPermissions.Keys.ToList()) CancelPermission(id);
        }

        private static string DiffTab(string requestId) => "claude-perm-" + requestId;

        /// <summary>
        /// Ouvre la modification proposée dans le diff natif ; Accepter/Refuser y répond à la permission.
        /// Un échec (fichier illisible, old_string introuvable…) est signalé : la carte du chat reste utilisable.
        /// </summary>
        private void ShowEditDiff(string requestId, JObject request)
        {
            var input = request["input"] as JObject;
            var tool = (string)request["tool_name"];
            var path = (string)input?["file_path"];
            if (string.IsNullOrEmpty(path)) return;

            RunOnUi(async () =>
            {
                string original, proposed;
                try
                {
                    // Le chemin vient du modèle (UNC, fichier verrouillé…) : lecture hors thread UI
                    original = File.Exists(path) ? await Task.Run(() => File.ReadAllText(path)) : "";
                    proposed = ApplyEdit(tool, input, original);
                }
                catch (Exception ex)
                {
                    _package.Log.Info($"Diff indisponible pour {path} : {ex.Message}");
                    PostNotice($"Diff indisponible pour {Path.GetFileName(path)} : {ex.Message} — répondez depuis la carte du chat.");
                    return;
                }
                if (!_pendingPermissions.ContainsKey(requestId)) return; // décidé ou abandonné pendant la lecture

                var (accepted, text) = await DiffManager.GetInstance(_package)
                    .ShowProposalAsync(path, path, proposed, DiffTab(requestId));
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (accepted == null || !_pendingPermissions.ContainsKey(requestId)) return; // onglet fermé ou décidé dans le chat

                JObject response;
                if (accepted == true)
                {
                    var updated = (JObject)input.DeepClone();
                    if (text != null && text != proposed)
                    {
                        // Retouches de l'utilisateur dans le diff : on les transmet telles quelles, en une seule
                        // substitution du contenu actuel (old_string vide = création, comme le fait le CLI)
                        switch (tool)
                        {
                            case "Write":
                                updated["content"] = text;
                                break;
                            case "Edit":
                                updated = new JObject { ["file_path"] = path, ["old_string"] = original, ["new_string"] = text, ["replace_all"] = false };
                                break;
                            case "MultiEdit":
                                updated = new JObject { ["file_path"] = path, ["edits"] = new JArray(new JObject { ["old_string"] = original, ["new_string"] = text }) };
                                break;
                        }
                    }
                    response = new JObject { ["behavior"] = "allow", ["updatedInput"] = updated };
                }
                else
                    response = new JObject { ["behavior"] = "deny", ["message"] = "L'utilisateur a refusé la modification dans le diff de Visual Studio." };
                ResolvePermission(requestId, response);
            }, "diff");
        }

        private static string ApplyEdit(string tool, JObject input, string original)
        {
            switch (tool)
            {
                case "Write":
                    return (string)input["content"] ?? "";
                case "Edit":
                    return Replace(original, (string)input["old_string"], (string)input["new_string"], (bool?)input["replace_all"] ?? false);
                case "MultiEdit":
                    var text = original;
                    foreach (var e in (JArray)input["edits"] ?? new JArray())
                        text = Replace(text, (string)e["old_string"], (string)e["new_string"], (bool?)e["replace_all"] ?? false);
                    return text;
                default:
                    throw new NotSupportedException(tool);
            }
        }

        private static string Replace(string text, string oldString, string newString, bool all)
        {
            if (string.IsNullOrEmpty(oldString)) return newString ?? "";
            newString = newString ?? "";
            var idx = text.IndexOf(oldString, StringComparison.Ordinal);
            if (idx < 0 && text.Contains("\r\n"))
            {
                // Le modèle écrit souvent en \n alors que le fichier est en \r\n
                oldString = oldString.Replace("\r\n", "\n").Replace("\n", "\r\n");
                newString = newString.Replace("\r\n", "\n").Replace("\n", "\r\n");
                idx = text.IndexOf(oldString, StringComparison.Ordinal);
            }
            if (idx < 0) throw new InvalidOperationException("old_string introuvable");
            return all ? text.Replace(oldString, newString) : text.Substring(0, idx) + newString + text.Substring(idx + oldString.Length);
        }

        // ---- Contexte IDE -----------------------------------------------------------------------

        private void OnSelectionChanged(JObject sel)
        {
            if (sel == null) { _post(new JObject { ["type"] = "selection", ["label"] = null }); return; }
            var file = Path.GetFileName((string)sel["filePath"]);
            var s = sel["selection"];
            var label = (bool)s["isEmpty"]
                ? file
                : (int)s["start"]["line"] == (int)s["end"]["line"]
                    ? $"{file}:{(int)s["start"]["line"] + 1}"
                    : $"{file}:{(int)s["start"]["line"] + 1}-{(int)s["end"]["line"] + 1}";
            _post(new JObject { ["type"] = "selection", ["label"] = label });
        }

        /// <summary>Même format que l'extension VS Code (ide_selection / ide_opened_file).</summary>
        private static string SelectionContext(JObject sel)
        {
            if (sel == null) return null;
            var path = (string)sel["filePath"];
            var s = sel["selection"];
            if ((bool)s["isEmpty"] || string.IsNullOrEmpty((string)sel["text"]))
                return $"<ide_opened_file>The user opened the file {path} in the IDE. This may or may not be related to the current task.</ide_opened_file>";
            return $"<ide_selection>The user selected the lines {(int)s["start"]["line"] + 1} to {(int)s["end"]["line"] + 1} from {path}:\n{sel["text"]}\n\nThis may or may not be related to the current task.</ide_selection>";
        }

        private static string WorkingDirectory()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (Package.GetGlobalService(typeof(SVsSolution)) is IVsSolution sln
                && sln.GetSolutionInfo(out var dir, out _, out _) == 0 && !string.IsNullOrEmpty(dir))
                return dir.TrimEnd('\\');
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        private void OpenFile(string path, int? line)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (string.IsNullOrEmpty(path)) return;
            if (!Path.IsPathRooted(path)) path = Path.Combine(WorkingDirectory(), path);
            if (!File.Exists(path)) return;
            VsShellUtilities.OpenDocument(_package, path, Guid.Empty, out _, out _, out var frame, out var view);
            frame?.Show();
            if (line.HasValue && view != null) view.SetCaretPos(Math.Max(0, line.Value - 1), 0);
        }

        // ---- Vers l'interface ---------------------------------------------------------------------

        private void PostStatus(string state) => _post(new JObject { ["type"] = "status", ["state"] = state });
        private void PostError(string text) => _post(new JObject { ["type"] = "error", ["text"] = text });
        private void PostNotice(string text) => _post(new JObject { ["type"] = "notice", ["text"] = text });

        private void PostSettings() => _post(new JObject
        {
            ["type"] = "settings",
            ["model"] = _model ?? "",
            ["effort"] = _effort ?? "",
            ["mode"] = _permissionMode,
            ["attachSelection"] = _attachSelection,
            ["autoFormat"] = _autoFormat,
            ["autoAnalyze"] = _autoAnalyzeExceptions,
            ["cwd"] = WorkingDirectory(),
        });

        public void Dispose()
        {
            SelectionTracker.Changed -= OnSelectionChanged;
            DebugMonitor.BreakOnException -= OnExceptionBreak;
            StopProcess();
        }
    }

    /// <summary>Conversations enregistrées par le CLI dans ~/.claude/projects/&lt;dossier encodé&gt;/*.jsonl.</summary>
    internal static class SessionStore
    {
        private const int MaxToolResultChars = 20000;

        private static string ProjectDir(string cwd)
        {
            var config = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            if (string.IsNullOrEmpty(config))
                config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
            return Path.Combine(config, "projects", Regex.Replace(cwd, "[^A-Za-z0-9]", "-"));
        }

        public static JArray List(string cwd, Log log)
        {
            var result = new JArray();
            var dir = ProjectDir(cwd);
            if (!Directory.Exists(dir)) return result;
            foreach (var file in new DirectoryInfo(dir).GetFiles("*.jsonl").OrderByDescending(f => f.LastWriteTimeUtc).Take(30))
            {
                var title = FirstPrompt(file.FullName, log);
                if (title == null) continue;
                result.Add(new JObject
                {
                    ["id"] = Path.GetFileNameWithoutExtension(file.Name),
                    ["title"] = title,
                    ["updated"] = file.LastWriteTime.ToString("dd/MM HH:mm"),
                });
            }
            return result;
        }

        private static string FirstPrompt(string file, Log log)
        {
            try
            {
                foreach (var line in ReadLines(file).Take(200))
                {
                    var o = TryParse(line);
                    if ((string)o?["type"] != "user" || (bool?)o["isMeta"] == true || (bool?)o["isSidechain"] == true) continue;
                    var text = UserText(o["message"]?["content"]);
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    text = text.Trim().Replace("\r", " ").Replace("\n", " ");
                    return text.Length > 90 ? text.Substring(0, 90) + "…" : text;
                }
            }
            catch (Exception ex) { log?.Info($"Session illisible {Path.GetFileName(file)} : {ex.Message}"); }
            return null;
        }

        /// <summary>Texte saisi par l'utilisateur, sans les blocs de contexte IDE ni les résultats d'outils.</summary>
        private static string UserText(JToken content)
        {
            if (content is JValue v) return Clean((string)v);
            if (content is JArray arr)
                return string.Join("\n", arr.Where(b => (string)b["type"] == "text").Select(b => Clean((string)b["text"])).Where(t => !string.IsNullOrWhiteSpace(t)));
            return null;
        }

        private static string Clean(string t)
        {
            if (t == null) return null;
            if (t.StartsWith("<ide_") || t.StartsWith("<command-") || t.StartsWith("<local-command") || t.StartsWith("<system-reminder")) return null;
            return t;
        }

        /// <summary>Messages user/assistant de la session, résultats d'outils volumineux tronqués (le panneau n'en affiche que 4 000 caractères).</summary>
        public static JArray Load(string cwd, string sessionId, Log log)
        {
            var result = new JArray();
            var file = Path.Combine(ProjectDir(cwd), sessionId + ".jsonl");
            if (!File.Exists(file)) return result;
            try
            {
                foreach (var line in ReadLines(file))
                {
                    var o = TryParse(line);
                    var type = (string)o?["type"];
                    if ((type != "user" && type != "assistant") || (bool?)o["isSidechain"] == true || (bool?)o["isMeta"] == true) continue;
                    if (type == "user") TruncateToolResults(o["message"]?["content"] as JArray);
                    result.Add(new JObject { ["type"] = type, ["uuid"] = o["uuid"], ["message"] = o["message"] });
                }
            }
            catch (Exception ex) { log?.Error($"Lecture de la session {sessionId} : {ex.Message}"); }
            return result;
        }

        private static void TruncateToolResults(JArray content)
        {
            if (content == null) return;
            foreach (var block in content.OfType<JObject>())
            {
                if ((string)block["type"] != "tool_result") continue;
                if (block["content"] is JValue v && v.Type == JTokenType.String)
                    block["content"] = Truncate((string)v);
                else if (block["content"] is JArray parts)
                    foreach (var p in parts.OfType<JObject>())
                        if ((string)p["type"] == "text" && p["text"] is JValue t && t.Type == JTokenType.String) p["text"] = Truncate((string)t);
            }
        }

        private static string Truncate(string text) =>
            text != null && text.Length > MaxToolResultChars ? text.Substring(0, MaxToolResultChars) + "\n… [tronqué]" : text;

        private static IEnumerable<string> ReadLines(string file)
        {
            // Le CLI peut être en train d'écrire : partage en lecture/écriture
            using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(fs))
            {
                string line;
                while ((line = sr.ReadLine()) != null) yield return line;
            }
        }

        private static JObject TryParse(string line)
        {
            try { return JObject.Parse(line); } catch { return null; }
        }
    }
}
