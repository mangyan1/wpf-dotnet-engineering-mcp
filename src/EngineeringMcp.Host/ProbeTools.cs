using System.ComponentModel;
using EngineeringMcp.Contracts;
using EngineeringMcp.Security;
using EngineeringMcp.Wpf;
using EngineeringMcp.Wpf.WpfUi;
using ModelContextProtocol.Server;

namespace EngineeringMcp.Host;

[McpServerToolType]
public static class ProbeTools
{
    [McpServerTool(Name = "wpf_probe_status", UseStructuredContent = true), Description("Checks the explicitly installed local WPF probe. The probe is never injected by this MCP server.")]
    public static Task<ToolResult<ProbeResponse>> Status(int processId, IWpfProbeClient probe, IToolAuthorization auth, CancellationToken cancellationToken)
        => Request("wpf_probe_status", processId, new ProbeRequest(string.Empty, "status"), probe, auth, cancellationToken);

    [McpServerTool(Name = "wpf_probe_visual_tree", UseStructuredContent = true), Description("Returns a bounded WPF visual tree from the authorized in-process probe.")]
    public static Task<ToolResult<ProbeResponse>> VisualTree(int processId, string? automationId, string? name, IWpfProbeClient probe, IToolAuthorization auth, CancellationToken cancellationToken)
        => Request("wpf_probe_visual_tree", processId, new ProbeRequest(string.Empty, "visualTree", AutomationId: automationId, Name: name), probe, auth, cancellationToken);

    [McpServerTool(Name = "wpf_probe_logical_tree", UseStructuredContent = true), Description("Returns a bounded WPF logical tree from the authorized in-process probe.")]
    public static Task<ToolResult<ProbeResponse>> LogicalTree(int processId, string? automationId, string? name, IWpfProbeClient probe, IToolAuthorization auth, CancellationToken cancellationToken)
        => Request("wpf_probe_logical_tree", processId, new ProbeRequest(string.Empty, "logicalTree", AutomationId: automationId, Name: name), probe, auth, cancellationToken);

    [McpServerTool(Name = "wpf_probe_datacontext", UseStructuredContent = true), Description("Returns DataContext TYPE evidence only; it never serializes arbitrary ViewModel values.")]
    public static Task<ToolResult<ProbeResponse>> DataContext(int processId, string? automationId, string? name, IWpfProbeClient probe, IToolAuthorization auth, CancellationToken cancellationToken)
        => Request("wpf_probe_datacontext", processId, new ProbeRequest(string.Empty, "datacontext", AutomationId: automationId, Name: name), probe, auth, cancellationToken);

    [McpServerTool(Name = "wpf_probe_binding", UseStructuredContent = true), Description("Returns metadata/status for an allowlisted WPF dependency-property binding without exposing arbitrary object graphs.")]
    public static Task<ToolResult<ProbeResponse>> Binding(int processId, string property, string? automationId, string? name, IWpfProbeClient probe, IToolAuthorization auth, CancellationToken cancellationToken)
        => Request("wpf_probe_binding", processId, new ProbeRequest(string.Empty, "binding", AutomationId: automationId, Name: name, Property: property), probe, auth, cancellationToken);

    [McpServerTool(Name = "wpf_probe_binding_errors", UseStructuredContent = true), Description("Enumerates bounded WPF binding errors from the selected visual subtree.")]
    public static Task<ToolResult<ProbeResponse>> BindingErrors(int processId, string? automationId, string? name, IWpfProbeClient probe, IToolAuthorization auth, CancellationToken cancellationToken)
        => Request("wpf_probe_binding_errors", processId, new ProbeRequest(string.Empty, "binding_errors", AutomationId: automationId, Name: name), probe, auth, cancellationToken);

    [McpServerTool(Name = "wpf_probe_command", UseStructuredContent = true), Description("Returns command type/attachment evidence. It deliberately does not invoke arbitrary commands or CanExecute methods.")]
    public static Task<ToolResult<ProbeResponse>> Command(int processId, string? automationId, string? name, IWpfProbeClient probe, IToolAuthorization auth, CancellationToken cancellationToken)
        => Request("wpf_probe_command", processId, new ProbeRequest(string.Empty, "command", AutomationId: automationId, Name: name), probe, auth, cancellationToken);

    [McpServerTool(Name = "wpf_probe_validation", UseStructuredContent = true), Description("Returns sanitized WPF Validation errors for the selected element.")]
    public static Task<ToolResult<ProbeResponse>> Validation(int processId, string? automationId, string? name, IWpfProbeClient probe, IToolAuthorization auth, CancellationToken cancellationToken)
        => Request("wpf_probe_validation", processId, new ProbeRequest(string.Empty, "validation", AutomationId: automationId, Name: name), probe, auth, cancellationToken);

