using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;
using Task = System.Threading.Tasks.Task;
#nullable enable

namespace ClaudeVS.Chat
{
    /// <summary>
    /// Demandes de permission (can_use_tool) en attente : cartes du chat, diff natif des modifications proposées,
    /// réponse au CLI et expiration quand la demande n'a plus d'objet.
    /// </summary>
    internal sealed class PermissionBroker
    {
        private readonly IIdeContext _ide;
        private readonly Action<JObject> _post;
        private readonly Func<string, JObject, bool> _sendResponse;   // faux si l'envoi au CLI a échoué
        private readonly Action<Func<Task>, string> _runOnUi;
        private readonly Dictionary<string, JObject> _pendingPermissions = new Dictionary<string, JObject>();
        private readonly Dictionary<string, (string path, string original, string proposed)> _diffBases = new Dictionary<string, (string, string, string)>();

        public PermissionBroker(IIdeContext ide, Action<JObject> post, Func<string, JObject, bool> sendResponse, Action<Func<Task>, string> runOnUi)
        {
            _ide = ide;
            _post = post;
            _sendResponse = sendResponse;
            _runOnUi = runOnUi;
        }

        public void Add(string requestId, JObject request) => _pendingPermissions[requestId] = request;

        public bool TryGet(string requestId, out JObject request) => _pendingPermissions.TryGetValue(requestId, out request);

        /// <summary>Interface rechargée (plantage WebView2) : les demandes de permission encore ouvertes sont réaffichées.</summary>
        public void RepostPending()
        {
            foreach (var kv in _pendingPermissions)
                _post(new JObject { ["type"] = FromHost.Cli, ["msg"] = new JObject { ["type"] = "control_request", ["request_id"] = kv.Key, ["request"] = kv.Value } });
        }

        public void Resolve(string requestId, JObject response)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (requestId == null || response == null || !_pendingPermissions.TryGetValue(requestId, out var request)) return;

            _ide.Log.Info($"→ permission {requestId} : {response["behavior"]}");
            response["toolUseID"] = request["tool_use_id"];
            if ((string?)response["behavior"] == "allow" && response["updatedInput"] == null)
            {
                // Accepté depuis la carte du chat alors que le diff est ouvert : on garde les retouches faites dans le diff
                if (_diffBases.TryGetValue(requestId, out var b) && request["input"] is JObject input)
                {
                    var edited = _ide.CurrentDiffText(DiffTab(requestId));
                    response["updatedInput"] = EditPreview.BuildUpdatedInput((string?)request["tool_name"], input, b.path, b.original, b.proposed, edited);
                }
                else
                    response["updatedInput"] = request["input"];
            }
            // En cas d'échec d'envoi, la demande est encore en attente : HandleWriteFailure la marque « cancelled » avec les autres
            if (!_sendResponse(requestId, response)) return;
            _pendingPermissions.Remove(requestId);
            _diffBases.Remove(requestId);
            _ide.CloseDiff(DiffTab(requestId));
            _post(new JObject { ["type"] = FromHost.PermissionResolved, ["requestId"] = requestId, ["behavior"] = response["behavior"] });
        }

        /// <summary>
        /// Demande abandonnée sans réponse : ferme le diff et marque la carte. <paramref name="reason"/> en donne la cause
        /// à l'interface : « cli » (control_cancel_request du CLI) ou « stopped » (processus arrêté).
        /// </summary>
        public void Cancel(string requestId, string reason)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!_pendingPermissions.Remove(requestId)) return;
            _diffBases.Remove(requestId);
            _ide.CloseDiff(DiffTab(requestId));
            _post(new JObject
            {
                ["type"] = FromHost.PermissionResolved, ["requestId"] = requestId, ["behavior"] = "cancelled", ["reason"] = reason,
            });
        }

        /// <summary>Processus arrêté (nouvelle conversation, reprise, plantage) : toutes les demandes sont abandonnées.</summary>
        public void ClearAll()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            foreach (var id in _pendingPermissions.Keys.ToList()) Cancel(id, "stopped");
        }

        public static string DiffTab(string requestId) => "claude-perm-" + requestId;

        /// <summary>
        /// Ouvre la modification proposée dans le diff natif ; Accepter/Refuser y répond à la permission.
        /// Un échec (fichier illisible, old_string introuvable…) est signalé : la carte du chat reste utilisable.
        /// </summary>
        public void ShowEditDiff(string requestId, JObject request)
        {
            var input = request["input"] as JObject;
            var tool = (string?)request["tool_name"];
            var path = (string?)input?["file_path"];
            if (input == null || path == null || path.Length == 0) return; // sans input, path est null aussi

            _runOnUi(async () =>
            {
                string original, proposed;
                try
                {
                    // Le chemin vient du modèle (UNC, fichier verrouillé…) : lecture hors thread UI
                    original = File.Exists(path) ? await Task.Run(() => File.ReadAllText(path)) : "";
                    proposed = EditPreview.Apply(tool, input, original);
                }
                catch (Exception ex)
                {
                    _ide.Log.Info($"Diff indisponible pour {path} : {ex.Message}");
                    _post(new JObject { ["type"] = FromHost.Banner, ["sticky"] = true, ["text"] = $"Diff indisponible pour {Path.GetFileName(path)} : {ex.Message}\nRépondez depuis la carte du chat." });
                    return;
                }
                if (!_pendingPermissions.ContainsKey(requestId)) return; // décidé ou abandonné pendant la lecture

                _diffBases[requestId] = (path, original, proposed);
                var (accepted, text) = await _ide.ShowDiffAsync(path, proposed, DiffTab(requestId));
                if (accepted == null || !_pendingPermissions.ContainsKey(requestId)) return; // onglet fermé ou décidé dans le chat

                JObject response;
                if (accepted == true)
                    response = new JObject { ["behavior"] = "allow", ["updatedInput"] = EditPreview.BuildUpdatedInput(tool, input, path, original, proposed, text) };
                else
                    response = new JObject { ["behavior"] = "deny", ["message"] = "L'utilisateur a refusé la modification dans le diff de Visual Studio." };
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(); // ShowDiffAsync s'y termine déjà : sans coût
                Resolve(requestId, response);
            }, "diff");
        }
    }
}
