using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.LanguageServices;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Newtonsoft.Json.Linq;

namespace ClaudeVS
{
    /// <summary>
    /// Outils sémantiques via l'espace de travail Roslyn de VS (C# et VB) : navigation, références,
    /// diagnostics et renommage de toute la solution. Le but est d'épargner au modèle la lecture des
    /// fichiers pour retrouver des symboles ou des références.
    /// </summary>
    internal sealed class RoslynTools : ToolGroup
    {
        private const int MaxResults = 300;
        private static readonly TimeSpan MaxDuration = TimeSpan.FromSeconds(120); // borne des opérations Roslyn (solution énorme ou en cours de chargement)
        private static readonly TimeSpan LongDuration = TimeSpan.FromMinutes(10); // renommage et diagnostics de toute la solution : compilation complète
        private const int ConflictCheckMaxDocuments = 25; // au-delà, la vérification des conflits de renommage coûterait des minutes
        private readonly AsyncPackage _package;
        private readonly Log _log;

        public RoslynTools(AsyncPackage package, Log log)
        {
            _package = package;
            _log = log;
        }

        private static JObject SymbolArgs() => new JObject
        {
            ["file"] = IdeTools.Str("Source file containing the symbol (with 'line'). Use this form when the symbol name is ambiguous."),
            ["line"] = IdeTools.Int("1-based line of the symbol in 'file'."),
            ["column"] = IdeTools.Int("1-based column in that line (optional). Without it, 'name' is searched in the line, else the first identifier is used."),
            ["name"] = IdeTools.Str("Symbol name, used alone (solution-wide lookup, must be unique) or to locate it in the line. May be qualified, e.g. 'Invoice.Total'."),
        };

        public override JArray Definitions() => new JArray
        {
            IdeTools.Tool("findSymbol", "Find types and members by name across the whole solution (Roslyn, C#/VB), without reading files. Returns kind, full name and definition location.", new JObject
            {
                ["query"] = IdeTools.Str("Name or part of a name (case-insensitive). E.g. 'Invoice', 'ComputeTotal'."),
                ["kind"] = new JObject { ["type"] = "string", ["enum"] = new JArray("type", "method", "property", "field", "event", "any"), ["description"] = "Restrict to a kind (default any)." },
            }, "query"),
            IdeTools.Tool("symbolInfo", "Describe a symbol: signature, kind, containing type, base types/interfaces, documentation, and where it is defined (go to definition).", SymbolArgs()),
            IdeTools.Tool("findReferences", "Find all references to a symbol across the solution (Roslyn, exact semantic matches, not text search). Returns file, line and the line text for each usage.", SymbolArgs()),
            IdeTools.Tool("documentSymbols", "Outline of a source file: namespaces, types and members with their lines and signatures, without reading the whole file.", new JObject
            {
                ["file"] = IdeTools.Str("Path to the source file."),
            }, "file"),
            IdeTools.Tool("semanticDiagnostics", "Compiler diagnostics from Roslyn (syntax and semantic errors/warnings; code analyzers are not run), with exact ranges, without building. For one file, or the whole solution.", new JObject
            {
                ["file"] = IdeTools.Str("Optional source file. Without it, all projects are analyzed (slower on large solutions)."),
                ["minSeverity"] = new JObject { ["type"] = "string", ["enum"] = new JArray("error", "warning", "info"), ["description"] = "Minimum severity (default warning)." },
            }),
            IdeTools.Tool("renameSymbol", "Rename a symbol everywhere in the solution with Visual Studio's rename refactoring (all references, overloads, comments), like F2 in the IDE. Much cheaper than editing files one by one. Undoable with Ctrl+Z in Visual Studio.", new JObject
            {
                ["file"] = SymbolArgs()["file"], ["line"] = SymbolArgs()["line"], ["column"] = SymbolArgs()["column"], ["name"] = SymbolArgs()["name"],
                ["newName"] = IdeTools.Str("The new name."),
                ["renameFile"] = IdeTools.Bool("Also rename the file when it is named after the type.", false),
                ["renameInStrings"] = IdeTools.Bool("Also rename occurrences inside string literals.", false),
                ["renameInComments"] = IdeTools.Bool("Also rename occurrences inside comments.", true),
                ["preview"] = IdeTools.Bool("Only list the files that would change, without applying.", false),
            }, "newName"),
        };

