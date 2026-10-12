using System;
using System.ComponentModel.Composition;
using System.Windows.Threading;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using Newtonsoft.Json.Linq;

namespace ClaudeVS
{
    /// <summary>
    /// Suit la sélection dans les éditeurs de code et la pousse au CLI (notification selection_changed),
    /// avec un anti-rebond de 300 ms comme VS Code.
    /// </summary>
    [Export(typeof(IWpfTextViewCreationListener))]
    [ContentType("text")]
    [TextViewRole(PredefinedTextViewRoles.PrimaryDocument)]
    internal sealed class SelectionTracker : IWpfTextViewCreationListener
    {
        public static event Action<JObject> Changed;
        public static JObject Latest { get; private set; }

        private static DispatcherTimer _debounce;
        private static IWpfTextView _pendingView;

        public void TextViewCreated(IWpfTextView view)
        {
            if (!view.TextBuffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument doc)) return;
            if (DiffManager.IsTempPath(doc.FilePath)) return;

            void OnChange(object s, EventArgs e) => Schedule(view);
            view.Selection.SelectionChanged += OnChange;
            view.GotAggregateFocus += OnChange;
            view.Closed += (s, e) =>
            {
                view.Selection.SelectionChanged -= OnChange;
                view.GotAggregateFocus -= OnChange;
            };
        }

        private static void Schedule(IWpfTextView view)
        {
            _pendingView = view;
            if (_debounce == null)
            {
                _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
                _debounce.Tick += (s, e) =>
                {
                    _debounce.Stop();
                    // Une exception ici remonterait au Dispatcher WPF de VS : on la journalise
                    try
                    {
                        var v = _pendingView;
                        if (v == null || v.IsClosed) return;
                        if (!v.TextBuffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument d)) return;
                        var sel = Describe(v, d.FilePath);
                        if (Latest != null && JToken.DeepEquals(Latest, sel)) return;
                        Latest = sel;
                        Changed?.Invoke(sel);
                    }
                    catch (Exception ex)
                    {
                        ClaudeVSPackage.Instance?.Log.Error("Suivi de la sélection : " + ex.Message);
                    }
                };
            }
            _debounce.Stop();
            _debounce.Start();
        }

        public static JObject Describe(ITextView view, string path)
        {
            var span = view.Selection.StreamSelectionSpan.SnapshotSpan;
            JObject Pos(SnapshotPoint p)
            {
                var line = p.GetContainingLine();
                return new JObject { ["line"] = line.LineNumber, ["character"] = p.Position - line.Start.Position };
            }
            return new JObject
            {
                ["text"] = span.GetText(),
                ["filePath"] = path,
                // Document sans chemin exploitable (fenêtre temporaire, nom non-URI) : pas d'URL plutôt qu'une exception
                ["fileUrl"] = Uri.TryCreate(path, UriKind.Absolute, out var uri) ? uri.AbsoluteUri : null,
                ["selection"] = new JObject
                {
                    ["start"] = Pos(span.Start),
                    ["end"] = Pos(span.End),
                    ["isEmpty"] = span.IsEmpty,
                },
            };
        }
    }
}
