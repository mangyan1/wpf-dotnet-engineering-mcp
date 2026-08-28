namespace EngineeringMcp.Contracts;

using System.Text.Json.Serialization;

public enum PermissionLevel
{
    Metadata = 0,
    UiRead = 1,
    UiInteraction = 2,
    ApplicationDiagnostics = 3,
    SensitiveDiagnostics = 4,
    DebugMutation = 5
}

public enum RiskClass
{
    Read,
    SafeMutation,
    StatefulMutation,
    Destructive,
    Privileged
}

public enum EvidenceKind
{
    Observed,
    Correlated,
    Inferred,
    Unknown
}

public enum DataClassification
{
    Public,
    Internal,
    Confidential,
    Pii,
    Secret,
    Credential,
    Unknown
}

public enum PiiMode
{
    Off,
    Mask,
    Hash,
    Remove
}

public sealed record ToolFailure(
    string Code,
    string Message,
    bool Retryable = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Remediation = null);

public sealed record ToolResult<T>(
    bool Success,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] T? Value = default,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ToolFailure? Error = null)
{
    public static ToolResult<T> Ok(T value) => new(true, value);
    public static ToolResult<T> Fail(string code, string message, bool retryable = false, string? remediation = null)
        => new(false, default, new ToolFailure(code, message, retryable, remediation));
}

public sealed record EvidenceItem(
    EvidenceKind Kind,
    string Claim,
    string Source,
    string? CorrelationId = null,
    DateTimeOffset? ObservedAtUtc = null);
