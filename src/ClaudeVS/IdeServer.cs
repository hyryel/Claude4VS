using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Fleck;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ClaudeVS
{
    /// <summary>
    /// Serveur MCP (JSON-RPC 2.0 sur WebSocket, 127.0.0.1) découvert par le CLI Claude Code
    /// via ~/.claude/ide/&lt;port&gt;.lock — même protocole que l'extension VS Code.
    /// </summary>
    internal sealed class IdeServer : IDisposable
    {
        private readonly IdeTools _tools;
        private readonly Log _log;
        private readonly string _authToken = Guid.NewGuid().ToString();
        private readonly object _gate = new object();
        private WebSocketServer _server;
        private IWebSocketConnection _client;
        // Connexions authentifiées (jeton vérifié), avec une source d'annulation déclenchée à leur fermeture
        private readonly Dictionary<IWebSocketConnection, CancellationTokenSource> _connections = new Dictionary<IWebSocketConnection, CancellationTokenSource>();
        private string[] _workspaceFolders = new string[0];
        private JObject _lastSelection;

        public int Port { get; private set; }

        /// <summary>Un CLI interactif (terminal) est connecté au pont IDE.</summary>
        public bool HasClient { get { lock (_gate) return _client != null; } }
        public string AuthToken => _authToken;

        public IdeServer(IdeTools tools, Log log)
        {
            _tools = tools;
            _log = log;
            FleckLog.LogAction = (level, msg, ex) => { if (level >= LogLevel.Warn) _log.Info($"[ws] {msg} {ex?.Message}"); };
        }

        public void Start(string[] workspaceFolders)
        {
            _workspaceFolders = workspaceFolders;
            RemoveStaleLockFiles();
            var rnd = new Random();
            for (var attempt = 0; attempt < 50; attempt++)
            {
                var port = rnd.Next(10000, 65535);
                try
                {
                    var server = new WebSocketServer($"ws://127.0.0.1:{port}")
                    {
                        // Le CLI demande le sous-protocole "mcp" et exige qu'il soit renvoyé
                        SupportedSubProtocols = new[] { "mcp" },
                    };
                    server.Start(OnConnection);
                    _server = server;
                    Port = port;
                    break;
                }
                catch (Exception ex)
                {
                    _log.Info($"Port {port} indisponible : {ex.Message}");
                }
            }
            if (_server == null)
                throw new InvalidOperationException("Aucun port disponible pour le serveur MCP.");
            WriteLockFile();
            // VS ne libère pas toujours le package à la fermeture : on retire aussi le verrou à la sortie du processus
            AppDomain.CurrentDomain.ProcessExit += (s, e) => DeleteLockFile();
        }

        private void DeleteLockFile()
        {
            try { File.Delete(LockFilePath); } catch { }
        }

        public void UpdateWorkspace(string[] workspaceFolders)
        {
            _workspaceFolders = workspaceFolders;
            WriteLockFile();
            _log.Info("Lock file mis à jour : " + string.Join(", ", workspaceFolders));
        }

        // ---- Lock file -------------------------------------------------------------------

        private static string IdeDir
        {
            get
            {
                var config = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
                if (string.IsNullOrEmpty(config))
                    config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
                return Path.Combine(config, "ide");
            }
        }

        private string LockFilePath => Path.Combine(IdeDir, $"{Port}.lock");

        /// <summary>Fichiers de verrou laissés par des instances de VS fermées brutalement.</summary>
        private void RemoveStaleLockFiles()
        {
            if (!Directory.Exists(IdeDir)) return;
            foreach (var file in Directory.GetFiles(IdeDir, "*.lock"))
            {
                try
                {
                    var lockInfo = JObject.Parse(File.ReadAllText(file));
                    if ((string)lockInfo["ideName"] != "Visual Studio") continue;
                    var pid = (int?)lockInfo["pid"] ?? 0;
                    if (pid > 0 && IsDevenvAlive(pid)) continue;
                    File.Delete(file);
                    _log.Info("Fichier de verrou périmé supprimé : " + Path.GetFileName(file));
                }
                catch (Exception ex) { _log.Info($"Fichier de verrou illisible {Path.GetFileName(file)} : {ex.Message}"); }
            }
        }

        /// <summary>
        /// Le PID du verrou désigne-t-il encore un Visual Studio ? Un PID réutilisé par un autre programme, ou
        /// par un processus d'un autre utilisateur (accès refusé), n'est pas le nôtre : le verrou est périmé.
        /// </summary>
        private bool IsDevenvAlive(int pid)
        {
            try
            {
                using (var process = Process.GetProcessById(pid))
                    return string.Equals(process.ProcessName, "devenv", StringComparison.OrdinalIgnoreCase);
            }
            catch (ArgumentException) { return false; } // aucun processus avec ce PID
            catch (InvalidOperationException) { return false; } // terminé entre-temps
            catch (Win32Exception ex)
            {
                _log.Info($"Processus {pid} inaccessible ({ex.Message}) : verrou considéré périmé.");
                return false;
            }
        }

        private void WriteLockFile()
        {
            Directory.CreateDirectory(IdeDir);
            var content = new JObject
            {
                ["pid"] = Process.GetCurrentProcess().Id,
                ["workspaceFolders"] = new JArray(_workspaceFolders),
                ["ideName"] = "Visual Studio",
                ["transport"] = "ws",
                ["runningInWindows"] = true,
                ["authToken"] = _authToken,
            };
            File.WriteAllText(LockFilePath, content.ToString(Formatting.None));
        }

        // ---- WebSocket / JSON-RPC -------------------------------------------------------

        private void OnConnection(IWebSocketConnection socket)
        {
            socket.OnOpen = () =>
            {
                if (!Authenticate(socket, out _))
                {
                    _log.Error("Connexion WebSocket refusée (jeton invalide).");
                    socket.Close();
                    return;
                }
                if (IsToolsClient(socket))
                {
                    // Client « outils » (CLI du panneau de chat) : plusieurs autorisés, pas de notifications
                    _log.Info("Serveur MCP visualstudio connecté (panneau).");
                    return;
                }
                lock (_gate)
                {
                    // Un seul client IDE à la fois, comme VS Code
                    if (_client != null && _client != socket)
                    {
                        _log.Info("Déconnexion du client précédent.");
                        try { _client.Close(); } catch { }
                    }
                    _client = socket;
                }
                _log.Info("Claude Code connecté.");
            };
            socket.OnClose = () =>
            {
                CancellationTokenSource cts;
                lock (_gate)
                {
                    if (_connections.TryGetValue(socket, out cts)) _connections.Remove(socket);
                    if (_client == socket)
                    {
                        _client = null;
                        _log.Info("Claude Code déconnecté.");
                    }
                }
                // Les outils encore en cours pour cette connexion (Roslyn…) n'ont plus de destinataire
                if (cts != null) try { cts.Cancel(); cts.Dispose(); } catch { }
            };
            socket.OnMessage = message => _ = HandleMessageSafeAsync(socket, message);
        }

        /// <summary>
        /// Vérifie le jeton de la connexion et mémorise le résultat. Dans Fleck, OnOpen est appelé dans le
        /// rappel d'envoi du handshake pendant que la lecture continue : un message peut être traité avant
        /// (ou sans) OnOpen, d'où la vérification à chaque message et non seulement à l'ouverture.
        /// </summary>
        private bool Authenticate(IWebSocketConnection socket, out CancellationToken ct)
        {
            lock (_gate)
            {
                if (_connections.TryGetValue(socket, out var known)) { ct = known.Token; return true; }
            }
            ct = CancellationToken.None;
            if (!socket.IsAvailable) return false; // déjà fermée : ne pas la réinscrire après OnClose
            var token = socket.ConnectionInfo.Headers
                .FirstOrDefault(h => string.Equals(h.Key, "x-claude-code-ide-authorization", StringComparison.OrdinalIgnoreCase)).Value;
            if (token != _authToken) return false;
            lock (_gate)
            {
                if (!socket.IsAvailable) return false; // fermée entre-temps : OnClose a déjà pu passer, ne pas réinscrire
                if (!_connections.TryGetValue(socket, out var cts)) _connections[socket] = cts = new CancellationTokenSource();
                ct = cts.Token;
            }
            return true;
        }

        /// <summary>Les fautes d'une tâche « fire and forget » ne seraient observées par personne : on les journalise.</summary>
        private async Task HandleMessageSafeAsync(IWebSocketConnection socket, string message)
        {
            try { await HandleMessageAsync(socket, message); }
            catch (Exception ex) { _log.Error("Traitement d'un message WebSocket : " + ex); }
        }

        private async Task HandleMessageAsync(IWebSocketConnection socket, string message)
        {
            if (!Authenticate(socket, out var ct))
            {
                _log.Error("Message reçu sur une connexion non authentifiée : fermeture.");
                try { socket.Close(); } catch { }
                return;
            }
            JObject request;
            try { request = JObject.Parse(message); }
            catch (Exception ex) { _log.Error("JSON invalide : " + ex.Message); return; }

            var method = (string)request["method"];
            var id = request["id"];
            if (method == null) return; // réponse à une de nos requêtes : ignorée

            if (id == null)
            {
                // Notification (notifications/initialized, ide_connected…)
                if (method == "notifications/initialized" || method == "ide_connected")
                {
                    _log.Info($"← {method}");
                    if (_lastSelection != null) Notify("selection_changed", _lastSelection);
                }
                return;
            }

            JObject response;
            try
            {
                var result = await DispatchAsync(method, request["params"] as JObject ?? new JObject(), IsToolsClient(socket), ct);
                response = new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
            }
            catch (MethodNotFoundException)
            {
                response = Error(id, -32601, "Method not found: " + method);
            }
            catch (Exception ex)
            {
                _log.Error($"{method} : {ex.Message}");
                // Erreur d'outil MCP : renvoyée comme résultat isError, pas comme erreur JSON-RPC
                if (method == "tools/call")
                    response = new JObject
                    {
                        ["jsonrpc"] = "2.0", ["id"] = id,
                        ["result"] = new JObject { ["content"] = IdeTools.Text(ex.Message), ["isError"] = true },
                    };
                else
                    response = Error(id, -32603, ex.Message);
            }
            Send(socket, response);
        }

        /// <summary>
        /// Chemin /tools : le même serveur exposé comme serveur MCP ordinaire au CLI du panneau,
        /// limité aux outils utiles au modèle (pas de diff ni de gestion d'onglets).
        /// </summary>
        private static bool IsToolsClient(IWebSocketConnection socket) =>
            socket.ConnectionInfo.Path?.TrimEnd('/').EndsWith("/tools", StringComparison.OrdinalIgnoreCase) == true;

        private static readonly HashSet<string> ModelTools = new HashSet<string>(DebugTools.Names.Concat(EditorTools.Names).Concat(RoslynTools.Names))
        {
            "getDiagnostics", "getOpenEditors", "getWorkspaceFolders", "getCurrentSelection", "openFile", "checkDocumentDirty", "saveDocument",
        };

        private async Task<JToken> DispatchAsync(string method, JObject p, bool toolsOnly, CancellationToken ct)
        {
            switch (method)
            {
                case "initialize":
                    _log.Info($"← initialize ({p["clientInfo"]?["name"]} {p["clientInfo"]?["version"]})");
                    return new JObject
                    {
                        ["protocolVersion"] = (string)p["protocolVersion"] ?? "2025-03-26",
                        ["capabilities"] = new JObject { ["tools"] = new JObject { ["listChanged"] = true } },
                        ["serverInfo"] = new JObject { ["name"] = "Claude Code Visual Studio MCP", ["version"] = "0.1.0" },
                    };
                case "ping":
                    return new JObject();
                case "tools/list":
                    var tools = IdeTools.ToolDefinitions();
                    if (toolsOnly) tools = new JArray(tools.Where(t => ModelTools.Contains((string)t["name"])));
                    return new JObject { ["tools"] = tools };
                case "tools/call":
                    var name = (string)p["name"];
                    _log.Info($"← tools/call {name}{(toolsOnly ? " (panneau)" : "")}");
                    if (toolsOnly && !ModelTools.Contains(name)) throw new InvalidOperationException("Unknown tool: " + name);
                    return await _tools.CallAsync(name, p["arguments"] as JObject ?? new JObject(), ct);
                case "resources/list":
                    return new JObject { ["resources"] = new JArray() };
                case "prompts/list":
                    return new JObject { ["prompts"] = new JArray() };
                default:
                    throw new MethodNotFoundException();
            }
        }

        public void Notify(string method, JObject @params)
        {
            if (method == "selection_changed") _lastSelection = @params;
            IWebSocketConnection client;
            lock (_gate) client = _client;
            if (client == null) return;
            Send(client, new JObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = @params });
        }

        private void Send(IWebSocketConnection socket, JObject payload)
        {
            try { _ = socket.Send(payload.ToString(Formatting.None)); }
            catch (Exception ex) { _log.Error("Envoi impossible : " + ex.Message); }
        }

        private static JObject Error(JToken id, int code, string message) =>
            new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JObject { ["code"] = code, ["message"] = message } };

        private sealed class MethodNotFoundException : Exception { }

        public void Dispose()
        {
            _log.Info("Arrêt du serveur MCP.");
            DeleteLockFile();
            List<CancellationTokenSource> pending;
            lock (_gate) { pending = _connections.Values.ToList(); _connections.Clear(); }
            foreach (var cts in pending) try { cts.Cancel(); cts.Dispose(); } catch { }
            try { _client?.Close(); } catch { }
            try { _server?.Dispose(); } catch { }
        }
    }
}
