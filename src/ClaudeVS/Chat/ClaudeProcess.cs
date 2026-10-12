using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ClaudeVS.Chat
{
    /// <summary>
    /// Un processus « claude » piloté en JSON ligne à ligne (stream-json), comme le fait le Claude Agent SDK.
    /// Les permissions remontent en control_request/can_use_tool grâce à --permission-prompt-tool stdio.
    /// </summary>
    internal sealed class ClaudeProcess : IDisposable
    {
        private readonly Process _process;
        private readonly StreamWriter _stdin;
        private readonly object _writeGate = new object();
        private readonly Log _log;
        private int _requestCounter;

        /// <summary>Chaque message JSON émis par le CLI (thread d'arrière-plan).</summary>
        public event Action<JObject> MessageReceived;
        /// <summary>Le processus s'est terminé (code de sortie, fin de stderr).</summary>
        public event Action<int, string> Exited;

        public ClaudeProcess(string exe, string cwd, IEnumerable<string> args, IDictionary<string, string> env, Log log)
        {
            _log = log;
            var psi = new ProcessStartInfo(exe)
            {
                Arguments = string.Join(" ", Quote(args)),
                WorkingDirectory = cwd,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
            };
            foreach (var kv in env) psi.EnvironmentVariables[kv.Key] = kv.Value;
            _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _process.Start();
            // net472 n'a pas StandardInputEncoding : on écrit nous-mêmes en UTF-8 sans BOM
            _stdin = new StreamWriter(_process.StandardInput.BaseStream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            _log.Info($"claude démarré (pid {_process.Id}) : {psi.Arguments}");

            Task.Run(ReadStdoutAsync);
            Task.Run(ReadStderrAsync);
        }

        private readonly StringBuilder _stderrTail = new StringBuilder();

        private async Task ReadStdoutAsync()
        {
            try
            {
                string line;
                while ((line = await _process.StandardOutput.ReadLineAsync()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    JObject msg;
                    try { msg = JObject.Parse(line); }
                    catch { _log.Info("[claude] " + line); continue; }
                    try { MessageReceived?.Invoke(msg); }
                    catch (Exception ex) { _log.Error("Traitement d'un message CLI : " + ex.Message); }
                }
            }
            catch (Exception ex) { _log.Error("Lecture stdout : " + ex.Message); }

            try { _process.WaitForExit(); } catch { }
            int code = -1;
            try { code = _process.ExitCode; } catch { }
            string tail;
            lock (_stderrTail) tail = _stderrTail.ToString();
            Exited?.Invoke(code, tail);
        }

        private async Task ReadStderrAsync()
        {
            try
            {
                string line;
                while ((line = await _process.StandardError.ReadLineAsync()) != null)
                {
                    _log.Info("[claude stderr] " + line);
                    lock (_stderrTail)
                    {
                        _stderrTail.AppendLine(line);
                        if (_stderrTail.Length > 4000) _stderrTail.Remove(0, _stderrTail.Length - 4000);
                    }
                }
            }
            catch { }
        }

        public bool IsRunning
        {
            get { try { return !_process.HasExited; } catch { return false; } }
        }

        public void Send(JObject message)
        {
            var json = message.ToString(Formatting.None);
            lock (_writeGate)
                _stdin.WriteLine(json);
        }

        public void SendUserMessage(JArray content) => Send(new JObject
        {
            ["type"] = "user",
            ["session_id"] = "",
            ["parent_tool_use_id"] = null,
            ["message"] = new JObject { ["role"] = "user", ["content"] = content },
        });

        /// <summary>Requête de contrôle (initialize, interrupt, set_permission_mode, set_model…).</summary>
        public string SendControlRequest(JObject request)
        {
            var id = "vs-" + System.Threading.Interlocked.Increment(ref _requestCounter);
            Send(new JObject { ["type"] = "control_request", ["request_id"] = id, ["request"] = request });
            return id;
        }

        public void SendControlResponse(string requestId, JObject response) => Send(new JObject
        {
            ["type"] = "control_response",
            ["response"] = new JObject { ["subtype"] = "success", ["request_id"] = requestId, ["response"] = response },
        });

        public void Dispose()
        {
            try { _stdin.Dispose(); } catch { }
            try
            {
                if (!_process.HasExited)
                {
                    // Tue aussi les sous-processus (bash, outils…) lancés par le CLI
                    using (var kill = Process.Start(new ProcessStartInfo("taskkill", $"/T /F /PID {_process.Id}")
                    { CreateNoWindow = true, UseShellExecute = false }))
                        kill?.WaitForExit(3000);
                }
            }
            catch { }
            try { _process.Dispose(); } catch { }
        }

        /// <summary>Échappement des arguments selon les règles de CommandLineToArgvW.</summary>
        private static IEnumerable<string> Quote(IEnumerable<string> args)
        {
            foreach (var a in args)
            {
                if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) { yield return a; continue; }
                var sb = new StringBuilder("\"");
                var backslashes = 0;
                foreach (var c in a)
                {
                    if (c == '\\') { backslashes++; continue; }
                    if (c == '"') { sb.Append('\\', backslashes * 2 + 1); sb.Append('"'); }
                    else { sb.Append('\\', backslashes); sb.Append(c); }
                    backslashes = 0;
                }
                sb.Append('\\', backslashes * 2);
                sb.Append('"');
                yield return sb.ToString();
            }
        }
    }
}
