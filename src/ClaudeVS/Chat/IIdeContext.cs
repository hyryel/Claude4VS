using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Newtonsoft.Json.Linq;
#nullable enable

namespace ClaudeVS.Chat
{
    /// <summary>Ce que le contrôleur du chat demande à Visual Studio.</summary>
    internal interface IIdeContext
    {
        Log Log { get; }

        /// <summary>Dossier de la solution ouverte, sinon le dossier utilisateur. Thread UI.</summary>
        string SolutionDirectory();

        /// <summary>Ouvre un fichier existant (chemin absolu), au besoin à une ligne (1-based). Thread UI.</summary>
        void OpenFile(string path, int? line);

        /// <summary>Mettre en forme le document ; null si les outils de l'IDE ne sont pas prêts. Thread UI.</summary>
        JObject? FormatDocument(string path);

        /// <summary>
        /// Diff natif de la proposition : accepted = true (Accepter), false (Refuser), null (onglet fermé ou abandonné) ;
        /// text = contenu final, éventuellement retouché. Se termine sur le thread UI.
        /// </summary>
        Task<(bool? accepted, string? text)> ShowDiffAsync(string path, string proposed, string tab);

        void CloseDiff(string tab);
    }

    /// <summary>Implémentation sur le package : solution courante, ouverture de documents, mise en forme, diff.</summary>
    internal sealed class VsIdeContext : IIdeContext
    {
        private readonly ClaudeVSPackage _package;

        public VsIdeContext(ClaudeVSPackage package) => _package = package;

        public Log Log => _package.Log;

        public string SolutionDirectory()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (Package.GetGlobalService(typeof(SVsSolution)) is IVsSolution sln
                && sln.GetSolutionInfo(out var dir, out _, out _) == 0 && !string.IsNullOrEmpty(dir))
                return dir.TrimEnd('\\');
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        public void OpenFile(string path, int? line)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            VsShellUtilities.OpenDocument(_package, path, Guid.Empty, out _, out _, out var frame, out var view);
            frame?.Show();
            if (line.HasValue && view != null) view.SetCaretPos(Math.Max(0, line.Value - 1), 0);
        }

        public JObject? FormatDocument(string path) => _package.Tools?.Editor.FormatDocument(path, closeIfNotOpen: true);

        public async Task<(bool? accepted, string? text)> ShowDiffAsync(string path, string proposed, string tab)
        {
            var result = await DiffManager.GetInstance(_package).ShowProposalAsync(path, path, proposed, tab);
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
            return result;
        }

        public void CloseDiff(string tab) => DiffManager.GetInstance(_package).Close(tab);
    }
}
