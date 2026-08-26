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