    [McpServerTool(Name = "wpf_probe_resource", UseStructuredContent = true), Description("Looks up a WPF resource key and returns a safe scalar/brush/type representation, not arbitrary object serialization.")]
    public static Task<ToolResult<ProbeResponse>> Resource(int processId, string resourceKey, string? automationId, string? name, IWpfProbeClient probe, IToolAuthorization auth, CancellationToken cancellationToken)
        => Request("wpf_probe_resource", processId, new ProbeRequest(string.Empty, "resource", AutomationId: automationId, Name: name, ResourceKey: resourceKey), probe, auth, cancellationToken);

    [McpServerTool(Name = "wpf_probe_property", UseStructuredContent = true), Description("Reads only an explicitly allowlisted WPF property. PasswordBox and arbitrary reflection are prohibited.")]
    public static Task<ToolResult<ProbeResponse>> Property(int processId, string property, string? automationId, string? name, IWpfProbeClient probe, IToolAuthorization auth, CancellationToken cancellationToken)
        => Request("wpf_probe_property", processId, new ProbeRequest(string.Empty, "property", AutomationId: automationId, Name: name, Property: property), probe, auth, cancellationToken);

    [McpServerTool(Name = "wpf_probe_dispatcher", UseStructuredContent = true), Description("Returns bounded WPF dispatcher health/state evidence.")]
    public static Task<ToolResult<ProbeResponse>> Dispatcher(int processId, IWpfProbeClient probe, IToolAuthorization auth, CancellationToken cancellationToken)
        => Request("wpf_probe_dispatcher", processId, new ProbeRequest(string.Empty, "dispatcher"), probe, auth, cancellationToken);

    [McpServerTool(Name = "wpf_probe_exceptions", UseStructuredContent = true), Description("Returns the bounded, redacted recent WPF dispatcher/domain/task exception feed captured by the explicitly installed probe.")]
    public static Task<ToolResult<ProbeResponse>> Exceptions(int processId, IWpfProbeClient probe, IToolAuthorization auth, CancellationToken cancellationToken)
        => Request("wpf_probe_exceptions", processId, new ProbeRequest(string.Empty, "exceptions"), probe, auth, cancellationToken);

    private static async Task<ToolResult<ProbeResponse>> Request(string tool, int processId, ProbeRequest request, IWpfProbeClient probe, IToolAuthorization auth, CancellationToken cancellationToken)
    {
        var policy = new ToolPolicy(tool, PermissionLevel.ApplicationDiagnostics, RiskClass.Read, "wpf.probe");
        var allowed = auth.Authorize(policy, processId.ToString());
        if (!allowed.Success) return ToolResult<ProbeResponse>.Fail(allowed.Error!.Code, allowed.Error.Message);
        var result = await probe.RequestAsync(processId, request, cancellationToken).ConfigureAwait(false);
        auth.Complete(allowed.Value!, policy, processId.ToString(), result.Success, result.Success ? "OK" : result.Error?.Code ?? "FAILED");
        return result;
    }
}

[McpServerToolType]
public static class WpfUiTools
{
    [McpServerTool(Name = "wpfui_resource", UseStructuredContent = true), Description("Returns observed WPF resource evidence through the in-process probe. It does not infer a resource origin that was not observed.")]
    public static Task<ToolResult<object>> Resource(int processId, string automationId, string resourceKey, IWpfUiInspectionService service, IToolAuthorization auth, CancellationToken cancellationToken)
        => Run("wpfui_resource", processId, () => service.GetResourceAsync(processId, automationId, resourceKey, cancellationToken), auth);

    [McpServerTool(Name = "wpfui_property", UseStructuredContent = true), Description("Returns an allowlisted effective WPF property through the probe for WPF-UI/design-system inspection.")]
    public static Task<ToolResult<object>> Property(int processId, string automationId, string property, IWpfUiInspectionService service, IToolAuthorization auth, CancellationToken cancellationToken)
        => Run("wpfui_property", processId, () => service.GetPropertyAsync(processId, automationId, property, cancellationToken), auth);

    [McpServerTool(Name = "wpfui_theme_evidence", UseStructuredContent = true), Description("Collects known theme/resource evidence without guessing a theme when evidence is absent.")]
    public static Task<ToolResult<object>> ThemeEvidence(int processId, IWpfUiInspectionService service, IToolAuthorization auth, CancellationToken cancellationToken)
        => Run("wpfui_theme_evidence", processId, () => service.GetThemeEvidenceAsync(processId, cancellationToken), auth);

    private static async Task<ToolResult<object>> Run(string tool, int processId, Func<Task<ToolResult<object>>> action, IToolAuthorization auth)
    {
        var policy = new ToolPolicy(tool, PermissionLevel.ApplicationDiagnostics, RiskClass.Read, "wpfui.resources");
        var allowed = auth.Authorize(policy, processId.ToString());
        if (!allowed.Success) return ToolResult<object>.Fail(allowed.Error!.Code, allowed.Error.Message);
        var result = await action().ConfigureAwait(false);
        auth.Complete(allowed.Value!, policy, processId.ToString(), result.Success, result.Success ? "OK" : result.Error?.Code ?? "FAILED");
        return result;
    }
}
