using System.ComponentModel;
using EngineeringMcp.Contracts;
using EngineeringMcp.Security;
using EngineeringMcp.Wpf;
using ModelContextProtocol.Server;

namespace EngineeringMcp.Host;

[McpServerToolType]
public static class AdvancedWpfTools
{
    [McpServerTool(Name = "wpf_wait_absent", UseStructuredContent = true), Description("Waits until a semantic element is absent. Returns only an opaque reference and state metadata; element text and values are never returned.")]
    public static ToolResult<UiConditionResult> WaitAbsent(
        [Description("Operating-system process identifier of an allowlisted target process.")] int processId,
        WpfSafeInspectionService service, ToolAuthorization auth, CancellationToken cancellationToken,
        [Description("Bounded timeout in milliseconds; maximum 60000.")] int timeoutMs = 5_000,
        [Description("Exact WPF AutomationId used only for local selection; it is not echoed in the result.")] string? automationId = null,
        [Description("Exact accessible name used only for local selection; it is not echoed in the result.")] string? name = null,
        [Description("WPF UI Automation control type, such as Button or DataGrid.")] string? controlType = null,
        [Description("Opaque UI element reference returned by an earlier MCP UI query.")] string? reference = null)
        => Run("wpf_wait_absent", processId, service, auth,
            () => service.WaitAbsent(processId, Selector(automationId, name, controlType, reference), timeoutMs, cancellationToken));

    [McpServerTool(Name = "wpf_wait_hidden", UseStructuredContent = true), Description("Waits until a semantic element is offscreen/hidden. Returns metadata only and never returns UI text or values.")]
    public static ToolResult<UiConditionResult> WaitHidden(
        [Description("Operating-system process identifier of an allowlisted target process.")] int processId,
        WpfSafeInspectionService service, ToolAuthorization auth, CancellationToken cancellationToken,
        [Description("Bounded timeout in milliseconds; maximum 60000.")] int timeoutMs = 5_000,
        [Description("Exact WPF AutomationId used only for local selection; it is not echoed in the result.")] string? automationId = null,
        [Description("Exact accessible name used only for local selection; it is not echoed in the result.")] string? name = null,
        [Description("WPF UI Automation control type, such as Button or DataGrid.")] string? controlType = null,
        [Description("Opaque UI element reference returned by an earlier MCP UI query.")] string? reference = null)
        => Run("wpf_wait_hidden", processId, service, auth,
            () => service.WaitHidden(processId, Selector(automationId, name, controlType, reference), timeoutMs, cancellationToken));

    [McpServerTool(Name = "wpf_wait_disabled", UseStructuredContent = true), Description("Waits until a semantic element is disabled. Returns metadata only and never evaluates application commands.")]
    public static ToolResult<UiConditionResult> WaitDisabled(
        [Description("Operating-system process identifier of an allowlisted target process.")] int processId,
        WpfSafeInspectionService service, ToolAuthorization auth, CancellationToken cancellationToken,
        [Description("Bounded timeout in milliseconds; maximum 60000.")] int timeoutMs = 5_000,
        [Description("Exact WPF AutomationId used only for local selection; it is not echoed in the result.")] string? automationId = null,
        [Description("Exact accessible name used only for local selection; it is not echoed in the result.")] string? name = null,
        [Description("WPF UI Automation control type, such as Button or DataGrid.")] string? controlType = null,
        [Description("Opaque UI element reference returned by an earlier MCP UI query.")] string? reference = null)
        => Run("wpf_wait_disabled", processId, service, auth,
            () => service.WaitDisabled(processId, Selector(automationId, name, controlType, reference), timeoutMs, cancellationToken));

    [McpServerTool(Name = "wpf_assert_exists", UseStructuredContent = true), Description("Asserts that a semantic element exists and returns a metadata-only assertion result.")]
    public static ToolResult<SafeUiAssertionResult> AssertExists(
        [Description("Operating-system process identifier of an allowlisted target process.")] int processId,
        WpfSafeInspectionService service, ToolAuthorization auth,
        [Description("Exact WPF AutomationId used only for local selection; it is not echoed in the result.")] string? automationId = null,
        [Description("Exact accessible name used only for local selection; it is not echoed in the result.")] string? name = null,
        [Description("WPF UI Automation control type, such as Button or DataGrid.")] string? controlType = null,
        [Description("Opaque UI element reference returned by an earlier MCP UI query.")] string? reference = null)
        => Run("wpf_assert_exists", processId, service, auth,
            () => service.AssertExists(processId, Selector(automationId, name, controlType, reference)));

