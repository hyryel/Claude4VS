using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.TextManager.Interop;
using Newtonsoft.Json.Linq;

namespace ClaudeVS
{
    /// <summary>
    /// Formatage de documents et signets, via les commandes de VS (il n'existe pas d'API publique pour les signets).
    /// </summary>
    internal sealed class EditorTools
    {
        public static readonly HashSet<string> Names = new HashSet<string> { "formatDocument", "bookmarkList", "bookmarkToggle" };

        private readonly AsyncPackage _package;

        public EditorTools(AsyncPackage package) => _package = package;

        public static JArray Definitions() => new JArray
        {
            IdeTools.Tool("formatDocument", "Format a source file with Visual Studio's Format Document (Ctrl+K, Ctrl+D), using the user's formatting settings, and save it.", new JObject
            {
                ["file"] = IdeTools.Str("Path to the file (absolute or relative to the solution)."),
            }, "file"),
            IdeTools.Tool("bookmarkList", "List the Visual Studio bookmarks (file, line, line text) across the solution."),
            IdeTools.Tool("bookmarkToggle", "Add a bookmark at a file and line, or remove it if one is already there.", new JObject
            {
                ["file"] = IdeTools.Str("Path to the file."),
                ["line"] = IdeTools.Int("1-based line number."),
            }, "file", "line"),
        };

        private DTE2 Dte
        {
            get
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                return (DTE2)Package.GetGlobalService(typeof(SDTE));
            }
        }

        public async Task<JObject> CallAsync(string name, JObject args)
        {
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
            switch (name)
            {
                case "formatDocument": return IdeTools.Json(FormatDocument(Resolve((string)args["file"]), closeIfNotOpen: false));
                case "bookmarkList": return IdeTools.Json(new JObject { ["bookmarks"] = ListBookmarks() });
                case "bookmarkToggle": return BookmarkToggle(Resolve((string)args["file"]), (int?)args["line"] ?? 0);
                default: throw new InvalidOperationException("Unknown tool: " + name);
            }
        }

