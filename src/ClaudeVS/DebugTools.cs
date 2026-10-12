using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using EnvDTE90a;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using Newtonsoft.Json.Linq;
using Thread = EnvDTE.Thread;

namespace ClaudeVS
{
    /// <summary>
    /// Outils MCP de compilation et de débogage, via l'automatisation de VS (DTE).
    /// Tous les appels DTE se font sur le thread principal ; les attentes se font par sondage
    /// pour ne pas bloquer l'interface.
    /// </summary>
    internal sealed class DebugTools
    {
        public static readonly HashSet<string> Names = new HashSet<string>
        {
            "build", "debugStart", "debugStop", "debugContinue", "debugStep", "debugBreak",
            "debugState", "debugLocals", "debugEvaluate", "breakpointAdd", "breakpointRemove", "breakpointList", "getOutput",
            "runTests", "debugWait",
        };

        // Volets de la fenêtre Sortie (les noms sont localisés, les GUID non)
        private static readonly Guid DebugPane = new Guid("FC076020-078A-11D1-A7DF-00A0C9110051");
        private static readonly Guid BuildPane = new Guid("1BD8A850-02E1-11D1-BEE7-00A0C913D1F8");

        private readonly AsyncPackage _package;
        private static int _waiting; // nombre d'outils en attente d'un arrêt du programme (toutes instances)

        public DebugTools(AsyncPackage package) => _package = package;

        /// <summary>Un outil attend que le programme s'arrête : les événements du débogueur n'ont pas à être relayés.</summary>
        public bool IsWaiting => _waiting > 0;

        /// <summary>État courant du débogueur (pour le moniteur d'événements).</summary>
        public JObject CurrentState()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return State();
        }

