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
    string? ExceptionStackTrace);

public sealed record BackendHealthObservation(
    string Status,
    DateTimeOffset ObservedAtUtc,
    int BufferedRequests,
    string AdapterVersion);

public sealed record BackendProbeRequest(string Token, string Operation, int Limit = 100);

public sealed record BackendProbeResponse(bool Success, object? Value = null, string? ErrorCode = null, string? ErrorMessage = null);