        // ---- Accès à l'espace de travail -------------------------------------------------------

        private async Task<VisualStudioWorkspace> GetWorkspaceAsync()
        {
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
            var componentModel = (IComponentModel)Package.GetGlobalService(typeof(SComponentModel));
            var ws = componentModel?.GetService<VisualStudioWorkspace>();
            if (ws == null) throw new InvalidOperationException("Roslyn workspace unavailable (no C#/VB solution loaded?).");
            if (!ws.CurrentSolution.Projects.Any()) throw new InvalidOperationException("No C#/VB project is loaded in Visual Studio.");
            return ws;
        }

        /// <param name="outer">Annulé si le client se déconnecte ; un délai maximal s'y ajoute.</param>
        public override async Task<JObject> CallAsync(string name, JObject args, CancellationToken outer)
        {
            var ws = await GetWorkspaceAsync();
            var solution = ws.CurrentSolution;
            // Le chemin se résout sur le thread principal (solution VS), avant de passer en arrière-plan
            var file = (string)args["file"];
            if (!string.IsNullOrEmpty(file)) args["file"] = PathResolver.Resolve(file, mustExist: true);
            await TaskScheduler.Default; // Roslyn travaille hors du thread principal
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(outer))
            {
                var limit = name == "renameSymbol" || (name == "semanticDiagnostics" && string.IsNullOrEmpty(file)) ? LongDuration : MaxDuration;
                cts.CancelAfter(limit);
                var ct = cts.Token;
                try
                {
                    switch (name)
                    {
                        case "findSymbol": return await FindSymbolAsync(solution, (string)args["query"], (string)args["kind"] ?? "any", ct);
                        case "symbolInfo": return IdeTools.Json(Describe(await ResolveAsync(solution, args, ct), ct));
                        case "findReferences": return await FindReferencesAsync(solution, await ResolveAsync(solution, args, ct), ct);
                        case "documentSymbols": return await DocumentSymbolsAsync(solution, (string)args["file"], ct);
                        case "semanticDiagnostics": return await DiagnosticsAsync(solution, (string)args["file"], (string)args["minSeverity"] ?? "warning", ct);
                        case "renameSymbol": return await RenameAsync(ws, solution, args, ct);
                        default: throw new InvalidOperationException("Unknown tool: " + name);
                    }
                }
                catch (OperationCanceledException) when (!outer.IsCancellationRequested)
                {
                    throw new TimeoutException($"{name} did not complete within {limit.TotalSeconds:0} s (solution still loading, or too large). Retry, or narrow the request to one file.");
                }
            }
        }

        // ---- Résolution d'un symbole ---------------------------------------------------------------

        /// <summary>Document Roslyn d'un chemin déjà résolu par <see cref="PathResolver"/> (absolu).</summary>
        private static Document FindDocument(Solution solution, string file)
        {
            if (string.IsNullOrEmpty(file)) throw new ArgumentException("file is required");
            var id = solution.GetDocumentIdsWithFilePath(file).FirstOrDefault()
                     ?? throw new FileNotFoundException("File is not part of a loaded C#/VB project: " + file);
            return solution.GetDocument(id);
        }

        private static async Task<ISymbol> ResolveAsync(Solution solution, JObject args, CancellationToken ct)
        {
            var file = (string)args["file"];
            var name = (string)args["name"];
            if (!string.IsNullOrEmpty(file) && args["line"] != null)
                return await SymbolAtAsync(solution, file, (int)args["line"], (int?)args["column"], name, ct);
            if (string.IsNullOrEmpty(name))
                throw new ArgumentException("Give either 'name', or 'file' and 'line' (plus 'column' or 'name').");
            return await SymbolByNameAsync(solution, name, ct);
        }

