namespace ClaudeVS.Chat
{
    // Types des messages échangés entre l'extension et l'interface web du panneau.
    // À garder synchronisé avec Chat/web/js/protocol.js.
    // (Les types du protocole stream-json du CLI — control_request, stream_event, result… — n'en font pas partie.)

    /// <summary>Messages envoyés par l'interface à l'extension.</summary>
    internal static class ToHost
    {
        public const string Ready = "ready";
        public const string Send = "send";
        public const string SearchFiles = "searchFiles";
        public const string Interrupt = "interrupt";
        public const string Permission = "permission";
        public const string ShowDiff = "showDiff";
        public const string SetMode = "setMode";
        public const string SetModel = "setModel";
        public const string SetEffort = "setEffort";
        public const string Rewind = "rewind";
        public const string SetAttachSelection = "setAttachSelection";
        public const string SetAutoFormat = "setAutoFormat";
        public const string SetAutoAnalyze = "setAutoAnalyze";
        public const string AnalyzeException = "analyzeException";
        public const string NewChat = "newChat";
        public const string ListSessions = "listSessions";
        public const string Resume = "resume";
        public const string OpenFile = "openFile";
    }

    /// <summary>Messages postés par l'extension à l'interface.</summary>
    internal static class FromHost
    {
        public const string Cli = "cli";
        public const string Status = "status";
        public const string Error = "error";
        public const string PermissionResolved = "permissionResolved";
        public const string Cleared = "cleared";
        public const string History = "history";
        public const string Insert = "insert";
        public const string Selection = "selection";
        public const string Focus = "focus";
        public const string Notice = "notice";
        public const string DebugEvent = "debugEvent";
        public const string Context = "context";
        public const string RewindPreview = "rewindPreview";
        public const string RewindDone = "rewindDone";
        public const string Settings = "settings";
        public const string Sessions = "sessions";
        public const string Files = "files";
        public const string Theme = "theme";
    }
}