        public static JArray Definitions() => new JArray
        {
            IdeTools.Tool("build", "Build the solution (or one project) in Visual Studio and return the result with compiler errors and warnings.", new JObject
            {
                ["project"] = IdeTools.Str("Optional project name to build instead of the whole solution."),
            }),
            IdeTools.Tool("debugStart", "Start debugging in Visual Studio (F5) and wait until the program stops at a breakpoint, throws, or exits.", new JObject
            {
                ["project"] = IdeTools.Str("Optional project to set as the startup project before starting."),
                ["timeoutSeconds"] = IdeTools.Int("How long to wait for a break or for the program to end (default 30). On timeout the program keeps running."),
            }),
            IdeTools.Tool("debugStop", "Stop the debugging session (Shift+F5)."),
            IdeTools.Tool("debugContinue", "Continue execution (F5) when stopped, and wait for the next break or for the program to end.", new JObject
            {
                ["timeoutSeconds"] = IdeTools.Int("How long to wait (default 30)."),
            }),
            IdeTools.Tool("debugStep", "Step when stopped: 'over' (F10), 'into' (F11) or 'out' (Shift+F11). Returns the new location.", new JObject
            {
                ["kind"] = new JObject { ["type"] = "string", ["enum"] = new JArray("over", "into", "out"), ["description"] = "Kind of step." },
            }, "kind"),
            IdeTools.Tool("debugBreak", "Pause the running program (Break All) and return where it stopped."),
            IdeTools.Tool("debugWait", "Wait while the program runs until it stops (breakpoint, exception, step) or ends. Use it after debugStart/debugContinue timed out, or when the user is interacting with the program.", new JObject
            {
                ["timeoutSeconds"] = IdeTools.Int("How long to wait (default 120, max 600)."),
            }),
            IdeTools.Tool("debugState", "Current debugger state: mode (design/running/break), current location, call stack, last break reason."),
            IdeTools.Tool("debugLocals", "Local variables and arguments of a stack frame while stopped.", new JObject
            {
                ["frameIndex"] = IdeTools.Int("Stack frame index, 0 = current frame (default 0)."),
                ["maxDepth"] = IdeTools.Int("How many levels of members to expand (default 1, max 3)."),
            }),
            IdeTools.Tool("debugEvaluate", "Evaluate an expression in the current stack frame while stopped (like the Watch window). May have side effects.", new JObject
            {
                ["expression"] = IdeTools.Str("Expression in the language of the current frame."),
            }, "expression"),
            IdeTools.Tool("breakpointAdd", "Add a breakpoint at a file and line.", new JObject
            {
                ["file"] = IdeTools.Str("Path to the source file (absolute or relative to the workspace)."),
                ["line"] = IdeTools.Int("1-based line number."),
                ["condition"] = IdeTools.Str("Optional condition expression; the breakpoint only hits when it is true."),
            }, "file", "line"),
            IdeTools.Tool("breakpointRemove", "Remove the breakpoint(s) at a file and line.", new JObject
            {
                ["file"] = IdeTools.Str("Path to the source file."),
                ["line"] = IdeTools.Int("1-based line number."),
            }, "file", "line"),
            IdeTools.Tool("breakpointList", "List all breakpoints."),
            IdeTools.Tool("getOutput", "Read the Visual Studio Output window: 'debug' (program output, Debug.WriteLine, exceptions) or 'build'.", new JObject
            {
                ["pane"] = new JObject { ["type"] = "string", ["enum"] = new JArray("debug", "build"), ["description"] = "Which pane (default debug)." },
                ["tail"] = IdeTools.Int("Number of last lines to return (default 100)."),
            }),
            TestTools.Definition(),
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
            if (Dte.Solution == null || !Dte.Solution.IsOpen)
                throw new InvalidOperationException("No solution is open in Visual Studio.");
            switch (name)
            {
                case "build": return await BuildAsync((string)args["project"]);
                case "debugStart": return await StartAsync((string)args["project"], (int?)args["timeoutSeconds"] ?? 30);
                case "debugStop":
                    return IdeTools.Json(await StopAsync());
                case "debugContinue":
                    RequireBreak();
                    Dte.Debugger.Go(false);
                    return IdeTools.Json(await WaitForBreakOrEndAsync((int?)args["timeoutSeconds"] ?? 30));
                case "debugStep": return await StepAsync((string)args["kind"]);
                case "debugBreak":
                    if (Dte.Debugger.CurrentMode != dbgDebugMode.dbgRunMode) throw new InvalidOperationException("The program is not running.");
                    Dte.Debugger.Break(false);
                    return IdeTools.Json(await WaitForBreakOrEndAsync(10));
                case "debugWait":
                    if (Dte.Debugger.CurrentMode == dbgDebugMode.dbgDesignMode) throw new InvalidOperationException("Not debugging." + RecentDebugOutput());
                    return IdeTools.Json(await WaitForBreakOrEndAsync(Math.Min(600, (int?)args["timeoutSeconds"] ?? 120)));
                case "debugState": return IdeTools.Json(State());
                case "debugLocals": return Locals((int?)args["frameIndex"] ?? 0, Math.Min(3, Math.Max(0, (int?)args["maxDepth"] ?? 1)));
                case "debugEvaluate": return Evaluate((string)args["expression"]);
                case "breakpointAdd": return BreakpointAdd((string)args["file"], (int?)args["line"] ?? 0, (string)args["condition"]);
                case "breakpointRemove": return BreakpointRemove((string)args["file"], (int?)args["line"] ?? 0);
                case "breakpointList": return IdeTools.Json(Breakpoints());
                case "getOutput": return GetOutput((string)args["pane"] ?? "debug", (int?)args["tail"] ?? 100);
                case "runTests": return await RunTestsAsync(args);
                default: throw new InvalidOperationException("Unknown tool: " + name);
            }
        }

        // ---- Tests ----------------------------------------------------------------------------

