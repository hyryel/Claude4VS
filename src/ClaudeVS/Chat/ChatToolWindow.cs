using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ClaudeVS.Chat
{
    [Guid("6c2b8f4e-9a1d-4e7b-b3c5-0d8e2f1a7c94")]
    public sealed class ChatToolWindow : ToolWindowPane
    {
        public ChatToolWindow() : base(null)
        {
            Caption = "Claude";
        }

        protected override void Initialize()
        {
            base.Initialize();
            Content = new ChatControl((ClaudeVSPackage)Package);
        }

        internal ChatController Controller => (Content as ChatControl)?.Controller;

        internal void FocusInput() => (Content as ChatControl)?.FocusInput();

        protected override void Dispose(bool disposing)
        {
            if (disposing) (Content as IDisposable)?.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>Héberge l'interface web (Chat/web) dans WebView2 et relaie les messages vers le contrôleur.</summary>
    internal sealed class ChatControl : UserControl, IDisposable
    {
        private const string Host = "claudevs.local";
        private readonly ClaudeVSPackage _package;
        private readonly WebView2 _web = new WebView2();
        private readonly List<string> _queue = new List<string>();
        private bool _uiReady;

        public ChatController Controller { get; }

        public ChatControl(ClaudeVSPackage package)
        {
            _package = package;
            Content = _web;
            Controller = new ChatController(package, Post);
            Loaded += (s, e) => _package.JoinableTaskFactory.RunAsync(InitAsync).FileAndForget("ClaudeVS/webview");
            VSColorTheme.ThemeChanged += e => PostTheme();
        }

        private bool _initialized;

        private async System.Threading.Tasks.Task InitAsync()
        {
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (_initialized) return;
            _initialized = true;
            try
            {
                // Un dossier de données par instance de VS (version, suffixe racine) et par niveau d'élévation :
                // WebView2 refuse de partager un dossier entre processus d'options ou d'élévation différentes (0x8007139F)
                var dataDir = Path.Combine(VsAppDataDir(), "ClaudeVS", "WebView2" + (IsElevated() ? "-admin" : ""));
                // Débogage : CLAUDEVS_WEBVIEW_DEBUG_PORT=9333 expose le panneau au protocole DevTools
                var debugPort = Environment.GetEnvironmentVariable("CLAUDEVS_WEBVIEW_DEBUG_PORT");
                var options = new CoreWebView2EnvironmentOptions(
                    string.IsNullOrEmpty(debugPort) ? null : "--remote-debugging-port=" + debugPort);
                var env = await CoreWebView2Environment.CreateAsync(null, dataDir, options);
                await _web.EnsureCoreWebView2Async(env);
                var core = _web.CoreWebView2;

                var webRoot = Path.Combine(Path.GetDirectoryName(typeof(ChatControl).Assembly.Location), "Chat", "web");
                core.SetVirtualHostNameToFolderMapping(Host, webRoot, CoreWebView2HostResourceAccessKind.Deny);
                core.Settings.AreDevToolsEnabled = true;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.IsZoomControlEnabled = true;

                core.WebMessageReceived += (s, e) =>
                {
                    JObject msg;
                    try { msg = JObject.Parse(e.WebMessageAsJson); } catch { return; }
                    if ((string)msg["type"] == "ready")
                    {
                        _uiReady = true;
                        _package.Log.Info("Interface du panneau prête.");
                        PostTheme();
                        foreach (var json in _queue) core.PostWebMessageAsJson(json);
                        _queue.Clear();
                    }
                    try { Controller.HandleUiMessage(msg); }
                    catch (Exception ex) { _package.Log.Error("Message de l'interface : " + ex.Message); }
                };

                // Les liens externes s'ouvrent dans le navigateur, jamais dans le panneau
                core.NewWindowRequested += (s, e) => { e.Handled = true; OpenExternal(e.Uri); };
                core.NavigationStarting += (s, e) =>
                {
                    if (!e.Uri.StartsWith($"https://{Host}/", StringComparison.OrdinalIgnoreCase))
                    {
                        e.Cancel = true;
                        OpenExternal(e.Uri);
                    }
                };

                _web.Source = new Uri($"https://{Host}/index.html");
            }
            catch (Exception ex)
            {
                _package.Log.Error("WebView2 : " + ex);
                Content = new TextBlock
                {
                    Text = "Impossible d'initialiser WebView2 : " + ex.Message,
                    TextWrapping = System.Windows.TextWrapping.Wrap,
                    Margin = new System.Windows.Thickness(12),
                };
            }
        }

        /// <summary>Dossier local de cette instance de VS, ex. %LOCALAPPDATA%\Microsoft\VisualStudio\18.0_xxxxExp.</summary>
        private static string VsAppDataDir()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (Package.GetGlobalService(typeof(SVsShell)) is IVsShell shell
                && shell.GetProperty((int)__VSSPROPID.VSSPROPID_AppDataDir, out var dir) == VSConstants.S_OK
                && dir is string s && !string.IsNullOrEmpty(s))
                return s;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "VisualStudio", "ClaudeVS-unknown");
        }

        private static bool IsElevated()
        {
            try
            {
                using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
                    return new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        private static void OpenExternal(string uri)
        {
            if (uri != null && (uri.StartsWith("http://") || uri.StartsWith("https://")))
                try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); } catch { }
        }

        /// <summary>Donne le focus à la zone de saisie du panneau.</summary>
        public void FocusInput()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _web.Focus();
            Post(new JObject { ["type"] = "focus" });
        }

        /// <summary>Envoie un message à l'interface (thread quelconque).</summary>
        private void Post(JObject message)
        {
            var json = message.ToString(Formatting.None);
            _package.JoinableTaskFactory.RunAsync(async () =>
            {
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (_uiReady && _web.CoreWebView2 != null) _web.CoreWebView2.PostWebMessageAsJson(json);
                else _queue.Add(json);
            }).FileAndForget("ClaudeVS/post");
        }

        /// <summary>Couleurs du thème VS, appliquées en variables CSS.</summary>
        private void PostTheme()
        {
            string Hex(ThemeResourceKey key)
            {
                var c = VSColorTheme.GetThemedColor(key);
                return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
            }
            var bg = VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowBackgroundColorKey);
            Post(new JObject
            {
                ["type"] = "theme",
                ["dark"] = (bg.R * 299 + bg.G * 587 + bg.B * 114) / 1000 < 128,
                ["vars"] = new JObject
                {
                    ["--bg"] = Hex(EnvironmentColors.ToolWindowBackgroundColorKey),
                    ["--fg"] = Hex(EnvironmentColors.ToolWindowTextColorKey),
                    ["--border"] = Hex(EnvironmentColors.ToolWindowBorderColorKey),
                    ["--accent"] = Hex(EnvironmentColors.SystemHighlightColorKey),
                    ["--input-bg"] = Hex(EnvironmentColors.ComboBoxBackgroundColorKey),
                    ["--input-fg"] = Hex(EnvironmentColors.ComboBoxTextColorKey),
                    ["--input-border"] = Hex(EnvironmentColors.ComboBoxBorderColorKey),
                },
            });
        }

        public void Dispose()
        {
            Controller.Dispose();
            try { _web.Dispose(); } catch { }
        }
    }
}
