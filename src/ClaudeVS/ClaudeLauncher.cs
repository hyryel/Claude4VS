using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace ClaudeVS
{
    /// <summary>Comment lancer le CLI : un exécutable, précédé éventuellement d'arguments (ex. node + cli.js).</summary>
    internal sealed class ClaudeCommand
    {
        public string FileName { get; }
        public IReadOnlyList<string> PrefixArgs { get; }
        public string Origin { get; }

        public ClaudeCommand(string fileName, string origin, params string[] prefixArgs)
        {
            FileName = fileName;
            Origin = origin;
            PrefixArgs = prefixArgs;
        }

        public override string ToString() => string.Join(" ", new[] { FileName }.Concat(PrefixArgs)) + $" [{Origin}]";
    }

    /// <summary>Lance le CLI dans une console, déjà branché sur le serveur MCP de cette instance de VS.</summary>
    internal static class ClaudeLauncher
    {
        public const string NotFoundMessage =
            "Claude Code (« claude ») est introuvable sur ce poste.\n" +
            "Installez-le avec : irm https://claude.ai/install.ps1 | iex   (ou : npm install -g @anthropic-ai/claude-code),\n" +
            "puis redémarrez Visual Studio. Chemin explicite possible via la variable d'environnement CLAUDEVS_CLAUDE_PATH.";

        public static void Launch(ClaudeVSPackage package)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var cmd = FindClaude();
            if (cmd == null)
            {
                VsShellUtilities.ShowMessageBox(package, NotFoundMessage, "ClaudeVS",
                    OLEMSGICON.OLEMSGICON_WARNING, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
                return;
            }

            var cwd = package.Server != null && Package.GetGlobalService(typeof(SVsSolution)) is IVsSolution sln
                      && sln.GetSolutionInfo(out var dir, out _, out _) == 0 && !string.IsNullOrEmpty(dir)
                ? dir
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            var psi = new ProcessStartInfo(cmd.FileName)
            {
                Arguments = string.Join(" ", cmd.PrefixArgs.Select(a => a.Contains(' ') ? "\"" + a + "\"" : a)),
                UseShellExecute = false, // nécessaire pour transmettre les variables d'environnement
                WorkingDirectory = cwd,
            };
            psi.EnvironmentVariables["CLAUDE_CODE_SSE_PORT"] = package.Server.Port.ToString();
            psi.EnvironmentVariables["ENABLE_IDE_INTEGRATION"] = "true";
            Process.Start(psi);
            package.Log.Info($"Claude lancé : {cmd} (cwd {cwd})");
        }

        /// <summary>
        /// Cherche le CLI, dans l'ordre : CLAUDEVS_CLAUDE_PATH, le PATH (claude.exe ou claude.cmd d'npm),
        /// ~/.local/bin (installeur officiel), ~/claude, le paquet npm global (lancé avec node), l'extension VS Code.
        /// </summary>
        internal static ClaudeCommand FindClaude()
        {
            var explicitPath = Environment.GetEnvironmentVariable("CLAUDEVS_CLAUDE_PATH");
            if (!string.IsNullOrEmpty(explicitPath) && File.Exists(explicitPath))
                return FromFile(explicitPath, "CLAUDEVS_CLAUDE_PATH");

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var dirs = new List<string>();
            dirs.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Select(d => d.Trim()).Where(d => d.Length > 0));
            dirs.Add(Path.Combine(home, ".local", "bin"));
            dirs.Add(Path.Combine(home, "claude"));
            dirs.Add(Path.Combine(home, "claude", "bin"));
            dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"));
            foreach (var d in dirs)
            {
                foreach (var name in new[] { "claude.exe", "claude.cmd" })
                {
                    try
                    {
                        var candidate = Path.Combine(d, name);
                        if (File.Exists(candidate)) return FromFile(candidate, "PATH");
                    }
                    catch (ArgumentException) { /* entrée de PATH mal formée */ }
                }
            }

            // Repli : binaire embarqué par l'extension VS Code (version la plus récente)
            var vscodeExt = Path.Combine(home, ".vscode", "extensions");
            if (Directory.Exists(vscodeExt))
            {
                var exe = Directory.GetDirectories(vscodeExt, "anthropic.claude-code-*")
                    .Select(d => Path.Combine(d, "resources", "native-binary", "claude.exe"))
                    .Where(File.Exists)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
                if (exe != null) return new ClaudeCommand(exe, "extension VS Code");
            }
            return null;
        }

        /// <summary>Un claude.cmd (npm) est un script : on lance directement node sur cli.js, qui accepte stdin/stdout redirigés.</summary>
        private static ClaudeCommand FromFile(string path, string origin)
        {
            if (!path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
                return new ClaudeCommand(path, origin);

            var npmDir = Path.GetDirectoryName(path);
            var cli = Path.Combine(npmDir, "node_modules", "@anthropic-ai", "claude-code", "cli.js");
            if (File.Exists(cli))
            {
                var node = FindOnPath("node.exe") ?? "node";
                return new ClaudeCommand(node, origin + " (npm)", cli);
            }
            // Script inconnu : on le fait exécuter par cmd.exe
            return new ClaudeCommand(Environment.ExpandEnvironmentVariables(@"%ComSpec%"), origin + " (cmd)", "/d", "/c", path);
        }

        private static string FindOnPath(string file)
        {
            foreach (var d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                try
                {
                    var candidate = Path.Combine(d.Trim(), file);
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException) { }
            }
            return null;
        }
    }
}
