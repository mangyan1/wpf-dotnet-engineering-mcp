using System.ComponentModel;
using EngineeringMcp.Contracts;
using EngineeringMcp.Security;
using EngineeringMcp.Wpf;
using ModelContextProtocol.Server;

namespace EngineeringMcp.Host;

[McpServerToolType]
public static class UiAnalysisTools
{
    [McpServerTool(Name = "a11y_audit", UseStructuredContent = true), Description("Audits measurable UI Automation accessibility properties. Findings are evidence-based and bounded to the observed UIA tree.")]
    public static ToolResult<IReadOnlyList<UiAuditFinding>> Accessibility(int processId, IUiAuditService service, IToolAuthorization auth)
        => Run("a11y_audit", "a11y.audit", processId, service.AccessibilityAudit, auth);

    [McpServerTool(Name = "gui_audit", UseStructuredContent = true), Description("Audits measurable GUI geometry/clipping-risk evidence; it does not label subjective design taste as fact.")]
    public static ToolResult<IReadOnlyList<UiAuditFinding>> Gui(int processId, IUiAuditService service, IToolAuthorization auth)
        => Run("gui_audit", "gui.audit", processId, service.GuiAudit, auth);

    [McpServerTool(Name = "ux_review", UseStructuredContent = true), Description("Provides explicitly HEURISTIC UX observations. These findings are not deterministic truth and cannot alone fail a security/build gate.")]
    public static ToolResult<IReadOnlyList<UiAuditFinding>> Ux(int processId, IUiAuditService service, IToolAuthorization auth)
        => Run("ux_review", "ux.heuristics", processId, service.UxHeuristicReview, auth);

    private static ToolResult<IReadOnlyList<UiAuditFinding>> Run(string tool, string capability, int processId, Func<int, ToolResult<IReadOnlyList<UiAuditFinding>>> action, IToolAuthorization auth)
    {
        var policy = new ToolPolicy(tool, PermissionLevel.UiRead, RiskClass.Read, capability);
        var allowed = auth.Authorize(policy, processId.ToString());
        if (!allowed.Success) return ToolResult<IReadOnlyList<UiAuditFinding>>.Fail(allowed.Error!.Code, allowed.Error.Message);
        var result = action(processId);
        auth.Complete(allowed.Value!, policy, processId.ToString(), result.Success, result.Success ? "OK" : result.Error?.Code ?? "FAILED");
        return result;
    }
}