    [McpServerTool(Name = "wpf_assert_not_exists", UseStructuredContent = true), Description("Asserts that a semantic element is absent and returns no element text or values.")]
    public static ToolResult<SafeUiAssertionResult> AssertNotExists(
        [Description("Operating-system process identifier of an allowlisted target process.")] int processId,
        WpfSafeInspectionService service, ToolAuthorization auth,
        [Description("Exact WPF AutomationId used only for local selection; it is not echoed in the result.")] string? automationId = null,
        [Description("Exact accessible name used only for local selection; it is not echoed in the result.")] string? name = null,
        [Description("WPF UI Automation control type, such as Button or DataGrid.")] string? controlType = null,
        [Description("Opaque UI element reference returned by an earlier MCP UI query.")] string? reference = null)
        => Run("wpf_assert_not_exists", processId, service, auth,
            () => service.AssertNotExists(processId, Selector(automationId, name, controlType, reference)));

    [McpServerTool(Name = "wpf_assert_pattern", UseStructuredContent = true), Description("Asserts that an element exposes a named UI Automation pattern without reading its text or value.")]
    public static ToolResult<SafeUiAssertionResult> AssertPattern(
        [Description("Operating-system process identifier of an allowlisted target process.")] int processId,
        [Description("Expected UI Automation pattern name, such as Invoke, Grid, Selection, or ExpandCollapse.")] string pattern,
        WpfSafeInspectionService service, ToolAuthorization auth,
        [Description("Exact WPF AutomationId used only for local selection; it is not echoed in the result.")] string? automationId = null,
        [Description("Exact accessible name used only for local selection; it is not echoed in the result.")] string? name = null,
        [Description("WPF UI Automation control type, such as Button or DataGrid.")] string? controlType = null,
        [Description("Opaque UI element reference returned by an earlier MCP UI query.")] string? reference = null)
        => Run("wpf_assert_pattern", processId, service, auth,
            () => service.AssertPattern(processId, Selector(automationId, name, controlType, reference), pattern));

    [McpServerTool(Name = "wpf_selector_audit", UseStructuredContent = true), Description("Audits selector stability using counts, issue codes, control types, and opaque references only. Raw names and AutomationIds are withheld.")]
    public static ToolResult<SelectorAuditSummary> SelectorAudit(
        [Description("Operating-system process identifier of an allowlisted target process.")] int processId,
        WpfSafeInspectionService service, ToolAuthorization auth)
        => Run("wpf_selector_audit", processId, service, auth, () => service.SelectorAudit(processId));

    [McpServerTool(Name = "wpf_duplicate_automation_ids", UseStructuredContent = true), Description("Reports duplicate AutomationId groups using non-reversible short fingerprints and opaque references; raw identifiers are never returned.")]
    public static ToolResult<DuplicateAutomationIdSummary> DuplicateAutomationIds(
        [Description("Operating-system process identifier of an allowlisted target process.")] int processId,
        WpfSafeInspectionService service, ToolAuthorization auth)
        => Run("wpf_duplicate_automation_ids", processId, service, auth, () => service.DuplicateAutomationIds(processId));

    [McpServerTool(Name = "wpf_control_inventory", UseStructuredContent = true), Description("Returns bounded UI Automation control-type counts only; UI text, names, values, and identifiers are omitted.")]
    public static ToolResult<UiInventorySummary> ControlInventory(
        [Description("Operating-system process identifier of an allowlisted target process.")] int processId,
        WpfSafeInspectionService service, ToolAuthorization auth)
        => Run("wpf_control_inventory", processId, service, auth, () => service.ControlInventory(processId));

    [McpServerTool(Name = "wpf_pattern_inventory", UseStructuredContent = true), Description("Returns bounded UI Automation pattern counts only; element content is omitted.")]
    public static ToolResult<UiInventorySummary> PatternInventory(
        [Description("Operating-system process identifier of an allowlisted target process.")] int processId,
        WpfSafeInspectionService service, ToolAuthorization auth)
        => Run("wpf_pattern_inventory", processId, service, auth, () => service.PatternInventory(processId));

