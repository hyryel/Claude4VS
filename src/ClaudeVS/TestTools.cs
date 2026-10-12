using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;

namespace ClaudeVS
{
    /// <summary>
    /// Exécution des tests via « dotnet test » avec un rapport TRX, lu ensuite pour le modèle.
    /// Indépendant de l'Explorateur de tests (dont l'API n'est pas publique) : vaut pour xUnit, NUnit et MSTest.
    /// </summary>
    internal static class TestTools
    {
        private static readonly XNamespace Ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

        public static JObject Definition() => IdeTools.Tool("runTests",
            "Run the unit tests with 'dotnet test' (xUnit, NUnit, MSTest) and return the results: counts, failed tests with messages and stack traces.",
            new JObject
            {
                ["project"] = IdeTools.Str("Path to a test project or solution. Default: the open solution (all test projects)."),
                ["filter"] = IdeTools.Str("Optional 'dotnet test --filter' expression, e.g. FullyQualifiedName~InvoiceTests or FullyQualifiedName~Should_Add (DisplayName~ also works; 'Name' is not supported by xUnit)."),
                ["noBuild"] = IdeTools.Bool("Skip the build (faster if the solution was just built).", false),
                ["timeoutSeconds"] = IdeTools.Int("Maximum run time (default 600)."),
            });

        /// <summary>À appeler hors du thread principal.</summary>
        public static async Task<JObject> RunAsync(string target, string filter, bool noBuild, int timeoutSeconds, Log log)
        {
            if (string.IsNullOrEmpty(target) || !(File.Exists(target) || Directory.Exists(target)))
                throw new FileNotFoundException("Test project or solution not found: " + target);

            var resultsDir = Path.Combine(Path.GetTempPath(), "ClaudeVS", "tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(resultsDir);
            // Pas de LogFileName : en solution multi-projets, chaque projet écrit son propre TRX au lieu d'écraser le même
            var args = new List<string> { "test", target, "--logger", "trx", "--results-directory", resultsDir, "--nologo" };
            if (!string.IsNullOrEmpty(filter)) args.AddRange(new[] { "--filter", filter });
            if (noBuild) args.Add("--no-build");

            var psi = new ProcessStartInfo("dotnet")
            {
                Arguments = CommandLine.Join(args),
                WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(target)),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            // Évite que les variables du processus VS ne perturbent le SDK
            psi.EnvironmentVariables.Remove("MSBuildSDKsPath");
            psi.EnvironmentVariables.Remove("MSBUILD_EXE_PATH");
            psi.EnvironmentVariables["DOTNET_CLI_UI_LANGUAGE"] = "en";
            log.Info($"dotnet {psi.Arguments}");

            var output = new StringBuilder();
            var sw = Stopwatch.StartNew();
            int exitCode;
            try
            {
                using (var p = new Process { StartInfo = psi })
                {
                    try { p.Start(); }
                    catch (Win32Exception ex)
                    {
                        throw new InvalidOperationException("Could not start 'dotnet' (is the .NET SDK installed and on the PATH of Visual Studio?): " + ex.Message);
                    }
                    var stdout = p.StandardOutput.ReadToEndAsync();
                    var stderr = p.StandardError.ReadToEndAsync();
                    var finished = await Task.Run(() => p.WaitForExit(Math.Max(1, timeoutSeconds) * 1000));
                    if (!finished)
                    {
                        KillTree(p, log); // Kill() seul laisserait testhost.exe (et les tests) tourner
                        throw new TimeoutException($"dotnet test did not finish within {timeoutSeconds} s.");
                    }
                    output.Append(await stdout).Append(await stderr);
                    exitCode = p.ExitCode;
                }

                // Un TRX par projet de tests : les compteurs sont sommés et les échecs concaténés
                var trx = Directory.GetFiles(resultsDir, "*.trx", SearchOption.AllDirectories);
                var result = new JObject { ["exitCode"] = exitCode, ["durationMs"] = sw.ElapsedMilliseconds };
                if (trx.Length == 0)
                {
                    // Pas de rapport : la compilation a échoué ou ce n'est pas un projet de tests
                    result["succeeded"] = false;
                    result["note"] = "No test report produced (build error or no test project). See output.";
                    result["output"] = Tail(output.ToString(), 60);
                    return result;
                }

                long total = 0, passed = 0, failed = 0;
                var failures = new JArray();
                foreach (var file in trx)
                {
                    var doc = XDocument.Load(file);
                    var counters = doc.Descendants(Ns + "Counters").FirstOrDefault();
                    if (counters != null)
                    {
                        total += Attr(counters, "total");
                        passed += Attr(counters, "passed");
                        failed += Attr(counters, "failed");
                    }
                    foreach (var r in doc.Descendants(Ns + "UnitTestResult"))
                    {
                        var outcome = (string)r.Attribute("outcome");
                        if (outcome == "Passed" || outcome == "NotExecuted") continue;
                        var error = r.Descendants(Ns + "ErrorInfo").FirstOrDefault();
                        var stack = (string)error?.Element(Ns + "StackTrace");
                        failures.Add(new JObject
                        {
                            ["test"] = (string)r.Attribute("testName"),
                            ["outcome"] = outcome,
                            ["duration"] = (string)r.Attribute("duration"),
                            ["message"] = Truncate((string)error?.Element(Ns + "Message"), 2000),
                            ["stackTrace"] = Truncate(stack, 1500),
                        });
                    }
                }
                result["succeeded"] = exitCode == 0 && failed == 0;
                result["total"] = total;
                result["passed"] = passed;
                result["failed"] = failed;
                result["skipped"] = Math.Max(0, total - passed - failed); // ignorés, non concluants… : tout ce qui n'est ni réussi ni échoué
                result["failures"] = failures;
                result["reports"] = trx.Length;
                if (total == 0) result["note"] = string.IsNullOrEmpty(filter)
                    ? "No tests were found in this project."
                    : "No tests matched the filter. Use FullyQualifiedName~<substring> or DisplayName~<substring>.";
                if (exitCode != 0 && failed == 0) result["output"] = Tail(output.ToString(), 40);
                return result;
            }
            finally
            {
                try { Directory.Delete(resultsDir, true); } catch { }
            }
        }

        /// <summary>Tue dotnet et ses descendants (vstest.console, testhost…) ; Kill() ne touche que le parent.</summary>
        private static void KillTree(Process p, Log log)
        {
            try
            {
                var psi = new ProcessStartInfo("taskkill", $"/T /F /PID {p.Id}") { UseShellExecute = false, CreateNoWindow = true };
                using (var kill = Process.Start(psi)) kill?.WaitForExit(5000);
            }
            catch (Exception ex) { log.Error("taskkill : " + ex.Message); }
            try { if (!p.HasExited) p.Kill(); } catch { }
        }

        private static long Attr(XElement e, string name) => long.TryParse((string)e.Attribute(name), out var v) ? v : 0;
        private static string Truncate(string s, int n) => s != null && s.Length > n ? s.Substring(0, n) + "…" : s;
        private static string Tail(string text, int lines)
        {
            var all = text.Replace("\r\n", "\n").Split('\n').Where(l => l.Trim().Length > 0).ToList();
            return string.Join("\n", all.Skip(Math.Max(0, all.Count - lines)));
        }
    }
}
