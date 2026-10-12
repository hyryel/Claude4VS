using System;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Newtonsoft.Json.Linq;

namespace ClaudeVS
{
    /// <summary>
    /// Écoute le débogueur de VS pour signaler au chat ce qui se passe en dehors de ses appels d'outils :
    /// quand l'utilisateur continue l'exécution lui-même et que le programme s'arrête sur une exception.
    /// </summary>
    internal sealed class DebugMonitor
    {
        private static DebugMonitor _instance;
        private readonly DebugTools _tools;
        private DebuggerEvents _events; // référence conservée, sinon les événements COM sont perdus

        /// <summary>Le débogueur vient de s'arrêter sur une exception (état complet : position, pile, exception).</summary>
        public static event Action<JObject> BreakOnException;

        private readonly Log _log;
        private readonly Action _ensureChatVisible;

        private DebugMonitor(DebugTools tools, Log log, Action ensureChatVisible)
        {
            _tools = tools;
            _log = log;
            _ensureChatVisible = ensureChatVisible;
        }

        /// <param name="tools">Le groupe d'outils de débogage, pour voir ses attentes en cours (IsWaiting).</param>
        /// <param name="ensureChatVisible">Réaffiche le panneau du chat s'il avait été ouvert.</param>
        public static void Start(DebugTools tools, Log log, Action ensureChatVisible)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_instance != null) return;
            _instance = new DebugMonitor(tools, log, ensureChatVisible);
            var dte = (DTE2)Package.GetGlobalService(typeof(SDTE));
            _instance._events = dte.Events.DebuggerEvents;
            _instance._events.OnEnterBreakMode += _instance.OnEnterBreakMode;
            _instance._events.OnEnterRunMode += _instance.OnEnterRunMode;
        }

        private void OnEnterRunMode(dbgEventReason reason)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            // Passage à la disposition « débogage » : le panneau ouvert en conception n'y est pas
            if (reason == dbgEventReason.dbgEventReasonLaunchProgram || reason == dbgEventReason.dbgEventReasonAttachProgram)
                _ensureChatVisible?.Invoke();
        }

        private void OnEnterBreakMode(dbgEventReason reason, ref dbgExecutionAction action)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (reason != dbgEventReason.dbgEventReasonExceptionThrown && reason != dbgEventReason.dbgEventReasonExceptionNotHandled)
                return;
            // Le panneau n'est réaffiché que pour une exception (il en montre le détail) : un point d'arrêt ou
            // un pas ne doit pas rouvrir un panneau que l'utilisateur a fermé
            _ensureChatVisible?.Invoke();
            // Un outil (debugStart, debugContinue…) est en train d'attendre cet arrêt : il le rapportera lui-même
            if (_tools.IsWaiting) return;
            try
            {
                var state = _tools.CurrentState();
                BreakOnException?.Invoke(state);
            }
            catch (Exception ex)
            {
                _log?.Error("Événement débogueur : " + ex.Message);
            }
        }
    }
}
