using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;
#nullable enable

namespace ClaudeVS.Chat
{
    /// <summary>
    /// Requêtes de contrôle dont la réponse est attendue (get_context_usage, rewind_files…) : corrélation des
    /// réponses du CLI par request_id, et messages produits vers l'interface.
    /// </summary>
    internal sealed class ControlRequests
    {
        private readonly Func<JObject, string> _send;   // request_id, ou null si le CLI ne tourne pas ou si l'envoi a échoué
        private readonly Action<JObject> _post;
        private readonly Log _log;
        private readonly Dictionary<string, Action<JObject>> _pendingControl = new Dictionary<string, Action<JObject>>();

        public ControlRequests(Func<JObject, string> send, Action<JObject> post, Log log)
        {
            _send = send;
            _post = post;
            _log = log;
        }

        /// <summary>Requête de contrôle dont la réponse est attendue (get_context_usage, rewind_files…). Faux si le CLI ne tourne pas ou si l'envoi a échoué.</summary>
        public bool Send(JObject request, Action<JObject> onResponse)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var id = _send(request);
            if (id == null) return false;
            _pendingControl[id] = onResponse;
            return true;
        }

        /// <summary>
        /// Réponse attendue : son gestionnaire est appelé et la méthode renvoie vrai. Le gestionnaire produit son propre
        /// message vers l'interface (rewindPreview, context…) : pas de relais brut, sinon une erreur s'afficherait deux fois.
        /// </summary>
        public bool TryComplete(string id, JObject response)
        {
            if (id == null || !_pendingControl.TryGetValue(id, out var handler)) return false;
            _pendingControl.Remove(id);
            handler(response);
            return true;
        }

        public void Clear() => _pendingControl.Clear();

        /// <summary>Remplissage du contexte : catégories renvoyées par le CLI, résumées en « utilisé / total ».</summary>
        public void RequestContextUsage()
        {
            Send(new JObject { ["subtype"] = "get_context_usage" }, r =>
            {
                var categories = r["response"]?["categories"] as JArray;
                if (categories == null) return;
                long total = 0, used = 0;
                var details = new JArray();
                foreach (var c in categories)
                {
                    var tokens = (long?)c["tokens"] ?? 0;
                    var kind = (string?)c["kind"];
                    total += tokens;
                    if (kind == "used" || kind == "deferred") used += tokens;
                    if (kind != "free") details.Add(new JObject { ["name"] = c["name"], ["tokens"] = tokens });
                }
                _post(new JObject { ["type"] = FromHost.Context, ["used"] = used, ["total"] = total, ["details"] = details });
            });
        }

        /// <summary>
        /// Consommation du forfait (données de /usage) : plafonds de session (5 h), de semaine et par modèle, coût de la
        /// session. Requête expérimentale du CLI : un refus (version trop ancienne) laisse simplement la ligne masquée.
        /// skip_behaviors évite le parcours des transcriptions des 7 derniers jours, inutile ici.
        /// </summary>
        public void RequestUsage()
        {
            Send(new JObject { ["subtype"] = "get_usage", ["skip_behaviors"] = true }, r =>
            {
                var usage = r["response"] as JObject;
                if ((string?)r["subtype"] != "success" || usage == null) return;
                _post(new JObject
                {
                    ["type"] = FromHost.Usage,
                    // Clé API, Bedrock, Vertex : pas de plafonds, seul le coût est affiché
                    ["limits"] = (bool?)usage["rate_limits_available"] == true ? usage["rate_limits"] as JObject : null,
                    ["cost"] = usage["session"]?["total_cost_usd"],
                });
            });
        }

        /// <summary>Modèle et effort réellement appliqués par le CLI (get_settings.applied), pour renseigner les entrées « par défaut ».</summary>
        public void RequestApplied()
        {
            Send(new JObject { ["subtype"] = "get_settings" }, r =>
            {
                var applied = r["response"]?["applied"] as JObject;
                if (applied == null) return;
                _post(new JObject { ["type"] = FromHost.Applied, ["model"] = applied["model"], ["effort"] = applied["effort"] });
            });
        }

        /// <summary>
        /// Réglage de la barre d'outils (set_model, set_permission_mode, apply_flag_settings) : la réponse du CLI est
        /// rapportée à l'interface, qui confirme le changement ou affiche le refus. Faux si le CLI ne tourne pas.
        /// </summary>
        public bool ApplySetting(string setting, JObject request)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return Send(request, r => _post(new JObject
            {
                ["type"] = FromHost.SettingApplied,
                ["setting"] = setting,
                ["ok"] = (string?)r["subtype"] == "success",
                ["error"] = r["error"],
            }));
        }

        /// <summary>Retour arrière des fichiers à l'état précédant un message utilisateur (dry run = aperçu).</summary>
        public void Rewind(string uuid, bool dryRun)
        {
            if (string.IsNullOrEmpty(uuid)) return;
            var request = new JObject { ["subtype"] = "rewind_files", ["user_message_id"] = uuid };
            if (dryRun) request["dry_run"] = true;
            var sent = Send(request, r =>
            {
                var resp = r["response"] as JObject ?? new JObject();
                var error = (string?)r["error"] ?? (string?)resp["error"];
                _post(new JObject
                {
                    ["type"] = dryRun ? FromHost.RewindPreview : FromHost.RewindDone,
                    ["uuid"] = uuid,
                    ["ok"] = (string?)r["subtype"] == "success" && error == null,
                    ["error"] = error,
                    ["canRewind"] = resp["canRewind"],
                    ["files"] = resp["filesChanged"] ?? new JArray(),
                    ["insertions"] = resp["insertions"],
                    ["deletions"] = resp["deletions"],
                });
                if (!dryRun) _log.Info($"Retour arrière des fichiers ({uuid}) : {(error ?? "ok")}");
            });
            // Sans processus, la carte de retour arrière explique pourquoi au lieu d'un bouton muet
            if (!sent)
                _post(new JObject
                {
                    ["type"] = dryRun ? FromHost.RewindPreview : FromHost.RewindDone, ["uuid"] = uuid, ["ok"] = false,
                    ["error"] = "claude n'est pas démarré : reprenez la conversation (Reprendre) pour restaurer les fichiers.",
                });
        }
    }
}
