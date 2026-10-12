using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using Newtonsoft.Json.Linq;

namespace ClaudeVS
{
    /// <summary>Outils MCP exposés au CLI — mêmes noms, schémas et formats de réponse que l'extension VS Code.</summary>
    internal sealed class IdeTools
    {
        private readonly AsyncPackage _package;
        private readonly DiffManager _diffs;
        private readonly DebugTools _debug;
        private readonly EditorTools _editor;

        public IdeTools(AsyncPackage package, Log log)
        {
            _package = package;
            _diffs = DiffManager.GetInstance(package, log);
            _debug = new DebugTools(package, log);
            _editor = new EditorTools(package);
            // Ordre = ordre de tools/list
            Groups = new IToolGroup[]
            {
                new CoreToolGroup(this),
                _debug,
                new TestToolGroup(package, log),
                _editor,
                new RoslynTools(package, log),
            };
        }

        /// <summary>Groupes d'outils, dans l'ordre de présentation.</summary>
        public IReadOnlyList<IToolGroup> Groups { get; }

        internal EditorTools Editor => _editor;
        internal DebugTools Debug => _debug;

        public static JArray Text(params string[] texts) =>
            new JArray(texts.Select(t => new JObject { ["type"] = "text", ["text"] = t }));

        internal static JObject Result(params string[] texts) => new JObject { ["content"] = Text(texts) };
        internal static JObject Json(JToken obj) => Result(obj.ToString(Newtonsoft.Json.Formatting.Indented));

        // ---- Définitions -----------------------------------------------------------------

        internal static JObject Str(string d) => new JObject { ["type"] = "string", ["description"] = d };
        internal static JObject Int(string d) => new JObject { ["type"] = "integer", ["description"] = d };
        internal static JObject Bool(string d, bool def) => new JObject { ["type"] = "boolean", ["description"] = d, ["default"] = def };
        internal static JObject Tool(string name, string description, JObject props = null, params string[] required) => new JObject
        {
            ["name"] = name,
            ["description"] = description,
            ["inputSchema"] = new JObject
            {
                ["type"] = "object",
                ["properties"] = props ?? new JObject(),
                ["required"] = new JArray(required),
                ["additionalProperties"] = false,
                ["$schema"] = "http://json-schema.org/draft-07/schema#",
            },
        };

        /// <param name="modelOnly">Uniquement les outils exposés au CLI du panneau.</param>
        public JArray ToolDefinitions(bool modelOnly = false)
        {
            var tools = new JArray();
            foreach (var g in Groups)
                foreach (var t in g.Definitions())
                    if (!modelOnly || g.ModelVisible((string)t["name"])) tools.Add(t.DeepClone());
            return tools;
        }

        public bool IsModelVisible(string name) => Groups.Any(g => g.Handles(name) && g.ModelVisible(name));

        /// <summary>Outils de base : diff, éditeurs, sélection, diagnostics, dossiers de travail.</summary>
        private sealed class CoreToolGroup : ToolGroup
        {
            // Liste blanche : un nouvel outil de base n'est exposé au modèle que s'il est ajouté ici explicitement
            // (pas de diff, de gestion d'onglets ni de « dernière sélection » pour le modèle)
            private static readonly HashSet<string> Visible = new HashSet<string>
            {
                "getDiagnostics", "getOpenEditors", "getWorkspaceFolders", "getCurrentSelection", "openFile", "checkDocumentDirty", "saveDocument",
            };

            private readonly IdeTools _o;
            public CoreToolGroup(IdeTools owner) => _o = owner;

            public override JArray Definitions() => CoreDefinitions();
            public override bool ModelVisible(string name) => Visible.Contains(name);
            public override Task<JObject> CallAsync(string name, JObject args, CancellationToken ct) => _o.CallCoreAsync(name, args);
        }

