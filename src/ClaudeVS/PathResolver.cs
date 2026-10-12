using System;
using System.IO;
using System.Linq;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
#nullable enable

namespace ClaudeVS
{
    /// <summary>
    /// Résolution unique des chemins reçus des outils : un chemin relatif est pris depuis le dossier de la
    /// solution, à défaut depuis celui du premier projet chargé (projet ouvert seul) ; une URI file: est acceptée.
    /// </summary>
    internal static class PathResolver
    {
        /// <summary>Chemin absolu normalisé. Thread principal requis (lecture de la solution).</summary>
        /// <exception cref="InvalidOperationException">Chemin relatif sans solution ni projet ouvert.</exception>
        /// <exception cref="FileNotFoundException">Si <paramref name="mustExist"/> et que le fichier n'existe pas.</exception>
        public static string Resolve(string path, bool mustExist)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("File path is required");
            if (path.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) path = new Uri(path).LocalPath;
            if (!Path.IsPathRooted(path)) path = Path.Combine(BaseDirectory(), path);
            path = Path.GetFullPath(path);
            if (mustExist && !File.Exists(path)) throw new FileNotFoundException("File not found: " + path);
            return path;
        }

        /// <summary>Dossier de base des chemins relatifs : solution, sinon premier projet chargé.</summary>
        public static string BaseDirectory()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (Package.GetGlobalService(typeof(SVsSolution)) is IVsSolution solution
                && solution.GetSolutionInfo(out var dir, out _, out _) == VSConstants.S_OK
                && !string.IsNullOrEmpty(dir))
                return dir;
            var project = FirstProjectPath((DTE2)Package.GetGlobalService(typeof(SDTE)));
            if (!string.IsNullOrEmpty(project)) return Path.GetDirectoryName(project);
            throw new InvalidOperationException("No solution or project is open");
        }

        /// <summary>Chemin du premier projet chargé (un « .csproj » ouvert seul n'a pas de solution enregistrée), sinon null.</summary>
        public static string? FirstProjectPath(DTE2? dte)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (dte?.Solution == null) return null;
            foreach (Project p in dte.Solution.Projects)
                if (!string.IsNullOrEmpty(p.FullName)) return p.FullName;
            return null;
        }
    }
}