        private async Task<JObject> RunTestsAsync(JObject args)
        {
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
            var target = (string)args["project"];
            // Un projet « .csproj » ouvert seul : Solution.FullName est vide, on se rabat sur le projet
            var solutionPath = Dte.Solution.FullName;
            var first = Dte.Solution.Projects.Cast<Project>().FirstOrDefault(p => !string.IsNullOrEmpty(p.FullName));
            var baseDir = Path.GetDirectoryName(!string.IsNullOrEmpty(solutionPath) ? solutionPath : first?.FullName ?? "");
            if (string.IsNullOrEmpty(target)) target = !string.IsNullOrEmpty(solutionPath) ? solutionPath : first?.FullName;
            else if (!Path.IsPathRooted(target) && !string.IsNullOrEmpty(baseDir)) target = Path.Combine(baseDir, target);
            var log = ClaudeVSPackage.Instance.Log;

            await TaskScheduler.Default; // le processus tourne hors du thread principal
            var result = await TestTools.RunAsync(target, (string)args["filter"], (bool?)args["noBuild"] ?? false,
                (int?)args["timeoutSeconds"] ?? 600, log);
            return IdeTools.Json(result);
        }

        // ---- Compilation -------------------------------------------------------------------

        private async Task<JObject> BuildAsync(string projectName)
        {
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
            var build = Dte.Solution.SolutionBuild;
            if (build.BuildState == vsBuildState.vsBuildStateInProgress)
                throw new InvalidOperationException("A build is already in progress.");

            var config = build.ActiveConfiguration?.Name ?? "Debug";
            if (string.IsNullOrEmpty(projectName))
                build.Build(false);
            else
                build.BuildProject(config, FindProject(projectName).UniqueName, false);

            // On sonde plutôt que Build(true) pour laisser l'interface de VS respirer
            var deadline = DateTime.UtcNow.AddMinutes(10);
            while (build.BuildState == vsBuildState.vsBuildStateInProgress && DateTime.UtcNow < deadline)
            {
                await Task.Delay(300);
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
            }

            var errors = new JArray();
            int warnings = 0;
            var items = Dte.ToolWindows.ErrorList.ErrorItems;
            for (var i = 1; i <= items.Count; i++)
            {
                var item = items.Item(i);
                if (item.ErrorLevel == vsBuildErrorLevel.vsBuildErrorLevelMedium) { warnings++; continue; }
                if (item.ErrorLevel != vsBuildErrorLevel.vsBuildErrorLevelHigh) continue;
                errors.Add(new JObject
                {
                    ["file"] = item.FileName, ["line"] = item.Line, ["message"] = item.Description, ["project"] = item.Project,
                });
            }
            return IdeTools.Json(new JObject
            {
                ["succeeded"] = build.LastBuildInfo == 0 && build.BuildState == vsBuildState.vsBuildStateDone,
                ["configuration"] = config,
                ["failedProjects"] = build.LastBuildInfo,
                ["errorCount"] = errors.Count,
                ["warningCount"] = warnings,
                ["errors"] = errors,
            });
        }