        private static JArray CoreDefinitions()
        {
            var tools = new JArray
            {
                Tool("openDiff", "Open a git diff for the file", new JObject
                {
                    ["old_file_path"] = Str("Path to the file to show diff for. If not provided, uses active editor."),
                    ["new_file_path"] = Str("Path to the file to show diff for. If not provided, uses active editor."),
                    ["new_file_contents"] = Str("Contents of the new file. If not provided then the current file contents of new_file_path will be used."),
                    ["tab_name"] = Str("Path to the file to show diff for. If not provided, uses active editor."),
                }, "old_file_path", "new_file_path", "new_file_contents", "tab_name"),
                Tool("getDiagnostics", "Get language diagnostics from Visual Studio", new JObject
                {
                    ["uri"] = Str("Optional file URI to get diagnostics for. If not provided, gets diagnostics for all files."),
                }),
                Tool("close_tab", "", new JObject { ["tab_name"] = new JObject { ["type"] = "string" } }, "tab_name"),
                Tool("closeAllDiffTabs", "Close all diff tabs in the editor"),
                Tool("openFile", "Open a file in the editor and optionally select a range of text", new JObject
                {
                    ["filePath"] = Str("Path to the file to open"),
                    ["preview"] = Bool("Whether to open the file in preview mode (provisional tab)", false),
                    ["startText"] = Str("Text pattern to find the start of the selection range. Selects from the beginning of this match."),
                    ["endText"] = Str("Text pattern to find the end of the selection range. Selects up to the end of this match. If not provided, only the startText match will be selected."),
                    ["selectToEndOfLine"] = Bool("If true, selection will extend to the end of the line containing the endText match.", false),
                    ["makeFrontmost"] = Bool("Whether to make the file the active editor tab. If false, the file will be opened in the background without changing focus.", true),
                }, "filePath"),
                Tool("getOpenEditors", "Get information about currently open editors"),
                Tool("getWorkspaceFolders", "Get all workspace folders currently open in the IDE"),
                Tool("getCurrentSelection", "Get the current text selection in the active editor"),
                Tool("checkDocumentDirty", "Check if a document has unsaved changes (is dirty)", new JObject
                {
                    ["filePath"] = Str("Path to the file to check"),
                }, "filePath"),
                Tool("saveDocument", "Save a document with unsaved changes", new JObject
                {
                    ["filePath"] = Str("Path to the file to save"),
                }, "filePath"),
                Tool("getLatestSelection", "Get the most recent text selection (even if not in the active editor)"),
            };
            return tools;
        }

        // ---- Dispatch --------------------------------------------------------------------

        /// <param name="ct">Annulé quand le client qui a demandé l'outil se déconnecte (utilisé par les outils longs).</param>
        public async Task<JObject> CallAsync(string name, JObject args, CancellationToken ct = default)
        {
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
            var group = Groups.FirstOrDefault(g => g.Handles(name));
            if (group == null) throw new InvalidOperationException("Unknown tool: " + name);
            return await group.CallAsync(name, args, ct);
        }

        private async Task<JObject> CallCoreAsync(string name, JObject args)
        {
            switch (name)
            {
                case "openDiff":
                    return await _diffs.OpenDiffAsync((string)args["old_file_path"], (string)args["new_file_path"],
                        (string)args["new_file_contents"], (string)args["tab_name"]);
                case "close_tab":
                    _diffs.Close((string)args["tab_name"]);
                    return Result("TAB_CLOSED");
                case "closeAllDiffTabs":
                    return Result($"CLOSED_{_diffs.CloseAll()}_DIFF_TABS");
                case "getDiagnostics": return GetDiagnostics((string)args["uri"]);
                case "openFile": return await OpenFileAsync(args);
                case "getOpenEditors": return GetOpenEditors();
                case "getWorkspaceFolders": return await GetWorkspaceFoldersResultAsync();
                case "getCurrentSelection": return await GetCurrentSelectionAsync();
                case "getLatestSelection":
                    return Json(SelectionTracker.Latest ?? new JObject { ["success"] = false, ["message"] = "No selection available" });
                case "checkDocumentDirty": return CheckDirty((string)args["filePath"]);
                case "saveDocument": return Save((string)args["filePath"]);
                default:
                    throw new InvalidOperationException("Unknown tool: " + name);
            }
        }

        // ---- Workspace -------------------------------------------------------------------

        public async Task<string[]> GetWorkspaceFoldersAsync()
        {
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
            return WorkspaceFolders();
        }

        private static string[] WorkspaceFolders()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (Package.GetGlobalService(typeof(SVsSolution)) is IVsSolution solution
                && solution.GetSolutionInfo(out var dir, out _, out _) == VSConstants.S_OK
                && !string.IsNullOrEmpty(dir))
                return new[] { dir.TrimEnd('\\') };
            return new string[0];
        }

        private async Task<JObject> GetWorkspaceFoldersResultAsync()
        {
            var folders = await GetWorkspaceFoldersAsync();
            return Json(new JObject
            {
                ["success"] = true,
                ["folders"] = new JArray(folders.Select((f, i) => new JObject
                {
                    ["name"] = Path.GetFileName(f), ["uri"] = new Uri(f).AbsoluteUri, ["path"] = f, ["index"] = i,
                })),
                ["rootPath"] = folders.FirstOrDefault(),
            });
        }

