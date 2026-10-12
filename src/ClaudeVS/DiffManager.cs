using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.TextManager.Interop;
using Newtonsoft.Json.Linq;

namespace ClaudeVS
{
    /// <summary>
    /// openDiff : affiche la proposition de Claude dans le diff natif de VS avec une barre
    /// « Accepter / Refuser ». L'appel MCP reste en attente jusqu'à la décision de l'utilisateur.
    /// </summary>
    internal sealed class DiffManager
    {
        private static DiffManager _instance;
        private static readonly string TempRoot = Path.Combine(Path.GetTempPath(), "ClaudeVS", "diff");

        private readonly AsyncPackage _package;
        private readonly Dictionary<string, PendingDiff> _pending = new Dictionary<string, PendingDiff>();

        private Log _log;

        private DiffManager(AsyncPackage package)
        {
            _package = package;
            _ = Task.Run(PurgeStaleTemp);
        }

        /// <param name="log">Journal : renseigné par le premier appelant qui en fournit un (IdeTools à l'initialisation).</param>
        public static DiffManager GetInstance(AsyncPackage package, Log log = null)
        {
            var instance = _instance ?? (_instance = new DiffManager(package));
            if (log != null && instance._log == null) instance._log = log;
            return instance;
        }

        /// <summary>Dossiers de diff laissés par des sessions précédentes (fichier verrouillé, VS tué) : purge au démarrage, au mieux.</summary>
        private static void PurgeStaleTemp()
        {
            try
            {
                if (!Directory.Exists(TempRoot)) return;
                var limit = DateTime.UtcNow.AddDays(-2);
                foreach (var dir in Directory.GetDirectories(TempRoot))
                    if (Directory.GetLastWriteTimeUtc(dir) < limit) TryDelete(dir);
            }
            catch { }
        }

        public static bool IsTempPath(string path) =>
            !string.IsNullOrEmpty(path) && path.StartsWith(TempRoot, StringComparison.OrdinalIgnoreCase);

        public async Task<JObject> OpenDiffAsync(string oldPath, string newPath, string newContents, string tabName)
        {
            tabName = tabName ?? newPath;
            var (accepted, finalText) = await ShowProposalAsync(oldPath, newPath, newContents, tabName);
            return new JObject
            {
                ["content"] = accepted == true ? IdeTools.Text("FILE_SAVED", finalText) : IdeTools.Text("DIFF_REJECTED", tabName),
            };
        }

        /// <summary>
        /// Affiche la proposition et attend la décision : true = Accepter, false = Refuser, null = onglet fermé ou
        /// proposition abandonnée. Renvoie le texte final (éventuellement retouché) si accepté.
        /// </summary>
        public async Task<(bool? accepted, string text)> ShowProposalAsync(string oldPath, string newPath, string newContents, string tabName)
        {
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
            Close(tabName); // une proposition antérieure sur le même onglet est abandonnée

            var dir = Path.Combine(TempRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var fileName = Path.GetFileName(newPath ?? oldPath ?? "fichier");

            // Gauche : le fichier actuel sur disque (vide s'il s'agit d'une création)
            var leftIsTemp = string.IsNullOrEmpty(oldPath) || !File.Exists(oldPath);
            var left = leftIsTemp ? Path.Combine(dir, "original_" + fileName) : oldPath;
            if (leftIsTemp) File.WriteAllText(left, "");

            // Droite : la proposition, dans un fichier temporaire modifiable par l'utilisateur
            var right = Path.Combine(dir, fileName);
            File.WriteAllText(right, newContents ?? "");

            var diffService = (IVsDifferenceService)await _package.GetServiceAsync(typeof(SVsDifferenceService));
            var options = leftIsTemp ? (uint)__VSDIFFSERVICEOPTIONS.VSDIFFOPT_LeftFileIsTemporary : 0u;
            var frame = diffService.OpenComparisonWindow2(left, right,
                $"Claude ↔ {fileName}", tabName,
                leftIsTemp ? $"{fileName} (nouveau fichier)" : $"{fileName} (actuel)",
                $"{fileName} (proposé par Claude)",
                $"Claude : {fileName}", null, options);
            if (frame == null)
            {
                TryDelete(dir);
                throw new InvalidOperationException("Visual Studio could not open the comparison window for " + fileName);
            }

            var pending = new PendingDiff(this, tabName, frame, right);
            _pending[tabName] = pending;
            pending.Attach();
            frame.Show();

            var accepted = await pending.Decision.Task;
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
            var finalText = accepted == true ? ReadBuffer(right) : null;
            pending.Dispose();
            if (_pending.TryGetValue(tabName, out var current) && current == pending)
                _pending.Remove(tabName);
            pending.CloseFrame();
            TryDelete(dir);

            _log?.Info(accepted == true ? $"Diff accepté : {fileName}" : accepted == false ? $"Diff refusé : {fileName}" : $"Diff fermé : {fileName}");
            return (accepted, finalText);
        }

        /// <summary>Contenu de la proposition, y compris les retouches faites par l'utilisateur dans le diff.</summary>
        private string ReadBuffer(string path)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var rdt = new RunningDocumentTable(_package);
            if (rdt.FindDocument(path) is IVsTextBuffer vsBuffer)
            {
                var componentModel = (IComponentModel)Package.GetGlobalService(typeof(SComponentModel));
                var buffer = componentModel.GetService<IVsEditorAdaptersFactoryService>().GetDocumentBuffer(vsBuffer);
                if (buffer != null) return buffer.CurrentSnapshot.GetText();
            }
            return File.ReadAllText(path);
        }

