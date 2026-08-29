namespace EngineeringMcp.Contracts;

public sealed record AllowedProcessRule(
    string Name,
    string? Path = null,
    string? Sha256 = null,
    string? Publisher = null);

public sealed record ProcessPolicy(IReadOnlyList<AllowedProcessRule> Allow);

public sealed record FileSystemPolicy(
    IReadOnlyList<string> ReadRoots,
    IReadOnlyList<string> DenyGlobs);

public sealed record NetworkPolicy(
    string Default,
    IReadOnlyList<string> Allow);

public sealed record AuditPolicy(
    bool Enabled = true,
    string? Directory = null,
    int RetentionDays = 30);

public sealed record ScreenshotPolicy(
    bool Enabled = false,
    bool MaskPasswordControls = true,
    bool MaskSensitiveNames = true,
    bool FailClosedOnRedactionError = true,
    bool MaskTextControls = true);

public sealed record UiActionPolicy(
    IReadOnlyList<string> DenyAutomationIds,
    IReadOnlyList<string> DestructiveAutomationIds,
    IReadOnlyList<string> StatefulAutomationIds);

public sealed record McpPolicy(
    PermissionLevel PermissionCeiling,
    ProcessPolicy Processes,
    FileSystemPolicy Filesystem,
    NetworkPolicy Network,
    PiiMode Pii,
    AuditPolicy Audit,
    ScreenshotPolicy Screenshots,
    UiActionPolicy UiActions,
    bool AllowDestructiveActions = false,
    bool AllowPrivilegedDiagnostics = false,
    int PolicyVersion = 1,
    IReadOnlyList<string>? EnabledToolProfiles = null,
    IReadOnlyList<string>? EnabledTools = null,
    IReadOnlyList<string>? DisabledTools = null)
{
    public static McpPolicy LockedDownDefault => new(
        PermissionLevel.Metadata,
        new ProcessPolicy(Array.Empty<AllowedProcessRule>()),
        new FileSystemPolicy(Array.Empty<string>(), Array.Empty<string>()),
        new NetworkPolicy("deny", Array.Empty<string>()),
        PiiMode.Mask,
        new AuditPolicy(),
        new ScreenshotPolicy(Enabled: false),
        new UiActionPolicy(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>()),
        false,
        false);
}
