using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
#nullable enable

namespace ClaudeVS.Chat
{
    /// <summary>Valeurs acceptées de l'interface (elles finissent sur la ligne de commande du CLI).</summary>
    internal static class ClaudeSettings
    {
        public static readonly string[] PermissionModes = { "default", "acceptEdits", "plan", "bypassPermissions", "dontAsk" };
        public static readonly string[] EffortLevels = { "low", "medium", "high", "xhigh", "max" };
        public static readonly Regex ModelPattern = new Regex(@"^[A-Za-z0-9][A-Za-z0-9._:/\[\]-]{0,199}$");

        public static bool IsValidMode(string mode) => PermissionModes.Contains(mode);
        public static bool IsValidEffort(string effort) => EffortLevels.Contains(effort);
        public static bool IsValidModel(string model) => ModelPattern.IsMatch(model);
    }

    /// <summary>Réglages d'un lancement du CLI.</summary>
    internal sealed class ClaudeOptions
    {
        public string PermissionMode { get; set; } = "default"; // toujours renseigné par l'appelant
        public string? Model { get; set; }             // null = modèle par défaut du CLI
        public string? Effort { get; set; }            // null = effort par défaut du modèle
        public string? ResumeSessionId { get; set; }   // null = nouvelle conversation
        public string? McpConfigPath { get; set; }     // null = pas de serveur MCP de l'IDE
    }

    /// <summary>Ligne de commande du CLI et configuration du serveur MCP de l'IDE. Sans dépendance à Visual Studio.</summary>
    internal static class ClaudeArguments
    {
        // Outils VS sans effet sur l'exécution du programme : autorisés sans confirmation.
        // Démarrer/arrêter/continuer le débogage et évaluer une expression passent par la carte de permission.
        private static readonly string[] ReadOnlyVsTools =
        {
            "getDiagnostics", "getOpenEditors", "getWorkspaceFolders", "getCurrentSelection", "checkDocumentDirty",
            "build", "debugState", "debugLocals", "breakpointAdd", "breakpointRemove", "breakpointList", "getOutput",
            "runTests", "formatDocument", "bookmarkList", "bookmarkToggle",
            "findSymbol", "symbolInfo", "findReferences", "documentSymbols", "semanticDiagnostics", // renameSymbol demande confirmation
        };

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

        public static List<string> Build(IEnumerable<string> prefixArgs, ClaudeOptions o)
        {
            var args = new List<string>(prefixArgs)
            {
                "--output-format", "stream-json", "--verbose",
                "--input-format", "stream-json",
                "--include-partial-messages",
                "--permission-prompt-tool", "stdio",
                "--permission-mode", o.PermissionMode,
            };
            if (o.Model != null) args.AddRange(new[] { "--model", o.Model });
            if (o.Effort != null) args.AddRange(new[] { "--effort", o.Effort });
            if (o.ResumeSessionId != null) args.AddRange(new[] { "--resume", o.ResumeSessionId });
            // Le CLI renvoie nos messages avec leur uuid : nécessaire pour le retour arrière (rewind_files)
            args.Add("--replay-user-messages");
            if (o.McpConfigPath != null)
            {
                args.AddRange(new[] { "--mcp-config", o.McpConfigPath });
                args.AddRange(new[] { "--append-system-prompt", VsToolsGuidance });
                // Outils VS en lecture seule : pas de demande de permission
                args.AddRange(new[] { "--allowedTools", string.Join(",", ReadOnlyVsTools.Select(t => "mcp__visualstudio__" + t)) });
            }
            return args;
        }

        /// <summary>
        /// Le serveur de l'extension est passé au CLI comme serveur MCP « visualstudio » :
        /// getDiagnostics, éditeurs ouverts, sélection… (le type ws-ide est ignoré hors mode interactif).
        /// </summary>
        public static JObject McpConfig(int port, string authToken) => new JObject
        {
            ["mcpServers"] = new JObject
            {
                ["visualstudio"] = new JObject
                {
                    ["type"] = "ws",
                    ["url"] = $"ws://127.0.0.1:{port}/tools",
                    ["headers"] = new JObject { ["x-claude-code-ide-authorization"] = authToken },
                    // Sans cela, les outils sont « différés » : le modèle ne les voit qu'après un ToolSearch, et ne les utilise pas spontanément
                    ["alwaysLoad"] = true,
                },
            },
        };
    }
}