        /// <summary>Contenu actuel (éventuellement retouché) de la proposition en cours sur cet onglet, sinon null.</summary>
        public string CurrentText(string tabName)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return tabName != null && _pending.TryGetValue(tabName, out var p) ? ReadBuffer(p.RightPath) : null;
        }

        public void Close(string tabName)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (tabName != null && _pending.TryGetValue(tabName, out var p))
                p.Decision.TrySetResult(null);
        }

        public int CloseAll()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var all = _pending.Values.ToList();
            foreach (var p in all) p.Decision.TrySetResult(null);
            return all.Count;
        }

        private static void TryDelete(string dir)
        {
            try { Directory.Delete(dir, true); } catch { /* fichier encore verrouillé : tant pis */ }
        }

        private sealed class PendingDiff : IVsInfoBarUIEvents, IVsWindowFrameNotify, IDisposable
        {
            private const string AcceptText = "Accepter";
            private const string RejectText = "Refuser";

            private readonly DiffManager _owner;
            private readonly string _tabName;
            private readonly IVsWindowFrame _frame;
            private readonly string _rightPath;
            private IVsInfoBarUIElement _infoBar;
            private uint _infoBarCookie;
            private uint _frameCookie;

            public string RightPath => _rightPath;

            public TaskCompletionSource<bool?> Decision { get; } =
                new TaskCompletionSource<bool?>(TaskCreationOptions.RunContinuationsAsynchronously);

            public PendingDiff(DiffManager owner, string tabName, IVsWindowFrame frame, string rightPath)
            {
                _owner = owner;
                _tabName = tabName;
                _frame = frame;
                _rightPath = rightPath;
            }

            public void Attach()
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var model = new InfoBarModel(
                    new[] { new InfoBarTextSpan($"Claude propose une modification de {Path.GetFileName(_rightPath)}. Vous pouvez la retoucher à droite avant d'accepter.  ") },
                    new IVsInfoBarActionItem[] { new InfoBarButton(AcceptText), new InfoBarButton(RejectText) },
                    KnownMonikers.StatusInformation,
                    isCloseButtonVisible: false);
                var factory = (IVsInfoBarUIFactory)Package.GetGlobalService(typeof(SVsInfoBarUIFactory));
                _infoBar = factory.CreateInfoBar(model);
                _infoBar.Advise(this, out _infoBarCookie);
                if (ErrorHandler.Succeeded(_frame.GetProperty((int)__VSFPROPID7.VSFPROPID_InfoBarHost, out var host))
                    && host is IVsInfoBarHost infoBarHost)
                    infoBarHost.AddInfoBar(_infoBar);

                if (_frame is IVsWindowFrame2 frame2)
                    frame2.Advise(this, out _frameCookie);
            }

            public void CloseFrame()
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                try { _frame.CloseFrame((uint)__FRAMECLOSE.FRAMECLOSE_NoSave); } catch { }
            }

            // Barre d'info
            public void OnActionItemClicked(IVsInfoBarUIElement element, IVsInfoBarActionItem actionItem)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                Decision.TrySetResult(actionItem.Text == AcceptText);
            }

            public void OnClosed(IVsInfoBarUIElement element) { }

            // Fermeture de l'onglet de diff sans décision
            public int OnShow(int fShow)
            {
                if (fShow == (int)__FRAMESHOW.FRAMESHOW_WinClosed)
                    Decision.TrySetResult(null);
                return VSConstants.S_OK;
            }

            public int OnMove() => VSConstants.S_OK;
            public int OnSize() => VSConstants.S_OK;
            public int OnDockableChange(int fDockable) => VSConstants.S_OK;

            public void Dispose()
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (_infoBar != null && _infoBarCookie != 0) _infoBar.Unadvise(_infoBarCookie);
                if (_frame is IVsWindowFrame2 frame2 && _frameCookie != 0) frame2.Unadvise(_frameCookie);
                _infoBarCookie = _frameCookie = 0;
            }
        }
    }
}