        private Project FindProject(string name)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var all = new List<Project>();
            void Collect(Project p)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (p == null) return;
                if (p.Kind == ProjectKinds.vsProjectKindSolutionFolder)
                    for (var i = 1; i <= p.ProjectItems.Count; i++) Collect(p.ProjectItems.Item(i).SubProject);
                else all.Add(p);
            }
            foreach (Project p in Dte.Solution.Projects) Collect(p);
            var found = all.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)
                                             || string.Equals(p.UniqueName, name, StringComparison.OrdinalIgnoreCase)
                                             || string.Equals(Path.GetFileNameWithoutExtension(p.FullName), name, StringComparison.OrdinalIgnoreCase));
            if (found == null)
                throw new InvalidOperationException($"Project not found: {name}. Projects: {string.Join(", ", all.Select(p => p.Name))}");
            return found;
        }

        // ---- Exécution ---------------------------------------------------------------------

        private async Task<JObject> StartAsync(string projectName, int timeoutSeconds)
        {
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
            var dbg = Dte.Debugger;
            if (dbg.CurrentMode != dbgDebugMode.dbgDesignMode)
                throw new InvalidOperationException("A debugging session is already active. Use debugContinue, or debugStop first.");
            if (!string.IsNullOrEmpty(projectName))
                Dte.Solution.SolutionBuild.StartupProjects = new object[] { FindProject(projectName).UniqueName };
            dbg.Go(false);
            var state = await WaitForBreakOrEndAsync(timeoutSeconds);
            // Compilation échouée ou démarrage impossible : rien ne tourne, le volet Build explique pourquoi
            if ((string)state["mode"] == "design" && Dte.Solution.SolutionBuild.LastBuildInfo > 0)
                state["note"] = "The build failed before starting. Call getOutput with pane 'build' or build for the errors.";
            return IdeTools.Json(state);
        }

        /// <summary>
        /// Stop(true) échoue (« Unable to execute method at this time ») quand VS affiche l'aide sur
        /// exception ; on passe alors par la commande de menu, puis on attend le retour en conception.
        /// </summary>
        private async Task<JObject> StopAsync()
        {
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (Dte.Debugger.CurrentMode == dbgDebugMode.dbgDesignMode) return State();
            string error = null;
            try { Dte.Debugger.Stop(false); }
            catch (Exception ex)
            {
                // L'arrêt est souvent déjà engagé malgré l'erreur : on vérifie l'état avant de conclure
                error = ex.Message;
                try { Dte.ExecuteCommand("Debug.StopDebugging"); } catch { }
            }
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (Dte.Debugger.CurrentMode != dbgDebugMode.dbgDesignMode && DateTime.UtcNow < deadline)
            {
                await Task.Delay(200);
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
            }
            if (Dte.Debugger.CurrentMode != dbgDebugMode.dbgDesignMode)
                throw new InvalidOperationException("Could not stop debugging: " + (error ?? "still running"));
            return State();
        }

        private async Task<JObject> StepAsync(string kind)
        {
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
            RequireBreak();
            switch (kind)
            {
                case "over": Dte.Debugger.StepOver(false); break;
                case "into": Dte.Debugger.StepInto(false); break;
                case "out": Dte.Debugger.StepOut(false); break;
                default: throw new ArgumentException("kind must be over, into or out");
            }
            return IdeTools.Json(await WaitForBreakOrEndAsync(30));
        }

        private void RequireBreak()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (Dte.Debugger.CurrentMode != dbgDebugMode.dbgBreakMode)
                throw new InvalidOperationException("The program is not stopped (mode: " + ModeName(Dte.Debugger.CurrentMode) + "). "
                    + "It may have been continued by the user in Visual Studio, or ended." + RecentDebugOutput());
        }

        /// <summary>Dernières lignes du volet Déboguer, pour expliquer un changement d'état inattendu.</summary>
        private string RecentDebugOutput()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var text = (string)((JObject)GetOutput("debug", 8)["content"][0]["text"]);
                var tail = (string)JObject.Parse(text)["text"];
                return string.IsNullOrWhiteSpace(tail) ? "" : "\nRecent debug output:\n" + tail;
            }
            catch { return ""; }
        }

        /// <summary>Attend que le programme s'arrête (point d'arrêt, exception, pas) ou se termine.</summary>
        private async Task<JObject> WaitForBreakOrEndAsync(int timeoutSeconds)
        {
            var deadline = DateTime.UtcNow.AddSeconds(Math.Max(1, timeoutSeconds));
            System.Threading.Interlocked.Increment(ref _waiting);
            try
            {
                // Laisse le temps au débogueur de quitter le mode précédent avant de sonder
                await Task.Delay(300);
                while (true)
                {
                    await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                    var mode = Dte.Debugger.CurrentMode;
                    if (mode != dbgDebugMode.dbgRunMode || DateTime.UtcNow > deadline)
                    {
                        var state = State();
                        if (mode == dbgDebugMode.dbgRunMode)
                            state["note"] = "Still running after the timeout (waiting for input, or long task). Call debugWait to keep waiting, debugBreak to pause it, or debugStop.";
                        return state;
                    }
                    await Task.Delay(250);
                }
            }
            finally { System.Threading.Interlocked.Decrement(ref _waiting); }
        }

        // ---- État ----------------------------------------------------------------------------

        private static string ModeName(dbgDebugMode mode) =>
            mode == dbgDebugMode.dbgBreakMode ? "break" : mode == dbgDebugMode.dbgRunMode ? "running" : "design";

        private JObject State()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var dbg = Dte.Debugger;
            var state = new JObject { ["mode"] = ModeName(dbg.CurrentMode) };
            if (dbg.CurrentMode == dbgDebugMode.dbgDesignMode)
            {
                state["description"] = "Not debugging.";
                return state;
            }
            state["process"] = dbg.CurrentProcess?.Name;
            if (dbg.CurrentMode != dbgDebugMode.dbgBreakMode) return state;

            state["breakReason"] = dbg.LastBreakReason.ToString().Replace("dbgEventReason", "");
            var frames = new JArray();
            var thread = dbg.CurrentThread;
            if (thread != null)
            {
                state["thread"] = new JObject { ["id"] = thread.ID, ["name"] = thread.Name };
                var i = 0;
                foreach (StackFrame f in thread.StackFrames)
                {
                    if (i >= 25) { frames.Add(new JObject { ["function"] = "…" }); break; }
                    frames.Add(Describe(f, i++));
                }
            }
            state["location"] = frames.Count > 0 ? frames[0] : null;
            state["callStack"] = frames;

            // Exception non gérée : son message est le plus utile
            if (dbg.LastBreakReason == dbgEventReason.dbgEventReasonExceptionThrown || dbg.LastBreakReason == dbgEventReason.dbgEventReasonExceptionNotHandled)
            {
                var ex = TryEval("$exception");
                if (ex != null) state["exception"] = ex;
            }
            return state;
        }

        private JObject Describe(StackFrame f, int index)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var o = new JObject { ["index"] = index, ["function"] = f.FunctionName, ["language"] = f.Language };
            if (f is StackFrame2 f2)
            {
                try
                {
                    if (!string.IsNullOrEmpty(f2.FileName)) { o["file"] = f2.FileName; o["line"] = (int)f2.LineNumber; }
                    o["module"] = f2.Module;
                }
                catch { /* trame sans symboles */ }
            }
            return o;
        }

        private JObject Locals(int frameIndex, int maxDepth)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            RequireBreak();
            var frame = Dte.Debugger.CurrentThread.StackFrames.Cast<StackFrame>().ElementAtOrDefault(frameIndex)
                        ?? throw new ArgumentException("No stack frame at index " + frameIndex);
            var budget = 400; // nombre maximal de valeurs renvoyées, toutes profondeurs confondues
            return IdeTools.Json(new JObject
            {
                ["frame"] = Describe(frame, frameIndex),
                ["arguments"] = Expressions(frame.Arguments, maxDepth, ref budget),
                ["locals"] = Expressions(frame.Locals, maxDepth, ref budget),
            });
        }

        private JArray Expressions(Expressions list, int depth, ref int budget)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var result = new JArray();
            if (list == null) return result;
            foreach (Expression e in list)
            {
                if (budget-- <= 0) { result.Add(new JObject { ["name"] = "…" }); break; }
                var o = new JObject { ["name"] = e.Name, ["type"] = e.Type, ["value"] = Truncate(e.Value) };
                if (depth > 0 && e.DataMembers != null && e.DataMembers.Count > 0 && e.DataMembers.Count <= 50)
                    o["members"] = Expressions(e.DataMembers, depth - 1, ref budget);
                result.Add(o);
            }
            return result;
        }

        private JObject Evaluate(string expression)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            RequireBreak();
            if (string.IsNullOrWhiteSpace(expression)) throw new ArgumentException("expression is required");
            var e = Dte.Debugger.GetExpression(expression, true, 10000);
            return IdeTools.Json(new JObject
            {
                ["expression"] = expression,
                ["valid"] = e.IsValidValue,
                ["type"] = e.Type,
                ["value"] = Truncate(e.Value),
            });
        }

        private JObject TryEval(string expression)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var e = Dte.Debugger.GetExpression(expression, false, 3000);
                return e.IsValidValue ? new JObject { ["type"] = e.Type, ["value"] = Truncate(e.Value) } : null;
            }
            catch { return null; }
        }

        private static string Truncate(string s) => s != null && s.Length > 2000 ? s.Substring(0, 2000) + "…" : s;

        // ---- Points d'arrêt --------------------------------------------------------------

        private string ResolveFile(string file)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (string.IsNullOrEmpty(file)) throw new ArgumentException("file is required");
            if (!Path.IsPathRooted(file) && Dte.Solution.FullName is string sln && !string.IsNullOrEmpty(sln))
                file = Path.Combine(Path.GetDirectoryName(sln), file);
            file = Path.GetFullPath(file);
            if (!File.Exists(file)) throw new FileNotFoundException("File not found: " + file);
            return file;
        }

        private JObject BreakpointAdd(string file, int line, string condition)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            file = ResolveFile(file);
            if (line < 1) throw new ArgumentException("line must be >= 1");
            Dte.Debugger.Breakpoints.Add(File: file, Line: line, Condition: condition ?? "",
                ConditionType: dbgBreakpointConditionType.dbgBreakpointConditionTypeWhenTrue);
            return IdeTools.Json(new JObject { ["added"] = true, ["file"] = file, ["line"] = line, ["breakpoints"] = Breakpoints() });
        }

        private JObject BreakpointRemove(string file, int line)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            file = ResolveFile(file);
            var removed = 0;
            foreach (var bp in Dte.Debugger.Breakpoints.Cast<Breakpoint>().ToList())
            {
                if (string.Equals(bp.File, file, StringComparison.OrdinalIgnoreCase) && bp.FileLine == line)
                {
                    bp.Delete();
                    removed++;
                }
            }
            return IdeTools.Json(new JObject { ["removed"] = removed, ["breakpoints"] = Breakpoints() });
        }

        private JArray Breakpoints()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var result = new JArray();
            foreach (Breakpoint bp in Dte.Debugger.Breakpoints)
            {
                var o = new JObject { ["file"] = bp.File, ["line"] = bp.FileLine, ["enabled"] = bp.Enabled };
                if (!string.IsNullOrEmpty(bp.Condition)) o["condition"] = bp.Condition;
                if (!string.IsNullOrEmpty(bp.FunctionName)) o["function"] = bp.FunctionName;
                result.Add(o);
            }
            return result;
        }

        // ---- Fenêtre Sortie ------------------------------------------------------------------

        private JObject GetOutput(string pane, int tail)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var wanted = pane == "build" ? BuildPane : DebugPane;
            OutputWindowPane found = null;
            foreach (OutputWindowPane p in Dte.ToolWindows.OutputWindow.OutputWindowPanes)
                if (Guid.TryParse(p.Guid, out var g) && g == wanted) { found = p; break; }
            if (found == null)
                return IdeTools.Json(new JObject { ["pane"] = pane, ["text"] = "", ["note"] = "This pane has not been created yet." });

            var doc = found.TextDocument;
            var text = doc.StartPoint.CreateEditPoint().GetText(doc.EndPoint);
            var lines = text.Replace("\r\n", "\n").Split('\n').AsEnumerable();
            // Le volet Déboguer est noyé sous les chargements de modules : on ne garde que l'utile
            if (pane != "build")
                lines = lines.Where(l => !(l.Contains("): Loaded '") || l.Contains("): Unloaded '") || l.Contains("Skipped loading symbols")));
            var all = lines.ToList();
            var kept = all.Skip(Math.Max(0, all.Count - Math.Max(1, tail)));
            return IdeTools.Json(new JObject { ["pane"] = pane, ["totalLines"] = all.Count, ["text"] = string.Join("\n", kept) });
        }
    }
}
