using EngineeringMcp.Contracts;

namespace EngineeringMcp.Wpf;

public sealed record UiAuditFinding(string Severity, string Category, string ElementReference, string Message, string Evidence);

public sealed class UiAuditService(WpfAutomationService wpf)
{
    public ToolResult<IReadOnlyList<UiAuditFinding>> AccessibilityAudit(int processId)
    {
        var snapshot = wpf.Snapshot(processId, maxElements: 2_000, maxDepth: 32);
        if (!snapshot.Success || snapshot.Value is null)
            return ToolResult<IReadOnlyList<UiAuditFinding>>.Fail(snapshot.Error!.Code, snapshot.Error.Message);

        var findings = new List<UiAuditFinding>();
        foreach (var e in snapshot.Value.Elements)
        {
            var actionable = e.ControlType is "Button" or "Edit" or "CheckBox" or "RadioButton" or "ComboBox" or "ListItem" or "MenuItem" or "Hyperlink" or "TabItem";
            if (actionable && string.IsNullOrWhiteSpace(e.Name) && string.IsNullOrWhiteSpace(e.AutomationId))
                findings.Add(new("high", "accessibility-name", e.Reference, "Interactive element has neither accessible Name nor AutomationId.", e.ControlType));
            if (actionable && !e.IsKeyboardFocusable && e.IsEnabled && !e.IsOffscreen)
                findings.Add(new("medium", "keyboard-focus", e.Reference, "Enabled interactive element is not keyboard-focusable according to UI Automation.", e.ControlType));
        }
        return ToolResult<IReadOnlyList<UiAuditFinding>>.Ok(findings);
    }

    public ToolResult<IReadOnlyList<UiAuditFinding>> GuiAudit(int processId)
    {
        var snapshot = wpf.Snapshot(processId, maxElements: 2_000, maxDepth: 32);
        if (!snapshot.Success || snapshot.Value is null)
            return ToolResult<IReadOnlyList<UiAuditFinding>>.Fail(snapshot.Error!.Code, snapshot.Error.Message);

        var findings = new List<UiAuditFinding>();
        foreach (var e in snapshot.Value.Elements)
        {
            if (!e.IsOffscreen && (e.Bounds.Width <= 0 || e.Bounds.Height <= 0))
                findings.Add(new("medium", "geometry", e.Reference, "Visible UIA element has zero/negative bounds.", $"{e.Bounds.Width}x{e.Bounds.Height}"));
            if (e.ControlType is "Text" or "Button" && !e.IsOffscreen && e.Bounds.Height < 8)
                findings.Add(new("medium", "clipping-risk", e.Reference, "Visible text/action element has an unusually small height; clipping is possible.", $"height={e.Bounds.Height}"));
        }
        return ToolResult<IReadOnlyList<UiAuditFinding>>.Ok(findings);
    }

    public ToolResult<IReadOnlyList<UiAuditFinding>> UxHeuristicReview(int processId)
    {
        var snapshot = wpf.Snapshot(processId, maxElements: 2_000, maxDepth: 32);
        if (!snapshot.Success || snapshot.Value is null)
            return ToolResult<IReadOnlyList<UiAuditFinding>>.Fail(snapshot.Error!.Code, snapshot.Error.Message);

        var findings = new List<UiAuditFinding>();
        foreach (var e in snapshot.Value.Elements)
        {
            if (e.ControlType == "Button" && e.IsEnabled && !e.IsOffscreen && string.IsNullOrWhiteSpace(e.Name))
                findings.Add(new("heuristic", "action-clarity", e.Reference, "HEURISTIC: visible enabled button has no accessible label, so its purpose may be unclear.", "AssessmentType=HEURISTIC"));
            if (e.ControlType == "Window" && e.Name.Contains("Error", StringComparison.OrdinalIgnoreCase))
                findings.Add(new("heuristic", "error-recovery", e.Reference, "HEURISTIC: an error window is present; inspect its controls to verify recovery guidance is offered.", "AssessmentType=HEURISTIC"));
        }
        return ToolResult<IReadOnlyList<UiAuditFinding>>.Ok(findings);
    }
}
