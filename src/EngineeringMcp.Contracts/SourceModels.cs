namespace EngineeringMcp.Contracts;

public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    int Offset,
    int PageSize,
    int? NextOffset,
    bool HasMore);

public sealed record SourceLocation(
    string File,
    int Line,
    int Column,
    string Kind,
    string Name,
    string? Container = null);

public sealed record SourceReadResult(
    string File,
    int StartLine,
    int EndLine,
    string Content,
    bool Truncated);

public sealed record XamlFinding(
    string File,
    int Line,
    string Severity,
    string Rule,
    string Message,
    string Evidence);
