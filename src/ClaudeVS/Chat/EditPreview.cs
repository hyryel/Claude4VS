using System;
using Newtonsoft.Json.Linq;
#nullable enable

namespace ClaudeVS.Chat
{
    /// <summary>
    /// Contenu proposé par les outils d'édition (Edit, MultiEdit, Write) et réponse à renvoyer au CLI
    /// quand l'utilisateur a retouché la proposition dans le diff. Sans dépendance à Visual Studio.
    /// </summary>
    internal static class EditPreview
    {
        public static string Apply(string? tool, JObject input, string original)
        {
            switch (tool)
            {
                case "Write":
                    return (string?)input["content"] ?? "";
                case "Edit":
                    return Replace(original, (string?)input["old_string"], (string?)input["new_string"], (bool?)input["replace_all"] ?? false);
                case "MultiEdit":
                    var text = original;
                    foreach (var e in (JArray?)input["edits"] ?? new JArray())
                        text = Replace(text, (string?)e["old_string"], (string?)e["new_string"], (bool?)e["replace_all"] ?? false);
                    return text;
                default:
                    throw new NotSupportedException(tool);
            }
        }

        public static string Replace(string text, string? oldString, string? newString, bool all)
        {
            if (oldString == null || oldString.Length == 0) return newString ?? "";
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
            // Le texte inséré prend les fins de ligne du fichier, même quand old_string (une seule ligne) a été trouvé tel
            // quel : sinon l'aperçu mélange \n et \r\n (« Inconsistent Line Endings » dans le diff)
            if (text.Contains("\r\n")) newString = newString.Replace("\r\n", "\n").Replace("\n", "\r\n");
            return all ? text.Replace(oldString, newString) : text.Substring(0, idx) + newString + text.Substring(idx + oldString.Length);
        }

        /// <summary>Entrée acceptée dans le diff : celle du modèle, ou le texte retouché par l'utilisateur (<paramref name="edited"/>).</summary>
        public static JObject BuildUpdatedInput(string? tool, JObject input, string path, string original, string proposed, string? edited)
        {
            var updated = (JObject)input.DeepClone();
            if (edited != null && edited != proposed)
            {
                // Retouches de l'utilisateur dans le diff : on les transmet telles quelles, en une seule
                // substitution du contenu actuel (old_string vide = création, comme le fait le CLI)
                switch (tool)
                {
                    case "Write":
                        updated["content"] = edited;
                        break;
                    case "Edit":
                        updated = new JObject { ["file_path"] = path, ["old_string"] = original, ["new_string"] = edited, ["replace_all"] = false };
                        break;
                    case "MultiEdit":
                        updated = new JObject { ["file_path"] = path, ["edits"] = new JArray(new JObject { ["old_string"] = original, ["new_string"] = edited }) };
                        break;
                }
            }
            return updated;
        }
    }
}