        private static async Task<ISymbol> SymbolAtAsync(Solution solution, string file, int line, int? column, string name, CancellationToken ct)
        {
            var doc = FindDocument(solution, file);
            var text = await doc.GetTextAsync(ct);
            if (line < 1 || line > text.Lines.Count) throw new ArgumentException($"line must be between 1 and {text.Lines.Count}");
            var textLine = text.Lines[line - 1];
            var lineText = textLine.ToString();
            int col;
            if (column.HasValue) col = Math.Max(0, Math.Min(column.Value - 1, lineText.Length));
            else if (!string.IsNullOrEmpty(name))
            {
                var simple = name.Substring(name.LastIndexOf('.') + 1);
                var m = Regex.Match(lineText, $@"\b{Regex.Escape(simple)}\b");
                if (!m.Success) throw new ArgumentException($"'{simple}' not found on line {line}: {lineText.Trim()}");
                col = m.Index;
            }
            else col = Regex.Match(lineText, @"[A-Za-z_]\w*").Index;

            var position = textLine.Start + col;
            var root = await doc.GetSyntaxRootAsync(ct);
            var model = await doc.GetSemanticModelAsync(ct);
            var token = root.FindToken(position);
            for (var node = token.Parent; node != null; node = node.Parent)
            {
                var symbol = model.GetDeclaredSymbol(node, ct);
                if (symbol == null)
                {
                    var info = model.GetSymbolInfo(node, ct);
                    symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
                }
                if (symbol is IAliasSymbol alias) symbol = alias.Target;
                if (symbol != null) return symbol; // premier symbole trouvé en remontant l'arbre, espace de noms compris
            }
            throw new InvalidOperationException($"No symbol at {Path.GetFileName(file)}:{line}:{col + 1} ('{token.Text}').");
        }

        private static async Task<ISymbol> SymbolByNameAsync(Solution solution, string name, CancellationToken ct)
        {
            var simple = name.Substring(name.LastIndexOf('.') + 1);
            var found = new List<ISymbol>();
            foreach (var project in solution.Projects)
                found.AddRange(await SymbolFinder.FindSourceDeclarationsAsync(project, simple, ignoreCase: false, ct));
            found = found.Where(s => s.Kind != SymbolKind.Namespace).GroupBy(s => s.ToDisplayString()).Select(g => g.First()).ToList();
            if (name.Contains('.'))
                found = found.Where(s => ("." + QualifiedName(s)).EndsWith("." + name, StringComparison.Ordinal)).ToList();
            if (found.Count == 1) return found[0];
            if (found.Count == 0) throw new InvalidOperationException($"No declaration named '{name}' in the solution. Try findSymbol.");
            var list = string.Join("\n", found.Take(20).Select(s => $"- {s.Kind} {s.ToDisplayString()} @ {Where(s)}"));
            throw new InvalidOperationException($"'{name}' is ambiguous ({found.Count} declarations). Use 'file' + 'line', or a qualified name:\n{list}");
        }

        // ---- Outils -----------------------------------------------------------------------------------

