namespace EngineeringMcp.Contracts;

public sealed record ProbeRequest(
    string Token,
    string Operation,
    string? AutomationId = null,
    string? Name = null,
    string? Property = null,
    string? ResourceKey = null);

public sealed record ProbeResponse(
    bool Success,
    object? Value = null,
    string? ErrorCode = null,
    string? ErrorMessage = null);

public sealed record BindingDiagnostic(
    string Element,
    string Property,
    string? Path,
    string? Status,
    string? Error);

public sealed record WpfResourceObservation(
    string Key,
    string Scope,
    string? DictionarySource,
    string? ValueType,
    string? DisplayValue);

public sealed record WpfExceptionObservation(
    DateTimeOffset TimestampUtc,
    string Source,
    string Type,
    string Message,
    string? StackTrace);
