namespace EntLimit.Core;

public sealed class ManagedApp
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string ExecutablePath { get; set; } = "";
    public string Source { get; set; } = "Manual";
    public bool Enabled { get; set; } = true;
}

public sealed class AppCatalogEntry
{
    public string Name { get; set; } = "";
    public string ExecutablePath { get; set; } = "";
    public string Source { get; set; } = "";
    public bool HasVisibleWindow { get; set; }
}

public sealed class AppConfig
{
    public int SchemaVersion { get; set; } = 1;
    public List<ManagedApp> Apps { get; set; } = [];
    public List<string> Domains { get; set; } = [];
    public int UnlockMinutes { get; set; } = 90;
    public int WarningMinutes { get; set; } = 10;
    public bool SoundEnabled { get; set; } = true;
    public bool OverlayEnabled { get; set; }
    public bool StartupEnabled { get; set; }
    public bool GuardEnabled { get; set; }
    public bool MonitoringEnabled { get; set; } = true;
}

public sealed record ProcessIdentity(int Pid, DateTimeOffset StartedAtUtc, string ExecutablePath);
public enum SessionPhase { Locked, Ready, Active, Draining }

public sealed class SessionState
{
    public int SchemaVersion { get; set; } = 1;
    public SessionPhase Phase { get; set; } = SessionPhase.Locked;
    public int GrantedMinutes { get; set; } = 90;
    public DateTimeOffset? GrantedAtUtc { get; set; }
    public DateTimeOffset? StartedAtUtc { get; set; }
    public DateTimeOffset? DeadlineUtc { get; set; }
    public DateTimeOffset LastObservedUtc { get; set; }
    public bool WarningDelivered { get; set; }
    public bool ExpiryDelivered { get; set; }
    public List<ProcessIdentity> ActiveProcesses { get; set; } = [];
    public List<ProcessIdentity> DrainingProcesses { get; set; } = [];
    public List<LearningRecord> History { get; set; } = [];
}

public sealed class LearningRecord
{
    public string AttemptId { get; set; } = "";
    public string QuestionId { get; set; } = "";
    public string TopicId { get; set; } = "";
    public string Mode { get; set; } = "";
    public string Takeaway { get; set; } = "";
    public DateTimeOffset CompletedAtUtc { get; set; }
}

public sealed class Question
{
    public string Id { get; set; } = "";
    public string TopicId { get; set; } = "";
    public string Mode { get; set; } = "standard";
    public string Title { get; set; } = "";
    public string Prompt { get; set; } = "";
    public string Explanation { get; set; } = "";
    public string Takeaway { get; set; } = "";
    public string SourceUrl { get; set; } = "";
    public string[] Options { get; set; } = [];
    public int CorrectIndex { get; set; }
    public int EstimatedMinutes { get; set; }
}

public readonly record struct TickResult(bool Warning, bool Expired, bool Changed);

public sealed class IpcRequest
{
    public string Command { get; set; } = "status";
    public AppConfig? Config { get; set; }
    public string? QuestionId { get; set; }
    public string? AttemptId { get; set; }
    public string? AppId { get; set; }
    public int AnswerIndex { get; set; }
    public bool Light { get; set; }
    public string? ExerciseId { get; set; }
    public Dictionary<string, string>? Answers { get; set; }
    public string? Code { get; set; }
    public string? PythonPath { get; set; }
    public string? RuntimePath { get; set; }
    public bool ExamplesOnly { get; set; }
    public string? JobId { get; set; }
    public string? PackJson { get; set; }
    public Dictionary<string, string>? PackChoices { get; set; }
    public string? ExpectedCatalogRevision { get; set; }
    public ProcessIdentity? GateProcess { get; set; }
    public long WindowHandle { get; set; }
}

public sealed class IpcResponse
{
    public bool EmergencyStopped { get; set; }
    public bool Ok { get; set; } = true;
    public string? Error { get; set; }
    public AppConfig Config { get; set; } = new();
    public SessionState Session { get; set; } = new();
    public List<AppCatalogEntry> Catalog { get; set; } = [];
    public List<Question> Questions { get; set; } = [];
    public List<ManagedWindowStatus> ManagedWindows { get; set; } = [];
    public List<Exercise> Exercises { get; set; } = [];
    public ExerciseGrade? Grade { get; set; }
    public CodeJobStatus? CodeJob { get; set; }
    public string? PythonPath { get; set; }
    public List<TopicDefinition> Topics { get; set; } = [];
    public List<ContentPackInfo> ContentPacks { get; set; } = [];
    public List<string> ContentWarnings { get; set; } = [];
    public ContentPackPreview? PackPreview { get; set; }
    public string? PromptText { get; set; }
    public bool GateDenied { get; set; }
    public List<RunnerPluginInfo> RunnerPlugins { get; set; } = [];
}

public sealed class CodeJobStatus
{
    public string JobId { get; set; } = "";
    public string ExerciseId { get; set; } = "";
    public string State { get; set; } = "running";
    public string Message { get; set; } = "";
    public bool ExamplesOnly { get; set; }
    public bool AnalysisOnly { get; set; }
    public CodeRunResult? Result { get; set; }
}

public sealed class ManagedWindowStatus
{
    public string AppId { get; set; } = "";
    public string ProcessName { get; set; } = "";
    public int ProcessId { get; set; }
    public long WindowHandle { get; set; }
    public bool IsMinimized { get; set; }
    public string MatchKind { get; set; } = "primary";
}
