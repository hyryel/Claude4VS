using System;
using System.ComponentModel.Design;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace ClaudeVS
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [Guid(PackageGuidString)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.ShellInitialized_string, PackageAutoLoadFlags.BackgroundLoad)]
    // Onglet à côté de l'Explorateur de solutions
    [ProvideToolWindow(typeof(Chat.ChatToolWindow), Style = VsDockStyle.Tabbed, Window = "3ae79031-e1bc-11d0-8f78-00a0c9110057")]
    public sealed class ClaudeVSPackage : AsyncPackage
    {
        public const string PackageGuidString = "3f6a0c8e-2d4b-4f1a-9c7e-5b8d1a2e4f60";
        public static readonly Guid CmdSet = new Guid("b2d4e6f8-1a3c-4e5f-8a9b-0c1d2e3f4a5b");
        public const int CmdLaunchClaude = 0x0100;
        public const int CmdMention = 0x0101;
        public const int CmdOpenChat = 0x0102;

        internal IdeServer Server { get; private set; }
        internal IdeTools Tools { get; private set; }
        internal Log Log { get; private set; }

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            Log = new Log(this);

            var tools = new IdeTools(this, Log);
            Tools = tools;
            Server = new IdeServer(tools, Log);
            Server.Start(await tools.GetWorkspaceFoldersAsync());
            DebugMonitor.Start(tools.Debug, Log, EnsureChatVisible);
            SelectionTracker.Start(Log);

            Microsoft.VisualStudio.Shell.Events.SolutionEvents.OnAfterOpenSolution += (s, e) => RefreshWorkspace();
            Microsoft.VisualStudio.Shell.Events.SolutionEvents.OnAfterCloseSolution += (s, e) => RefreshWorkspace();

            if (await GetServiceAsync(typeof(IMenuCommandService)) is OleMenuCommandService mcs)
            {
                mcs.AddCommand(new MenuCommand((s, e) => ClaudeLauncher.Launch(this), new CommandID(CmdSet, CmdLaunchClaude)));
                mcs.AddCommand(new MenuCommand((s, e) => JoinableTaskFactory.RunAsync(() => MentionAsync(tools)).FileAndForget("ClaudeVS/mention"),
                    new CommandID(CmdSet, CmdMention)));
                mcs.AddCommand(new MenuCommand((s, e) => JoinableTaskFactory.RunAsync(async () => (await ShowChatAsync()).FocusInput()).FileAndForget("ClaudeVS/chat"),
                    new CommandID(CmdSet, CmdOpenChat)));
            }

            // Pousse la sélection courante dès qu'un client se connecte (comme VS Code)
            SelectionTracker.Changed += sel => Server.Notify("selection_changed", sel);
            Log.Info($"ClaudeVS prêt — serveur MCP sur 127.0.0.1:{Server.Port}");
        }

        /// <summary>
        /// VS a une disposition de fenêtres distincte en débogage : un panneau ouvert en conception
        /// disparaît au démarrage du débogage. On le réaffiche s'il avait été ouvert dans la session.
        /// </summary>
        internal void EnsureChatVisible()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (FindToolWindow(typeof(Chat.ChatToolWindow), 0, false) is Chat.ChatToolWindow w && w.Frame is IVsWindowFrame frame
                && frame.IsVisible() != VSConstants.S_OK)
                frame.Show();
        }

        private async System.Threading.Tasks.Task<Chat.ChatToolWindow> ShowChatAsync()
        {
            var window = (Chat.ChatToolWindow)await ShowToolWindowAsync(typeof(Chat.ChatToolWindow), 0, true, DisposalToken);
            return window;
        }

        /// <summary>
        /// Envoie la sélection : au panneau s'il est ouvert, sinon au CLI du terminal s'il est connecté,
        /// sinon ouvre le panneau. Le panneau prend le focus pour qu'on puisse taper directement.
        /// </summary>
        private async Task MentionAsync(IdeTools tools)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            var mention = await tools.GetMentionAsync();
            if (mention == null) return;
            // La fenêtre reste en cache après fermeture par l'utilisateur : seul IsVisible dit si le panneau est affiché
            var panelOpen = FindToolWindow(typeof(Chat.ChatToolWindow), 0, false) is Chat.ChatToolWindow existing
                            && existing.Frame is IVsWindowFrame existingFrame && existingFrame.IsVisible() == VSConstants.S_OK;
            if (!panelOpen && Server.HasClient)
            {
                Server.Notify("at_mentioned", mention);
                return;
            }
            var chat = await ShowChatAsync();
            chat.Controller?.InsertMention((string)mention["filePath"], (int?)mention["lineStart"], (int?)mention["lineEnd"]);
            chat.FocusInput();
        }

        private void RefreshWorkspace()
        {
            JoinableTaskFactory.RunAsync(async () =>
            {
                Server.UpdateWorkspace(await Tools.GetWorkspaceFoldersAsync());
            }).FileAndForget("ClaudeVS/workspace");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                Server?.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>Écrit dans un volet « Claude » de la fenêtre Sortie.</summary>
    internal sealed class Log
    {
        private static readonly Guid PaneGuid = new Guid("9a0b7c3d-5e6f-4a1b-8c2d-3e4f5a6b7c8d");
        private readonly AsyncPackage _package;
        private IVsOutputWindowPane _pane;

        public Log(AsyncPackage package) => _package = package;

        public void Info(string message) => Write(message);
        public void Error(string message) => Write("ERREUR : " + message);

        private static readonly string LogFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ClaudeVS", "claudevs.log");
        private const long MaxLogBytes = 5 * 1024 * 1024; // au-delà, le journal est renommé en .1 (une seule génération conservée)
        private static readonly object FileGate = new object();

        private void Write(string message)
        {
            var line = $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}";
            try
            {
                lock (FileGate)
                {
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(LogFile));
                    var info = new System.IO.FileInfo(LogFile);
                    if (info.Exists && info.Length > MaxLogBytes)
                    {
                        System.IO.File.Delete(LogFile + ".1");
                        System.IO.File.Move(LogFile, LogFile + ".1");
                    }
                    System.IO.File.AppendAllText(LogFile, line);
                }
            }
            catch { }
            _package.JoinableTaskFactory.RunAsync(async () =>
            {
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (_pane == null && await _package.GetServiceAsync(typeof(SVsOutputWindow)) is IVsOutputWindow ow)
                {
                    var guid = PaneGuid;
                    ow.CreatePane(ref guid, "Claude", 1, 1);
                    ow.GetPane(ref guid, out _pane);
                }
                _pane?.OutputStringThreadSafe(line);
            }).FileAndForget("ClaudeVS/log");
        }
    }
}
