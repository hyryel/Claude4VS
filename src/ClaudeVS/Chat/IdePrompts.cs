using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
#nullable enable

namespace ClaudeVS.Chat
{
    /// <summary>Textes tirés du contexte de l'IDE (exception, sélection) pour le modèle et l'interface. Sans dépendance à Visual Studio.</summary>
    internal static class IdePrompts
    {
        public static string ExceptionPrompt(JObject state)
        {
            // Première trame avec un fichier source (la tête de pile est souvent dans le framework)
            var loc = (state["callStack"] as JArray)?.FirstOrDefault(f => f["file"] != null) ?? state["location"];
            var ex = state["exception"];
            var sb = new System.Text.StringBuilder();
            sb.Append("[Événement Visual Studio] Le débogueur s'est arrêté sur une exception (")
              .Append((string?)state["breakReason"]).Append(')');
            if (ex != null) sb.Append(" : ").Append((string?)ex["type"]).Append(" — ").Append((string?)ex["value"]);
            sb.Append(".\n");
            if (loc != null) sb.Append($"Position : {loc["file"]}:{loc["line"]} dans {loc["function"]}.\n");
            if (state["callStack"] is JArray frames && frames.Count > 0)
            {
                sb.Append("Pile :\n");
                foreach (var f in frames)
                    sb.Append("- ").Append((string?)f["function"])
                      .Append(f["file"] != null ? $" ({f["file"]}:{f["line"]})" : "").Append('\n');
            }
            sb.Append("Analyse la cause avec les outils mcp__visualstudio__ (debugState, debugLocals, debugEvaluate si utile), "
                    + "explique-la et propose une correction. Ne continue ni n'arrête l'exécution sans me le demander.");
            return sb.ToString();
        }

        /// <summary>Même format que l'extension VS Code (ide_selection / ide_opened_file).</summary>
        public static string? SelectionContext(JObject? sel)
        {
            if (sel == null) return null;
            var path = (string?)sel["filePath"];
            var s = sel["selection"]!; // toujours produit par SelectionTracker
            if (IsEmpty(s) || string.IsNullOrEmpty((string?)sel["text"]))
                return $"<ide_opened_file>The user opened the file {path} in the IDE. This may or may not be related to the current task.</ide_opened_file>";
            return $"<ide_selection>The user selected the lines {Line(s, "start") + 1} to {Line(s, "end") + 1} from {path}:\n{(string?)sel["text"]}\n\nThis may or may not be related to the current task.</ide_selection>";
        }

        // start/end sont toujours produits par SelectionTracker avec la sélection
        private static bool IsEmpty(JToken selection) => (bool)selection["isEmpty"]!;
        private static int Line(JToken selection, string edge) => (int)selection[edge]!["line"]!;

        /// <summary>Libellé de la sélection affiché dans le panneau : « fichier », « fichier:ligne » ou « fichier:début-fin ».</summary>
        public static string? SelectionLabel(JObject? sel)
        {
            if (sel == null) return null;
            var file = Path.GetFileName((string?)sel["filePath"]);
            var s = sel["selection"]!; // toujours produit par SelectionTracker
            return IsEmpty(s)
                ? file
                : Line(s, "start") == Line(s, "end")
                    ? $"{file}:{Line(s, "start") + 1}"
                    : $"{file}:{Line(s, "start") + 1}-{Line(s, "end") + 1}";
        }
    }
}
