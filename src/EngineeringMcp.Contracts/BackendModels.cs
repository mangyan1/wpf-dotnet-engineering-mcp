namespace EngineeringMcp.Contracts;

public sealed record BackendRequestObservation(
    DateTimeOffset TimestampUtc,
    string Method,
    string Path,
    int StatusCode,
    double DurationMs,
    string? TraceId,
    string? ExceptionType,
    string? ExceptionMessage,
    string? ExceptionStackTrace,
    string? CorrelationId = null,
    long Sequence = 0);

public sealed record BackendHealthObservation(
    string Status,
    DateTimeOffset ObservedAtUtc,
    int BufferedRequests,
    string AdapterVersion,
    int ProcessId = 0,
    bool ActionCorrelationSupported = false);

public sealed record BackendCorrelationObservation(
    string CorrelationId,
    long AfterSequence,
    DateTimeOffset StartedAtUtc);

public sealed record BackendProbeRequest(
    string Token,
    string Operation,
    int Limit = 100,
    string? CorrelationId = null,
    long? AfterSequence = null);

public sealed record BackendProbeResponse(bool Success, object? Value = null, string? ErrorCode = null, string? ErrorMessage = null);
