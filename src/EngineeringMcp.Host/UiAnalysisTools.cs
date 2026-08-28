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
    public static ToolResult<IReadOnlyList<UiAuditFinding>> Accessibility([Description("Operating-system process identifier of an allowlisted target process.")] int processId, UiAuditService service, ToolAuthorization auth)
        => ToolRun.Sync(auth, new ToolPolicy("a11y_audit", PermissionLevel.UiRead, RiskClass.Read, "a11y.audit"), processId.ToString(), () => service.AccessibilityAudit(processId));

    [McpServerTool(Name = "gui_audit", UseStructuredContent = true), Description("Audits measurable GUI geometry/clipping-risk evidence; it does not label subjective design taste as fact.")]
    public static ToolResult<IReadOnlyList<UiAuditFinding>> Gui([Description("Operating-system process identifier of an allowlisted target process.")] int processId, UiAuditService service, ToolAuthorization auth)
        => ToolRun.Sync(auth, new ToolPolicy("gui_audit", PermissionLevel.UiRead, RiskClass.Read, "gui.audit"), processId.ToString(), () => service.GuiAudit(processId));

    [McpServerTool(Name = "ux_review", UseStructuredContent = true), Description("Provides explicitly HEURISTIC UX observations. These findings are not deterministic truth and cannot alone fail a security/build gate.")]
    public static ToolResult<IReadOnlyList<UiAuditFinding>> Ux([Description("Operating-system process identifier of an allowlisted target process.")] int processId, UiAuditService service, ToolAuthorization auth)
        => ToolRun.Sync(auth, new ToolPolicy("ux_review", PermissionLevel.UiRead, RiskClass.Read, "ux.heuristics"), processId.ToString(), () => service.UxHeuristicReview(processId));
}