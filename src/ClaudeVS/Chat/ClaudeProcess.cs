using System;
using System.Collections.Concurrent;
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
        // Les écritures passent par un thread dédié : stdin est un pipe de quelques Ko, une image collée
        // peut faire plusieurs Mo, et le thread UI ne doit jamais attendre que le CLI lise.
        private readonly BlockingCollection<string> _queue = new BlockingCollection<string>();
        private readonly JobObject _job;
        private readonly Log _log;
        private readonly Task _stderrTask;
        private int _requestCounter;
        private volatile bool _disposed;

        /// <summary>Chaque message JSON émis par le CLI (thread d'arrière-plan).</summary>
        public event Action<JObject> MessageReceived;
        /// <summary>Le processus s'est terminé (code de sortie, fin de stderr).</summary>
        public event Action<int, string> Exited;
        /// <summary>L'écriture vers stdin a échoué (pipe fermé) : le processus est à considérer comme mort.</summary>
        public event Action<Exception> WriteFailed;

        public ClaudeProcess(string exe, string cwd, IEnumerable<string> args, IDictionary<string, string> env, Log log)
        {
            _log = log;
            var psi = new ProcessStartInfo(exe)
            {
                Arguments = CommandLine.Join(args),
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
            // Si devenv meurt brutalement, le job emporte le CLI et ses sous-processus
            _job = JobObject.TryCreateFor(_process, log);
            // net472 n'a pas StandardInputEncoding : on écrit nous-mêmes en UTF-8 sans BOM
            _stdin = new StreamWriter(_process.StandardInput.BaseStream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            _log.Info($"claude démarré (pid {_process.Id}) : {psi.Arguments}");

            Task.Factory.StartNew(WriteLoop, TaskCreationOptions.LongRunning);
            _stderrTask = Task.Run(ReadStderrAsync);
            Task.Run(ReadStdoutAsync);
        }

        private readonly StringBuilder _stderrTail = new StringBuilder();

        /// <summary>Dernières lignes de stderr (pour expliquer une mort du processus).</summary>
        public string StderrTail
        {
            get { lock (_stderrTail) return _stderrTail.ToString(); }
        }

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
            catch (Exception ex) { if (!_disposed) _log.Error("Lecture stdout : " + ex.Message); }

            try { _process.WaitForExit(); } catch (Exception ex) { if (!_disposed) _log.Info("WaitForExit : " + ex.Message); }
            // stderr se ferme avec le processus, mais sa lecture peut avoir un temps de retard : on l'attend pour un message complet
            try { await Task.WhenAny(_stderrTask, Task.Delay(2000)); } catch { }
            int code = -1;
            try { code = _process.ExitCode; } catch { }
            Exited?.Invoke(code, StderrTail);
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
            catch (Exception ex) { if (!_disposed) _log.Error("Lecture stderr : " + ex.Message); }
        }

        /// <summary>Thread écrivain : vide la file vers stdin ; un échec ferme la file et remonte par WriteFailed.</summary>
        private void WriteLoop()
        {
            try
            {
                foreach (var json in _queue.GetConsumingEnumerable())
                    _stdin.WriteLine(json);
            }
            catch (Exception ex)
            {
                if (_disposed) return;
                _log.Error("Écriture vers claude : " + ex.Message);
                try { _queue.CompleteAdding(); } catch { }
                try { WriteFailed?.Invoke(ex); }
                catch (Exception ex2) { _log.Error("Traitement d'un échec d'écriture : " + ex2.Message); }
            }
        }

        public bool IsRunning
        {
            get { try { return !_disposed && !_process.HasExited; } catch { return false; } }
        }

        /// <summary>Met le message en file d'envoi ; ne bloque pas. Lève IOException si le processus n'accepte plus rien.</summary>
        public void Send(JObject message)
        {
            var json = message.ToString(Formatting.None);
            if (_disposed || _queue.IsAddingCompleted || !IsRunning)
                throw new IOException("Le processus claude n'accepte plus de messages (arrêté ou entrée fermée).");
            try { _queue.Add(json); }
            catch (Exception ex) when (ex is InvalidOperationException || ex is ObjectDisposedException)
            {
                throw new IOException("Le processus claude n'accepte plus de messages (entrée fermée).", ex);
            }
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

        /// <summary>Arrête le processus sans attendre : la fin (taskkill, libération) se fait en arrière-plan.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _queue.CompleteAdding(); } catch { }
            try { _stdin.Dispose(); } catch { }
            var process = _process;
            var job = _job;
            Task.Run(() =>
            {
                try
                {
                    if (!process.HasExited)
                    {
                        // Tue aussi les sous-processus (bash, outils…) lancés par le CLI
                        using (var kill = Process.Start(new ProcessStartInfo("taskkill", $"/T /F /PID {process.Id}")
                        { CreateNoWindow = true, UseShellExecute = false }))
                            kill?.WaitForExit(3000);
                    }
                }
                catch (Exception ex) { _log.Info("taskkill : " + ex.Message); }
                try { job?.Dispose(); } catch { }  // fermeture du job = mort de ce qui resterait
                try { process.WaitForExit(3000); } catch { }
                try { process.Dispose(); } catch { }
                try { _queue.Dispose(); } catch { }
            });
        }
    }
}
