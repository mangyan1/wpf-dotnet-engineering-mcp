namespace EngineeringMcp.Contracts;

public sealed record RuntimeProcessInfo(
    int ProcessId,
    string? RuntimeVersion,
    string? CommandLine,
    string? OperatingSystem,
    string? Architecture);

public sealed record TraceHandle(
    string TraceId,
    int ProcessId,
    string LocalPath,
    DateTimeOffset StartedAtUtc,
    string State);

public sealed record ExceptionObservation(
    DateTimeOffset TimestampUtc,
    string Type,
    string Message,
    string? StackTrace,
    int ProcessId,
    string Source);

public sealed record ThreadStackSummary(
    uint OsThreadId,
    bool IsAlive,
    string? CurrentExceptionType,
    IReadOnlyList<string> Frames);

public sealed record DumpAnalysisSummary(
    string DumpPath,
    string RuntimeVersion,
    IReadOnlyList<ThreadStackSummary> Threads,
    int ThreadCount,
    bool Truncated);

public sealed record DiagnosisReport(
    string CorrelationId,
    string Status,
    IReadOnlyList<EvidenceItem> Evidence,
    IReadOnlyList<string> Unknowns,
    IReadOnlyList<string> NextVerification,
    DateTimeOffset CompletedAtUtc);

public sealed record RuntimeCounterObservation(
    string Name,
    double Value,
    string? Unit,
    string Kind,
    DateTimeOffset ObservedAtUtc);

public sealed record ProcessThreadObservation(
    int OsThreadId,
    string State,
    string? WaitReason,
    int BasePriority);

public sealed record ProcessModuleObservation(string Name);

public sealed record DiagnosticActionResult<T>(
    T ActionResult,
    bool CaptureAvailable,
    string? CaptureWarningCode);
