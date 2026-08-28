using EngineeringMcp.Contracts;

namespace EngineeringMcp.Wpf;

public sealed class WpfSafeInspectionService(WpfAutomationService automation)
{
    public ToolResult<UiConditionResult> WaitAbsent(int processId, UiSelector selector, int timeoutMs, CancellationToken cancellationToken)
        => WaitFor(processId, selector, timeoutMs, cancellationToken, "absent", current => current is null);

    public ToolResult<UiConditionResult> WaitHidden(int processId, UiSelector selector, int timeoutMs, CancellationToken cancellationToken)
        => WaitFor(processId, selector, timeoutMs, cancellationToken, "hidden", current => current?.IsOffscreen == true);

    public ToolResult<UiConditionResult> WaitDisabled(int processId, UiSelector selector, int timeoutMs, CancellationToken cancellationToken)
        => WaitFor(processId, selector, timeoutMs, cancellationToken, "disabled", current => current?.IsEnabled == false);

    public ToolResult<SafeUiAssertionResult> AssertExists(int processId, UiSelector selector)
    {
        var current = automation.Query(processId, selector);
        if (IsNotFound(current.Error))
            return ToolResult<SafeUiAssertionResult>.Ok(new(false, "exists", null, ["Element was not present."]));
        if (!current.Success || current.Value is null) return Failure<SafeUiAssertionResult>(current.Error!);
        return ToolResult<SafeUiAssertionResult>.Ok(new(true, "exists", SafeUiAnalysis.ElementState(current.Value), []));
    }

    public ToolResult<SafeUiAssertionResult> AssertNotExists(int processId, UiSelector selector)
    {
        var current = automation.Query(processId, selector);
        if (IsNotFound(current.Error))
            return ToolResult<SafeUiAssertionResult>.Ok(new(true, "not-exists", null, []));
        if (!current.Success || current.Value is null) return Failure<SafeUiAssertionResult>(current.Error!);
        return ToolResult<SafeUiAssertionResult>.Ok(new(false, "not-exists", SafeUiAnalysis.ElementState(current.Value), ["Element was present."]));
    }

    public ToolResult<SafeUiAssertionResult> AssertPattern(int processId, UiSelector selector, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern) || pattern.Length > 128)
            return ToolResult<SafeUiAssertionResult>.Fail("PATTERN_REQUIRED", "A bounded UI Automation pattern name is required.");
        var current = automation.Query(processId, selector);
        if (!current.Success || current.Value is null) return Failure<SafeUiAssertionResult>(current.Error!);
        var supported = current.Value.SupportedPatterns.Any(value => value.Contains(pattern.Trim(), StringComparison.OrdinalIgnoreCase));
        return ToolResult<SafeUiAssertionResult>.Ok(new(
            supported,
            "supports-pattern",
            SafeUiAnalysis.ElementState(current.Value),
            supported ? [] : ["Requested UI Automation pattern was not observed."]));
    }

    public ToolResult<SelectorAuditSummary> SelectorAudit(int processId)
        => AnalyzeRoot(processId, SafeUiAnalysis.SelectorAudit);

    public ToolResult<DuplicateAutomationIdSummary> DuplicateAutomationIds(int processId)
        => AnalyzeRoot(processId, SafeUiAnalysis.DuplicateAutomationIds);

    public ToolResult<UiInventorySummary> ControlInventory(int processId)
        => AnalyzeRoot(processId, SafeUiAnalysis.ControlInventory);

    public ToolResult<UiInventorySummary> PatternInventory(int processId)
        => AnalyzeRoot(processId, SafeUiAnalysis.PatternInventory);

    public ToolResult<GridMetadataSummary> GridSummary(int processId, UiSelector selector)
        => AnalyzeSelection(processId, selector, SafeUiAnalysis.GridSummary);

    public ToolResult<TreeMetadataSummary> TreeSummary(int processId, UiSelector selector)
        => AnalyzeSelection(processId, selector, SafeUiAnalysis.TreeSummary);

    public ToolResult<ItemsMetadataSummary> ItemsSummary(int processId, UiSelector selector)
        => AnalyzeSelection(processId, selector, SafeUiAnalysis.ItemsSummary);

    public ToolResult<AccessibilityMetadataSummary> AccessibilitySummary(int processId)
        => AnalyzeRoot(processId, SafeUiAnalysis.AccessibilitySummary);

    public ToolResult<WindowStateSummary> WindowState(int processId)
    {
        var windows = automation.ListWindows(processId);
        if (!windows.Success || windows.Value is null) return Failure<WindowStateSummary>(windows.Error!);
        return ToolResult<WindowStateSummary>.Ok(new(
            windows.Value.Count,
            windows.Value.Take(32).Select(window => new SafeWindowState(window.Reference, window.Bounds, window.IsEnabled, window.IsOffscreen)).ToArray()));
    }

    private ToolResult<UiConditionResult> WaitFor(
        int processId,
        UiSelector selector,
        int timeoutMs,
        CancellationToken cancellationToken,
        string condition,
        Func<UiElementSnapshot?, bool> predicate)
    {
        timeoutMs = Math.Clamp(timeoutMs, 50, 60_000);
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 <= deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = automation.Query(processId, selector);
            if (current.Success && current.Value is not null)
            {
                if (predicate(current.Value))
                    return ToolResult<UiConditionResult>.Ok(new(true, condition, SafeUiAnalysis.ElementState(current.Value), "condition-observed"));
            }
            else if (IsNotFound(current.Error))
            {
                if (predicate(null))
                    return ToolResult<UiConditionResult>.Ok(new(true, condition, null, "element-absent"));
            }
            else
            {
                return Failure<UiConditionResult>(current.Error!);
            }

            if (cancellationToken.WaitHandle.WaitOne(100)) cancellationToken.ThrowIfCancellationRequested();
        }

        return ToolResult<UiConditionResult>.Fail("WAIT_TIMEOUT", $"Timed out waiting for the metadata-only '{condition}' condition.");
    }

    private ToolResult<T> AnalyzeRoot<T>(int processId, Func<UiSnapshot, T> analyze)
    {
        var snapshot = automation.Snapshot(processId, maxElements: 2_000, maxDepth: 32);
        return !snapshot.Success || snapshot.Value is null
            ? Failure<T>(snapshot.Error!)
            : ToolResult<T>.Ok(analyze(snapshot.Value));
    }

    private ToolResult<T> AnalyzeSelection<T>(int processId, UiSelector selector, Func<UiSnapshot, T> analyze)
    {
        var selected = automation.Query(processId, selector);
        if (!selected.Success || selected.Value is null) return Failure<T>(selected.Error!);
        var snapshot = automation.Snapshot(processId, selected.Value.Reference, maxElements: 2_000, maxDepth: 32);
        return !snapshot.Success || snapshot.Value is null
            ? Failure<T>(snapshot.Error!)
            : ToolResult<T>.Ok(analyze(snapshot.Value));
    }

    private static bool IsNotFound(ToolFailure? failure)
        => failure?.Code is "ELEMENT_NOT_FOUND" or "ELEMENT_REFERENCE_NOT_FOUND";

    private static ToolResult<T> Failure<T>(ToolFailure failure)
        => ToolResult<T>.Fail(failure.Code, failure.Message, failure.Retryable, failure.Remediation);
}
