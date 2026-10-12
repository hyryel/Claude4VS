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
using Task = System.Threading.Tasks.Task;

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

        internal void FocusInput()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            (Content as ChatControl)?.FocusInput();
        }

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
        private const int MaxQueued = 500;   // messages gardés en attendant que l'interface soit prête
        private readonly ClaudeVSPackage _package;
        private readonly ThemeChangedEventHandler _onThemeChanged;
        private IWebView2 _web = CreateWebView();

        private static bool _compositionFailed;   // contrôle composé en échec une fois : WebView2 classique jusqu'au redémarrage

        /// <summary>
        /// WebView2CompositionControl (rendu composé par WPF) : un panneau de VS posé par-dessus masque le chat et reçoit
        /// ses clics. Public dans la DLL de VS 2026, encore interne dans celle de VS 2022 (1.0.2903.40) mais présent :
        /// créé par réflexion dans les deux cas. Repli sur WebView2, fenêtre native toujours dessinée devant le contenu WPF.
        /// </summary>
        private static IWebView2 CreateWebView()
        {
            var composition = typeof(WebView2).Assembly.GetType("Microsoft.Web.WebView2.Wpf.WebView2CompositionControl");
            if (!_compositionFailed && composition != null && typeof(IWebView2).IsAssignableFrom(composition))
            {
                try { return (IWebView2)Activator.CreateInstance(composition, nonPublic: true); }
                catch (Exception) { _compositionFailed = true; }
            }
            return new WebView2();
        }

        private readonly List<string> _queue = new List<string>();
        private bool _uiReady;
        private bool _initFailed;
        private bool _disposed;

        public ChatController Controller { get; }

        public ChatControl(ClaudeVSPackage package)
        {
            _package = package;
            Content = _web;
            Controller = new ChatController(package, Post);
            Loaded += (s, e) => RunOnUi(InitAsync, "webview");
            _onThemeChanged = e => PostTheme();
            VSColorTheme.ThemeChanged += _onThemeChanged;
        }

        /// <summary>Bascule sur le thread UI et journalise toute exception du corps (pas de PostError ici : on journalise).</summary>
        private void RunOnUi(Func<Task> body, string name)
        {
            _package.JoinableTaskFactory.RunAsync(async () =>
            {
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                try { await body(); }
                catch (Exception ex) { _package.Log.Error(name + " : " + ex); }
            }).FileAndForget("ClaudeVS/" + name);
        }

        private bool _initialized;

        private async Task InitAsync()
        {
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (_initialized || _disposed) return;
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
                // Outils de développement : en build Debug, ou à la demande par la variable d'environnement
                var devTools = !string.IsNullOrEmpty(debugPort);
#if DEBUG
                devTools = true;
#endif
                core.Settings.AreDevToolsEnabled = devTools;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.IsZoomControlEnabled = true;

                core.WebMessageReceived += (s, e) =>
                {
                    JObject msg;
                    try { msg = JObject.Parse(e.WebMessageAsJson); }
                    catch (Exception ex) { _package.Log.Error("Message de l'interface illisible : " + ex.Message); return; }
                    if ((string)msg["type"] == ToHost.Ready)
                    {
                        _uiReady = true;
                        _package.Log.Info("Interface du panneau prête.");
                        PostTheme();
                        foreach (var json in _queue) core.PostWebMessageAsJson(json);
                        _queue.Clear();
                    }
                    try { Controller.HandleUiMessage(msg); }
                    catch (Exception ex) { _package.Log.Error("Message de l'interface : " + ex); }
                };

                // Plantage d'un processus WebView2 : sans cela le panneau reste blanc jusqu'au redémarrage de VS
                core.ProcessFailed += (s, e) => OnProcessFailed(e);

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
            catch (Exception ex) when (!(_web is WebView2) && !_disposed)
            {
                // Contrôle composé inutilisable (DLL de VS trop ancienne ?) : nouvel essai avec WebView2 classique
                _package.Log.Error("WebView2 (rendu composé), repli sur WebView2 classique : " + ex);
                _compositionFailed = true;
                try { _web.Dispose(); } catch { }
                _web = CreateWebView();
                Content = _web;
                _initialized = false;
                RunOnUi(InitAsync, "webview");
            }
            catch (Exception ex)
            {
                _package.Log.Error("WebView2 : " + ex);
                _initFailed = true;
                _queue.Clear();
                Content = new TextBlock
                {
                    Text = "Impossible d'initialiser WebView2 : " + ex.Message,
                    TextWrapping = System.Windows.TextWrapping.Wrap,
                    Margin = new System.Windows.Thickness(12),
                };
            }
        }

        /// <summary>
        /// Rendu planté : rechargement de la page (l'interface renvoie « ready » et repart à vide).
        /// Processus navigateur mort : le contrôle est inutilisable, on en recrée un.
        /// </summary>
        private void OnProcessFailed(CoreWebView2ProcessFailedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _package.Log.Error($"WebView2 : échec du processus {e.ProcessFailedKind} ({e.Reason}) {e.ProcessDescription}");
            if (_disposed) return;
            // Bandeau orange (comme le changement de solution), livré après le prochain « ready » de la page rechargée
            var banner = new JObject
            {
                ["type"] = FromHost.Banner,
                ["text"] = "Le panneau a été rechargé après un plantage de WebView2.\n"
                         + "La conversation se poursuit ; son affichage est disponible via le bouton Conversations précédentes (🕘).",
            };
            try
            {
                switch (e.ProcessFailedKind)
                {
                    case CoreWebView2ProcessFailedKind.BrowserProcessExited:
                        _uiReady = false;
                        try { _web.Dispose(); } catch { }
                        _web = CreateWebView();
                        Content = _web;
                        _initialized = false;
                        Post(banner);
                        RunOnUi(InitAsync, "webview");
                        break;
                    case CoreWebView2ProcessFailedKind.RenderProcessExited:
                        _uiReady = false;
                        Post(banner);
                        _web.CoreWebView2?.Reload();
                        break;
                    case CoreWebView2ProcessFailedKind.RenderProcessUnresponsive:
                        // Simple lenteur (gros rendu) : Chromium se remet seul, ou signale ensuite RenderProcessExited
                        _package.Log.Info("WebView2 : rendu temporairement sans réponse.");
                        break;
                }
            }
            catch (Exception ex) { _package.Log.Error("Récupération WebView2 : " + ex); }
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
            if (_disposed) return;
            _web.Focus();
            Post(new JObject { ["type"] = FromHost.Focus });
        }

        /// <summary>Envoie un message à l'interface (thread quelconque). Sans effet après Dispose ou si WebView2 n'a pas pu démarrer.</summary>
        private void Post(JObject message)
        {
            if (_disposed || _initFailed) return;
            var json = message.ToString(Formatting.None);
            RunOnUi(() =>
            {
                if (_disposed || _initFailed) return Task.CompletedTask;
                if (_uiReady && _web.CoreWebView2 != null) _web.CoreWebView2.PostWebMessageAsJson(json);
                else
                {
                    // Interface pas encore prête : on garde les derniers messages, sans accumuler sans fin
                    if (_queue.Count >= MaxQueued) _queue.RemoveAt(0);
                    _queue.Add(json);
                }
                return Task.CompletedTask;
            }, "post");
        }

        /// <summary>Couleurs du thème VS, appliquées en variables CSS.</summary>
        private void PostTheme()
        {
            if (_disposed) return;
            string Hex(ThemeResourceKey key)
            {
                var c = VSColorTheme.GetThemedColor(key);
                return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
            }
            var bg = VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowBackgroundColorKey);
            Post(new JObject
            {
                ["type"] = FromHost.Theme,
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
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_disposed) return;
            _disposed = true;
            VSColorTheme.ThemeChanged -= _onThemeChanged;
            _queue.Clear();
            Controller.Dispose();
            try { _web.Dispose(); } catch { }
        }
    }
}