        private static async Task<JObject> FindSymbolAsync(Solution solution, string query, string kind, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("query is required");
            var results = new List<ISymbol>();
            foreach (var project in solution.Projects)
                results.AddRange(await SymbolFinder.FindSourceDeclarationsAsync(project,
                    n => n.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0, ct));
            var filtered = results
                .Where(s => s.Kind != SymbolKind.Namespace && s.Kind != SymbolKind.Local && s.Kind != SymbolKind.Parameter)
                .Where(s => MatchesKind(s, kind))
                .GroupBy(s => s.ToDisplayString()).Select(g => g.First())
                .OrderBy(s => s.Name.Equals(query, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(s => s.Name.Length).ThenBy(s => s.ToDisplayString())
                .ToList();
            const int shown = 100;
            var symbols = filtered.Take(shown).Select(s => new JObject
            {
                ["kind"] = KindName(s),
                ["name"] = s.ToDisplayString(),
                ["location"] = Where(s),
            });
            return IdeTools.Json(new JObject { ["count"] = filtered.Count, ["symbols"] = new JArray(symbols), ["truncated"] = filtered.Count > shown });
        }

        private static bool MatchesKind(ISymbol s, string kind)
        {
            switch (kind)
            {
                case "type": return s is INamedTypeSymbol;
                case "method": return s is IMethodSymbol;
                case "property": return s is IPropertySymbol;
                case "field": return s is IFieldSymbol;
                case "event": return s is IEventSymbol;
                default: return true;
            }
        }

        private static JObject Describe(ISymbol s, CancellationToken ct)
        {
            var o = new JObject
            {
                ["kind"] = KindName(s),
                ["name"] = s.Name,
                ["fullName"] = s.ToDisplayString(),
                ["signature"] = s.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
                ["accessibility"] = s.DeclaredAccessibility.ToString(),
                ["isStatic"] = s.IsStatic,
                ["containingType"] = s.ContainingType?.ToDisplayString(),
                ["namespace"] = s.ContainingNamespace?.ToDisplayString(),
                ["assembly"] = s.ContainingAssembly?.Name,
            };
            if (s.IsAbstract) o["isAbstract"] = true;
            if (s.IsVirtual) o["isVirtual"] = true;
            if (s.IsOverride) o["isOverride"] = true;
            switch (s)
            {
                case INamedTypeSymbol t:
                    o["typeKind"] = t.TypeKind.ToString();
                    if (t.BaseType != null && t.BaseType.SpecialType != SpecialType.System_Object) o["baseType"] = t.BaseType.ToDisplayString();
                    if (t.Interfaces.Length > 0) o["interfaces"] = new JArray(t.Interfaces.Select(i => i.ToDisplayString()));
                    o["members"] = new JArray(t.GetMembers().Where(m => m.CanBeReferencedByName && !m.IsImplicitlyDeclared)
                        .Take(80).Select(m => $"{KindName(m)} {m.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}"));
                    break;
                case IMethodSymbol m:
                    o["returnType"] = m.ReturnType.ToDisplayString();
                    o["parameters"] = new JArray(m.Parameters.Select(p => $"{p.Type.ToDisplayString()} {p.Name}"));
                    if (m.IsExtensionMethod) o["isExtension"] = true;
                    break;
                case IPropertySymbol p:
                    o["type"] = p.Type.ToDisplayString();
                    o["hasSetter"] = p.SetMethod != null;
                    break;
                case IFieldSymbol f:
                    o["type"] = f.Type.ToDisplayString();
                    if (f.HasConstantValue) o["constant"] = f.ConstantValue?.ToString();
                    break;
            }
            var doc = Summary(s.GetDocumentationCommentXml(cancellationToken: ct));
            if (doc != null) o["documentation"] = doc;
            o["definitions"] = new JArray(s.Locations.Where(l => l.IsInSource).Select(Loc));
            if (s.Locations.All(l => !l.IsInSource)) o["definedIn"] = "metadata (compiled assembly)";
            // Membres d'interface / overrides : utile pour comprendre d'où vient une méthode
            if (s is IMethodSymbol om && om.OverriddenMethod != null) o["overrides"] = om.OverriddenMethod.ToDisplayString();
            return o;
        }

        private static async Task<JObject> FindReferencesAsync(Solution solution, ISymbol symbol, CancellationToken ct)
        {
            var refs = await SymbolFinder.FindReferencesAsync(symbol, solution, ct);
            var locations = refs.SelectMany(r => r.Locations).Select(l => l.Location)
                .Where(l => l.IsInSource).Distinct()
                .OrderBy(l => l.SourceTree.FilePath).ThenBy(l => l.SourceSpan.Start).ToList();
            var definitions = refs.Select(r => r.Definition).SelectMany(d => d.Locations).Where(l => l.IsInSource)
                .GroupBy(l => l.SourceTree.FilePath + ":" + l.SourceSpan.Start).Select(g => g.First()).Select(Loc);
            return IdeTools.Json(new JObject
            {
                ["symbol"] = symbol.ToDisplayString(),
                ["kind"] = KindName(symbol),
                ["definitions"] = new JArray(definitions),
                ["referenceCount"] = locations.Count,
                ["references"] = new JArray(locations.Take(MaxResults).Select(Loc)),
                ["truncated"] = locations.Count > MaxResults,
            });
        }

        private static async Task<JObject> DocumentSymbolsAsync(Solution solution, string file, CancellationToken ct)
        {
            var doc = FindDocument(solution, file);
            var root = await doc.GetSyntaxRootAsync(ct);
            var model = await doc.GetSemanticModelAsync(ct);
            var text = await doc.GetTextAsync(ct);
            var symbols = new JArray();

            IEnumerable<SyntaxNode> candidates = doc.Project.Language == LanguageNames.CSharp
                ? root.DescendantNodes().Where(n => n is MemberDeclarationSyntax || n is VariableDeclaratorSyntax)
                : root.DescendantNodes(); // VB : on interroge tous les nœuds, plus lent mais sûr
            foreach (var node in candidates)
            {
                if (node is FieldDeclarationSyntax || node is EventFieldDeclarationSyntax) continue; // leurs variables portent les symboles
                var s = model.GetDeclaredSymbol(node, ct);
                if (s == null || s.IsImplicitlyDeclared) continue;
                if (s.Kind == SymbolKind.Local || s.Kind == SymbolKind.Parameter || s.Kind == SymbolKind.TypeParameter || s.Kind == SymbolKind.Label) continue;
                if (s is IMethodSymbol m && (m.MethodKind == MethodKind.LambdaMethod || m.MethodKind == MethodKind.LocalFunction)) continue;
                var span = text.Lines.GetLinePositionSpan(node.Span);
                symbols.Add(new JObject
                {
                    ["kind"] = KindName(s),
                    ["name"] = s.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    ["container"] = (s.ContainingType ?? (ISymbol)s.ContainingNamespace)?.ToDisplayString(),
                    ["line"] = span.Start.Line + 1,
                    ["endLine"] = span.End.Line + 1,
                    ["accessibility"] = s.DeclaredAccessibility.ToString(),
                });
            }
            return IdeTools.Json(new JObject { ["file"] = doc.FilePath, ["language"] = doc.Project.Language, ["symbols"] = symbols });
        }

        private static async Task<JObject> DiagnosticsAsync(Solution solution, string file, string minSeverity, CancellationToken ct)
        {
            var min = minSeverity == "error" ? DiagnosticSeverity.Error : minSeverity == "info" ? DiagnosticSeverity.Info : DiagnosticSeverity.Warning;
            var all = new List<Diagnostic>();
            if (!string.IsNullOrEmpty(file))
            {
                var model = await FindDocument(solution, file).GetSemanticModelAsync(ct);
                all.AddRange(model.GetDiagnostics(cancellationToken: ct));
            }
            else
                foreach (var project in solution.Projects)
                {
                    var compilation = await project.GetCompilationAsync(ct);
                    if (compilation != null) all.AddRange(compilation.GetDiagnostics(ct));
                }
            var kept = all.Where(d => d.Severity >= min && !d.IsSuppressed && d.Location.IsInSource)
                .OrderByDescending(d => d.Severity).ThenBy(d => d.Location.SourceTree?.FilePath).ThenBy(d => d.Location.SourceSpan.Start)
                .ToList();
            return IdeTools.Json(new JObject
            {
                ["errors"] = kept.Count(d => d.Severity == DiagnosticSeverity.Error),
                ["warnings"] = kept.Count(d => d.Severity == DiagnosticSeverity.Warning),
                ["diagnostics"] = new JArray(kept.Take(MaxResults).Select(d =>
                {
                    var o = Loc(d.Location);
                    o["id"] = d.Id;
                    o["severity"] = d.Severity.ToString();
                    o["message"] = d.GetMessage();
                    return o;
                })),
                ["truncated"] = kept.Count > MaxResults,
            });
        }

        private async Task<JObject> RenameAsync(VisualStudioWorkspace ws, Solution solution, JObject args, CancellationToken ct)
        {
            var newName = (string)args["newName"];
            if (string.IsNullOrWhiteSpace(newName) || !Regex.IsMatch(newName, @"^[A-Za-z_@]\w*$"))
                throw new ArgumentException("newName must be a valid identifier.");
            var symbol = await ResolveAsync(solution, args, ct);
            if (symbol.Locations.All(l => !l.IsInSource))
                throw new InvalidOperationException($"'{symbol.ToDisplayString()}' is defined in a compiled assembly and cannot be renamed.");

            var options = new SymbolRenameOptions(
                RenameOverloads: true,
                RenameInStrings: (bool?)args["renameInStrings"] ?? false,
                RenameInComments: (bool?)args["renameInComments"] ?? true,
                RenameFile: (bool?)args["renameFile"] ?? false);
            var newSolution = await Renamer.RenameSymbolAsync(solution, symbol, options, newName, ct);

            var files = new JArray();
            var introduced = new List<string>(); // erreurs de compilation apparues dans les documents modifiés (conflit de nom)
            var changes = newSolution.GetChanges(solution);
            var projectChangesList = changes.GetProjectChanges().ToList();
            // La vérification des conflits coûte deux modèles sémantiques par document : on la réserve aux renommages contenus
            var changedCount = projectChangesList.Sum(p => p.GetChangedDocuments().Count());
            var checkConflicts = changedCount <= ConflictCheckMaxDocuments;
            foreach (var projectChanges in projectChangesList)
            {
                foreach (var docId in projectChanges.GetChangedDocuments())
                {
                    var oldDoc = solution.GetDocument(docId);
                    var newDoc = newSolution.GetDocument(docId);
                    var edits = await newDoc.GetTextChangesAsync(oldDoc, ct);
                    var entry = new JObject { ["file"] = oldDoc.FilePath, ["edits"] = edits.Count() };
                    // Avant application, Roslyn ne change que le nom du document, pas encore le chemin
                    if (!string.Equals(newDoc.Name, oldDoc.Name, StringComparison.OrdinalIgnoreCase)) entry["renamedTo"] = newDoc.Name;
                    files.Add(entry);
                    if (checkConflicts) introduced.AddRange(await NewErrorsAsync(oldDoc, newDoc, ct));
                }
                foreach (var docId in projectChanges.GetAddedDocuments())
                    files.Add(new JObject { ["file"] = newSolution.GetDocument(docId)?.FilePath, ["added"] = true });
                foreach (var docId in projectChanges.GetRemovedDocuments())
                    files.Add(new JObject { ["file"] = solution.GetDocument(docId)?.FilePath, ["removed"] = true });
            }
            if (introduced.Count > 0)
                throw new InvalidOperationException($"Renaming '{symbol.Name}' to '{newName}' would introduce {introduced.Count} compile error(s) (name conflict?); nothing was applied:\n"
                    + string.Join("\n", introduced.Take(10).Select(e => "- " + e)));

            var preview = (bool?)args["preview"] ?? false;
            var applied = false;
            if (!preview)
            {
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
                // TryApplyChanges refuse si la solution a changé entre-temps : on le signale au modèle
                applied = ws.TryApplyChanges(newSolution);
                if (!applied) throw new InvalidOperationException("Visual Studio refused to apply the rename (solution changed meanwhile, or document locked). Call renameSymbol again.");
                _log?.Info($"Renommage : {symbol.ToDisplayString()} → {newName} ({files.Count} fichier(s))");
            }
            return IdeTools.Json(new JObject
            {
                ["symbol"] = symbol.ToDisplayString(),
                ["kind"] = KindName(symbol),
                ["newName"] = newName,
                ["files"] = files,
                ["applied"] = applied,
                ["note"] = (applied ? "Applied in Visual Studio (undo with Ctrl+Z there). Files were modified by the IDE, not by your Edit tool: re-read them if needed."
                                    : "Preview only, nothing changed.")
                         + (checkConflicts ? "" : $" Name-conflict check skipped ({changedCount} files changed, more than {ConflictCheckMaxDocuments}): build to verify."),
            });
        }

        /// <summary>
        /// Erreurs de compilation d'un document après renommage qui n'existaient pas avant. Comparaison sur le
        /// nombre (les messages citant l'ancien nom changent de texte), puis liste des messages nouveaux.
        /// </summary>
        private static async Task<IEnumerable<string>> NewErrorsAsync(Document oldDoc, Document newDoc, CancellationToken ct)
        {
            var before = (await oldDoc.GetSemanticModelAsync(ct)).GetDiagnostics(cancellationToken: ct)
                .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            var after = (await newDoc.GetSemanticModelAsync(ct)).GetDiagnostics(cancellationToken: ct)
                .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            if (after.Count <= before.Count) return Enumerable.Empty<string>();
            var known = new HashSet<string>(before.Select(d => d.Id + d.GetMessage()));
            var added = after.Where(d => !known.Contains(d.Id + d.GetMessage())).ToList();
            if (added.Count == 0) added = after;
            return added.Select(d =>
            {
                var span = d.Location.GetLineSpan();
                return $"{Path.GetFileName(span.Path)}:{span.StartLinePosition.Line + 1}: {d.Id} {d.GetMessage()}";
            });
        }

        // ---- Utilitaires -------------------------------------------------------------------------------

        /// <summary>Nom qualifié sans génériques ni paramètres : Namespace.Type.Membre.</summary>
        private static string QualifiedName(ISymbol s)
        {
            var parts = new List<string>();
            for (var c = s; c != null && !(c is INamespaceSymbol ns && ns.IsGlobalNamespace); c = c.ContainingSymbol)
                if (!string.IsNullOrEmpty(c.Name)) parts.Add(c.Name);
            parts.Reverse();
            return string.Join(".", parts);
        }

        private static string KindName(ISymbol s)
        {
            switch (s)
            {
                case INamedTypeSymbol t: return t.TypeKind.ToString().ToLowerInvariant();
                case IMethodSymbol m: return m.MethodKind == MethodKind.Constructor ? "constructor" : "method";
                default: return s.Kind.ToString().ToLowerInvariant();
            }
        }

        private static JObject Loc(Location l)
        {
            var span = l.GetLineSpan();
            var o = new JObject
            {
                ["file"] = span.Path,
                ["line"] = span.StartLinePosition.Line + 1,
                ["column"] = span.StartLinePosition.Character + 1,
            };
            try
            {
                var text = l.SourceTree?.GetText();
                if (text != null) o["text"] = text.Lines[span.StartLinePosition.Line].ToString().Trim();
            }
            catch { }
            return o;
        }

        private static string Where(ISymbol s)
        {
            var l = s.Locations.FirstOrDefault(x => x.IsInSource);
            if (l == null) return "metadata";
            var span = l.GetLineSpan();
            return $"{span.Path}:{span.StartLinePosition.Line + 1}";
        }

        /// <summary>Résumé lisible d'un commentaire de documentation XML.</summary>
        private static string Summary(string xml)
        {
            if (string.IsNullOrWhiteSpace(xml)) return null;
            var text = Regex.Replace(xml, @"<see\s+cref=""[A-Z]:([^""]+)""\s*/>", "$1");
            text = Regex.Replace(text, @"<param\s+name=""([^""]+)"">", "\n$1: ");
            text = Regex.Replace(text, @"<returns>", "\nReturns: ");
            text = Regex.Replace(text, @"<[^>]+>", "");
            text = Regex.Replace(text, @"[ \t]+", " ");
            text = Regex.Replace(text, @"\s*\n\s*", "\n").Trim();
            return text.Length > 1500 ? text.Substring(0, 1500) + "…" : text;
        }
    }
}