        private string Resolve(string file)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (string.IsNullOrEmpty(file)) throw new ArgumentException("file is required");
            if (!Path.IsPathRooted(file))
            {
                var sln = Dte.Solution?.FullName;
                var project = Dte.Solution?.Projects.Cast<Project>().FirstOrDefault(p => !string.IsNullOrEmpty(p.FullName))?.FullName;
                var baseDir = Path.GetDirectoryName(!string.IsNullOrEmpty(sln) ? sln : project ?? "");
                if (!string.IsNullOrEmpty(baseDir)) file = Path.Combine(baseDir, file);
            }
            file = Path.GetFullPath(file);
            if (!File.Exists(file)) throw new FileNotFoundException("File not found: " + file);
            return file;
        }

        private Document FindDocument(string path)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            foreach (Document d in Dte.Documents)
                if (string.Equals(d.FullName, path, StringComparison.OrdinalIgnoreCase)) return d;
            return null;
        }

        private static string BufferText(Document doc)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var td = (TextDocument)doc.Object("TextDocument");
            return td.StartPoint.CreateEditPoint().GetText(td.EndPoint);
        }

        // ---- Formatage -----------------------------------------------------------------------

        /// <summary>
        /// Ouvre le fichier (en rechargeant la version disque si VS avait une copie périmée), applique
        /// Edit.FormatDocument et enregistre. Refuse si le document a des modifications non enregistrées.
        /// </summary>
        public JObject FormatDocument(string path, bool closeIfNotOpen)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var doc = FindDocument(path);
            var wasOpen = doc != null;
            var onDisk = File.ReadAllText(path);
            if (doc != null)
            {
                if (!doc.Saved)
                    throw new InvalidOperationException("The document has unsaved changes in Visual Studio; save or discard them first.");
                // Le fichier vient d'être modifié sur disque (par Claude) et VS ne l'a pas encore rechargé
                if (Normalize(BufferText(doc)) != Normalize(onDisk))
                {
                    doc.Close(vsSaveChanges.vsSaveChangesNo);
                    doc = null;
                }
            }

            VsShellUtilities.OpenDocument(_package, path, Guid.Empty, out _, out _, out var frame, out IVsTextView _);
            frame?.Show();
            doc = FindDocument(path) ?? throw new InvalidOperationException("Could not open the document in Visual Studio.");

            Dte.ExecuteCommand("Edit.FormatDocument");
            var formatted = BufferText(doc);
            var changed = Normalize(formatted) != Normalize(onDisk);
            if (changed) doc.Save();
            if (!wasOpen && closeIfNotOpen) doc.Close(vsSaveChanges.vsSaveChangesNo);

            return new JObject { ["file"] = path, ["formatted"] = true, ["changed"] = changed };
        }

        private static string Normalize(string s) => s?.Replace("\r\n", "\n");

        // ---- Signets ---------------------------------------------------------------------------

        private JObject BookmarkToggle(string path, int line)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (line < 1) throw new ArgumentException("line must be >= 1");
            var wasOpen = FindDocument(path) != null;
            VsShellUtilities.OpenDocument(_package, path, Guid.Empty, out _, out _, out var frame, out IVsTextView view);
            frame?.Show();
            view.SetCaretPos(line - 1, 0);
            var before = HasBookmarkAt(path, line);
            Dte.ExecuteCommand("Edit.ToggleBookmark");
            var after = HasBookmarkAt(path, line);
            return IdeTools.Json(new JObject
            {
                ["file"] = path, ["line"] = line,
                ["action"] = after ? "added" : "removed",
                ["bookmarks"] = ListBookmarks(),
            });
        }

        /// <summary>Un signet est un marqueur de type MARKER_BOOKMARK dans le tampon du document ouvert.</summary>
        private bool HasBookmarkAt(string path, int line)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!(new RunningDocumentTable(_package).FindDocument(path) is IVsTextLines lines)) return false;
            if (lines.EnumMarkers(line - 1, 0, line - 1, 0, (int)MARKERTYPE.MARKER_BOOKMARK,
                    (uint)ENUMMARKERFLAGS.EM_ENTIREBUFFER, out var markers) != VSConstants.S_OK || markers == null)
                return false;
            while (markers.Next(out var marker) == VSConstants.S_OK && marker != null)
            {
                var span = new TextSpan[1];
                if (marker.GetCurrentSpan(span) == VSConstants.S_OK && span[0].iStartLine == line - 1) return true;
            }
            return false;
        }

        /// <summary>
        /// Parcourt les signets avec Edit.NextBookmark jusqu'à revenir au premier. Les documents ouverts
        /// pour l'occasion sont refermés et le document actif est restauré.
        /// </summary>
        private JArray ListBookmarks()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var result = new JArray();
            var openBefore = new HashSet<string>(Dte.Documents.Cast<Document>().Select(d => d.FullName), StringComparer.OrdinalIgnoreCase);
            var startDoc = Dte.ActiveDocument;
            var startPath = startDoc?.FullName;
            var startLine = (startDoc?.Selection as TextSelection)?.ActivePoint.Line ?? 0;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            (string path, int line)? first = null;
            for (var i = 0; i < 500; i++)
            {
                try { Dte.ExecuteCommand("Edit.NextBookmark"); }
                catch { break; } // commande indisponible : aucun document, pas de signet
                var doc = Dte.ActiveDocument;
                if (doc == null || !(doc.Selection is TextSelection sel)) break;
                var key = (doc.FullName, sel.ActivePoint.Line);
                if (first == null)
                {
                    // Position inchangée après NextBookmark : zéro signet, ou un seul sous le curseur
                    if (key.FullName == startPath && key.Line == startLine && !HasBookmarkAt(doc.FullName, key.Line)) break;
                    first = key;
                }
                else if (key == first.Value) break;
                if (!seen.Add(key.FullName + ":" + key.Line)) break;
                var lineText = sel.ActivePoint.CreateEditPoint().GetLines(key.Line, key.Line + 1).Trim();
                result.Add(new JObject { ["file"] = doc.FullName, ["line"] = key.Line, ["text"] = lineText });
            }

            // Restauration : documents ouverts par le parcours refermés, document de départ réactivé
            foreach (var d in Dte.Documents.Cast<Document>().ToList())
                if (!openBefore.Contains(d.FullName) && d.Saved) d.Close(vsSaveChanges.vsSaveChangesNo);
            if (startPath != null && FindDocument(startPath) is Document sd)
            {
                sd.Activate();
                if (startLine > 0 && sd.Selection is TextSelection s) s.MoveToLineAndOffset(startLine, 1);
            }
            return new JArray(result.OrderBy(b => (string)b["file"], StringComparer.OrdinalIgnoreCase).ThenBy(b => (int)b["line"]));
        }
    }
}