        // ---- Éditeurs --------------------------------------------------------------------

        private DTE2 Dte
        {
            get
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                return (DTE2)Package.GetGlobalService(typeof(SDTE));
            }
        }

        private Document FindDocument(string path)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            foreach (Document d in Dte.Documents)
                if (string.Equals(d.FullName, path, StringComparison.OrdinalIgnoreCase))
                    return d;
            return null;
        }

        private JObject GetOpenEditors()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var dte = Dte;
            var active = dte.ActiveDocument?.FullName;
            var tabs = new JArray();
            foreach (Document d in dte.Documents)
            {
                if (string.IsNullOrEmpty(d.FullName) || DiffManager.IsTempPath(d.FullName)) continue;
                tabs.Add(new JObject
                {
                    ["uri"] = new Uri(d.FullName).AbsoluteUri,
                    ["isActive"] = string.Equals(d.FullName, active, StringComparison.OrdinalIgnoreCase),
                    ["isDirty"] = !d.Saved,
                    ["label"] = d.Name,
                    ["fileName"] = d.FullName,
                    ["languageId"] = d.Language?.ToLowerInvariant(),
                });
            }
            return Json(new JObject { ["tabs"] = tabs });
        }

        private async Task<JObject> OpenFileAsync(JObject args)
        {
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
            var path = PathResolver.Resolve((string)args["filePath"], mustExist: true);
            var startText = (string)args["startText"];
            var endText = (string)args["endText"];
            var toEol = (bool?)args["selectToEndOfLine"] ?? false;
            var preview = (bool?)args["preview"] ?? false;
            var makeFrontmost = (bool?)args["makeFrontmost"] ?? true;

            IVsWindowFrame frame;
            IVsTextView vsView;
            // Onglet provisoire (aperçu) si demandé, comme un simple clic dans l'Explorateur de solutions
            using (preview ? new NewDocumentStateScope(__VSNEWDOCUMENTSTATE.NDS_Provisional, VSConstants.NewDocumentStateReason.Navigation) : null)
                VsShellUtilities.OpenDocument(_package, path, Guid.Empty, out _, out _, out frame, out vsView);
            // makeFrontmost = false : l'onglet est ouvert sans voler le focus à l'éditeur courant
            if (makeFrontmost) frame?.Show(); else frame?.ShowNoActivate();
            if (string.IsNullOrEmpty(startText) || vsView == null)
                return Result("Opened file: " + path);

            var view = await GetWpfViewAsync(vsView);
            if (view == null) return Result("Opened file: " + path + " (not a text editor: selection skipped)");
            var snapshot = view.TextSnapshot;
            var text = snapshot.GetText();
            var start = text.IndexOf(startText, StringComparison.Ordinal);
            if (start < 0) return Result($"Opened file, but text \"{startText}\" not found");

            var end = start + startText.Length;
            string message = $"Opened file and selected text \"{startText}\"";
            if (!string.IsNullOrEmpty(endText))
            {
                var e = text.IndexOf(endText, end, StringComparison.Ordinal);
                if (e >= 0)
                {
                    end = e + endText.Length;
                    if (toEol) end = snapshot.GetLineFromPosition(end).End.Position;
                    message = $"Opened file and selected text from \"{startText}\" to \"{endText}\"";
                }
                else
                {
                    end = start;
                    message = $"Opened file and positioned at \"{startText}\" (end text \"{endText}\" not found)";
                }
            }
            var span = new SnapshotSpan(snapshot, start, end - start);
            view.Selection.Select(span, false);
            view.Caret.MoveTo(span.End);
            view.ViewScroller.EnsureSpanVisible(span, EnsureSpanVisibleOptions.AlwaysCenter);
            return Result(message);
        }

        private JObject CheckDirty(string filePath)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var path = PathResolver.Resolve(filePath, mustExist: false);
            var doc = FindDocument(path);
            if (doc == null)
                return Json(new JObject { ["success"] = false, ["message"] = "Document not open: " + path });
            return Json(new JObject { ["success"] = true, ["filePath"] = path, ["isDirty"] = !doc.Saved, ["isUntitled"] = false });
        }

        private JObject Save(string filePath)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var path = PathResolver.Resolve(filePath, mustExist: false);
            var doc = FindDocument(path);
            if (doc == null)
                return Json(new JObject { ["success"] = false, ["message"] = "Document not open: " + path });
            var wasDirty = !doc.Saved;
            // Save() peut échouer ou être annulé par l'utilisateur (boîte de dialogue) : on lit son résultat
            var status = wasDirty ? doc.Save() : vsSaveStatus.vsSaveSucceeded;
            var saved = wasDirty && status == vsSaveStatus.vsSaveSucceeded;
            return Json(new JObject
            {
                ["success"] = status == vsSaveStatus.vsSaveSucceeded, ["filePath"] = path, ["saved"] = saved,
                ["message"] = saved ? "Document saved successfully"
                    : !wasDirty ? "Document was not dirty"
                    : status == vsSaveStatus.vsSaveCancelled ? "Save cancelled by the user" : "Save failed (" + status + ")",
            });
        }

        // ---- Sélection -------------------------------------------------------------------

        private async Task<IWpfTextView> GetWpfViewAsync(IVsTextView vsView)
        {
            var componentModel = (IComponentModel)await _package.GetServiceAsync(typeof(SComponentModel));
            return componentModel.GetService<IVsEditorAdaptersFactoryService>().GetWpfTextView(vsView);
        }

        private async Task<(IWpfTextView view, string path)> GetActiveViewAsync()
        {
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
            var textManager = (IVsTextManager)await _package.GetServiceAsync(typeof(SVsTextManager));
            if (textManager.GetActiveView(1, null, out var vsView) != VSConstants.S_OK || vsView == null)
                return (null, null);
            var view = await GetWpfViewAsync(vsView);
            if (view == null || !view.TextBuffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument doc))
                return (null, null);
            return (view, doc.FilePath);
        }

        private async Task<JObject> GetCurrentSelectionAsync()
        {
            var (view, path) = await GetActiveViewAsync();
            if (view == null)
                return Json(new JObject { ["success"] = false, ["message"] = "No active editor found" });
            var sel = SelectionTracker.Describe(view, path);
            sel["success"] = true;
            return Json(sel);
        }

        /// <summary>Commande « Envoyer la sélection à Claude » : fichier actif et lignes sélectionnées (format at_mentioned).</summary>
        public async Task<JObject> GetMentionAsync()
        {
            var (view, path) = await GetActiveViewAsync();
            if (view == null) return null;
            var p = new JObject { ["filePath"] = path };
            if (!view.Selection.IsEmpty)
            {
                var span = view.Selection.StreamSelectionSpan.SnapshotSpan;
                p["lineStart"] = span.Start.GetContainingLine().LineNumber;
                p["lineEnd"] = span.End.GetContainingLine().LineNumber;
            }
            return p;
        }

        // ---- Diagnostics -----------------------------------------------------------------

        private JObject GetDiagnostics(string uri)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string filter = string.IsNullOrEmpty(uri) ? null : PathResolver.Resolve(uri, mustExist: false);
            var byFile = new Dictionary<string, JArray>(StringComparer.OrdinalIgnoreCase);
            var items = Dte.ToolWindows.ErrorList.ErrorItems;
            for (var i = 1; i <= items.Count; i++)
            {
                var item = items.Item(i);
                var file = item.FileName;
                if (string.IsNullOrEmpty(file)) continue;
                if (filter != null && !string.Equals(Path.GetFullPath(file), filter, StringComparison.OrdinalIgnoreCase)) continue;
                if (!byFile.TryGetValue(file, out var list)) byFile[file] = list = new JArray();
                var pos = new JObject { ["line"] = Math.Max(0, item.Line - 1), ["character"] = Math.Max(0, item.Column - 1) };
                list.Add(new JObject
                {
                    ["message"] = item.Description,
                    ["severity"] = item.ErrorLevel == vsBuildErrorLevel.vsBuildErrorLevelHigh ? "Error"
                        : item.ErrorLevel == vsBuildErrorLevel.vsBuildErrorLevelMedium ? "Warning" : "Information",
                    ["range"] = new JObject { ["start"] = pos, ["end"] = pos.DeepClone() },
                    ["source"] = item.Project,
                });
            }
            if (filter != null && byFile.Count == 0) byFile[filter] = new JArray();
            return Json(new JArray(byFile.Select(kv => new JObject
            {
                ["uri"] = new Uri(kv.Key).AbsoluteUri,
                ["diagnostics"] = kv.Value,
            })));
        }
    }
}
