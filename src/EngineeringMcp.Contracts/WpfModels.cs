namespace EngineeringMcp.Contracts;

public sealed record RectDto(double X, double Y, double Width, double Height);

public sealed record ProcessDescriptor(
    int ProcessId,
    string Name,
    string? ExecutablePath,
    bool Allowed,
    string AuthorizationReason);

public sealed record WindowDescriptor(
    string Reference,
    string Title,
    int ProcessId,
    RectDto Bounds,
    bool IsEnabled,
    bool IsOffscreen);

public sealed record UiElementSnapshot(
    string Reference,
    string? ParentReference,
    string ControlType,
    string Name,
    string AutomationId,
    string ClassName,
    string FrameworkType,
    RectDto Bounds,
    bool IsEnabled,
    bool IsOffscreen,
    bool IsKeyboardFocusable,
    bool IsPassword,
    IReadOnlyList<string> SupportedPatterns,
    int Depth);

public sealed record UiSnapshot(
    int ProcessId,
    string WindowReference,
    DateTimeOffset CapturedAtUtc,
    IReadOnlyList<UiElementSnapshot> Elements,
    bool Truncated,
    int MaxElements);

public sealed record UiSelector(
    string? Reference = null,
    string? AutomationId = null,
    string? Name = null,
    string? ControlType = null,
    string? ClassName = null);

public sealed record SanitizedScreenshot(
    string MediaType,
    string Base64,
    int Width,
    int Height,
    int RedactedRegions,
    string RedactionMode);

public sealed record ScreenshotToolOutput(
    bool Success,
    string MediaType,
    int Width,
    int Height,
    int RedactedRegions,
    string RedactionMode);

public sealed record UiAssertionResult(
    bool Passed,
    UiElementSnapshot Actual,
    IReadOnlyList<string> Failures);

public sealed record SafeUiElementState(
    string Reference,
    string ControlType,
    bool IsEnabled,
    bool IsOffscreen,
    bool IsKeyboardFocusable,
    bool IsPassword,
    IReadOnlyList<string> SupportedPatterns);

public sealed record UiConditionResult(
    bool Satisfied,
    string Condition,
    SafeUiElementState? Element,
    string Observation);

public sealed record SafeUiAssertionResult(
    bool Passed,
    string Assertion,
    SafeUiElementState? Element,
    IReadOnlyList<string> Failures);

public sealed record SelectorAuditFinding(
    string Severity,
    string Issue,
    string ElementReference,
    string ControlType);

public sealed record SelectorAuditSummary(
    int ElementCount,
    int ActionableElementCount,
    int StableSelectorCount,
    int MissingAutomationIdCount,
    int DuplicateAutomationIdGroupCount,
    IReadOnlyList<SelectorAuditFinding> Findings,
    bool Truncated,
    bool MetadataOnly = true);

public sealed record DuplicateAutomationIdGroup(
    string IdentifierFingerprint,
    int Count,
    IReadOnlyList<string> ElementReferences);

public sealed record DuplicateAutomationIdSummary(
    int DuplicateGroupCount,
    IReadOnlyList<DuplicateAutomationIdGroup> Groups,
    bool Truncated,
    bool MetadataOnly = true);

public sealed record UiCountEntry(string Key, int Count);

public sealed record UiInventorySummary(
    int ElementCount,
    IReadOnlyList<UiCountEntry> Counts,
    bool Truncated,
    bool MetadataOnly = true);

public sealed record GridMetadataSummary(
    int RowCount,
    int ColumnHeaderCount,
    int CellLikeElementCount,
    int VisibleRowCount,
    bool Truncated,
    bool MetadataOnly = true);

public sealed record TreeMetadataSummary(
    int NodeCount,
    int VisibleNodeCount,
    int ExpandableNodeCount,
    int MaximumObservedDepth,
    bool Truncated,
    bool MetadataOnly = true);

public sealed record ItemsMetadataSummary(
    int ItemCount,
    int VisibleItemCount,
    int EnabledItemCount,
    int SelectableItemCount,
    bool Truncated,
    bool MetadataOnly = true);

public sealed record AccessibilityMetadataSummary(
    int InteractiveElementCount,
    int MissingAccessibleIdentityCount,
    int KeyboardInaccessibleCount,
    int PasswordControlCount,
    int OffscreenInteractiveCount,
    bool Truncated,
    bool MetadataOnly = true);

public sealed record SafeWindowState(
    string Reference,
    RectDto Bounds,
    bool IsEnabled,
    bool IsOffscreen);

public sealed record WindowStateSummary(
    int WindowCount,
    IReadOnlyList<SafeWindowState> Windows,
    bool MetadataOnly = true);