    [McpServerTool(Name = "wpf_grid_summary", UseStructuredContent = true), Description("Returns DataGrid/Table structural counts only. It never returns row labels, cell text, values, or bound business objects.")]
    public static ToolResult<GridMetadataSummary> GridSummary(
        [Description("Operating-system process identifier of an allowlisted target process.")] int processId,
        WpfSafeInspectionService service, ToolAuthorization auth,
        [Description("Exact WPF AutomationId used only for local selection; it is not echoed in the result.")] string? automationId = null,
        [Description("Exact accessible name used only for local selection; it is not echoed in the result.")] string? name = null,
        [Description("WPF UI Automation control type; typically DataGrid or Table.")] string? controlType = null,
        [Description("Opaque UI element reference returned by an earlier MCP UI query.")] string? reference = null)
        => Run("wpf_grid_summary", processId, service, auth,
            () => service.GridSummary(processId, Selector(automationId, name, controlType, reference)));

    [McpServerTool(Name = "wpf_tree_summary", UseStructuredContent = true), Description("Returns TreeView structural counts and expansion capability only; node labels and values are omitted.")]
    public static ToolResult<TreeMetadataSummary> TreeSummary(
        [Description("Operating-system process identifier of an allowlisted target process.")] int processId,
        WpfSafeInspectionService service, ToolAuthorization auth,
        [Description("Exact WPF AutomationId used only for local selection; it is not echoed in the result.")] string? automationId = null,
        [Description("Exact accessible name used only for local selection; it is not echoed in the result.")] string? name = null,
        [Description("WPF UI Automation control type; typically Tree.")] string? controlType = null,
        [Description("Opaque UI element reference returned by an earlier MCP UI query.")] string? reference = null)
        => Run("wpf_tree_summary", processId, service, auth,
            () => service.TreeSummary(processId, Selector(automationId, name, controlType, reference)));

    [McpServerTool(Name = "wpf_items_summary", UseStructuredContent = true), Description("Returns aggregate list/grid/tree item counts and states only; item text and values are omitted.")]
    public static ToolResult<ItemsMetadataSummary> ItemsSummary(
        [Description("Operating-system process identifier of an allowlisted target process.")] int processId,
        WpfSafeInspectionService service, ToolAuthorization auth,
        [Description("Exact WPF AutomationId used only for local selection; it is not echoed in the result.")] string? automationId = null,
        [Description("Exact accessible name used only for local selection; it is not echoed in the result.")] string? name = null,
        [Description("WPF UI Automation control type, such as List, Tree, or DataGrid.")] string? controlType = null,
        [Description("Opaque UI element reference returned by an earlier MCP UI query.")] string? reference = null)
        => Run("wpf_items_summary", processId, service, auth,
            () => service.ItemsSummary(processId, Selector(automationId, name, controlType, reference)));

    [McpServerTool(Name = "wpf_accessibility_summary", UseStructuredContent = true), Description("Returns aggregate accessibility/testability counts only. Accessible names and application content are never returned.")]
    public static ToolResult<AccessibilityMetadataSummary> AccessibilitySummary(
        [Description("Operating-system process identifier of an allowlisted target process.")] int processId,
        WpfSafeInspectionService service, ToolAuthorization auth)
        => Run("wpf_accessibility_summary", processId, service, auth, () => service.AccessibilitySummary(processId));

    [McpServerTool(Name = "wpf_window_state", UseStructuredContent = true), Description("Returns bounded top-level window geometry and state with opaque references. Window titles are deliberately omitted.")]
    public static ToolResult<WindowStateSummary> WindowState(
        [Description("Operating-system process identifier of an allowlisted target process.")] int processId,
        WpfSafeInspectionService service, ToolAuthorization auth)
        => Run("wpf_window_state", processId, service, auth, () => service.WindowState(processId));

    private static ToolResult<T> Run<T>(string name, int processId, WpfSafeInspectionService service, ToolAuthorization auth, Func<ToolResult<T>> action)
        => ToolRun.Sync(auth, ToolPolicies.Read(name, "wpf.uia.read"), processId.ToString(), action);

    private static UiSelector Selector(string? automationId, string? name, string? controlType, string? reference)
        => new(Reference: reference, AutomationId: automationId, Name: name, ControlType: controlType);
}
