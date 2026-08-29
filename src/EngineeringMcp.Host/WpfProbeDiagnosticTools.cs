using System.ComponentModel;
using EngineeringMcp.Contracts;
using EngineeringMcp.Security;
using EngineeringMcp.Wpf;
using ModelContextProtocol.Server;

namespace EngineeringMcp.Host;

[McpServerToolType]
public static class WpfProbeDiagnosticTools
{
    [McpServerTool(Name = "wpf_binding_info", UseStructuredContent = true), Description("Returns bounded binding configuration metadata for one allowlisted dependency property. Bound values and ViewModel values are never read.")]
    public static Task<ToolResult<ProbeResponse>> BindingInfo(
        [Description("Operating-system process identifier of an allowlisted target process with the explicit WPF probe installed.")] int processId,
        [Description("Allowlisted WPF dependency-property name, such as Text, IsEnabled, Visibility, or SelectedItem.")] string property,
        WpfProbeClient probe, ToolAuthorization auth, CancellationToken cancellationToken,
        [Description("AutomationId used locally to select the target element.")] string? automationId = null,
        [Description("WPF x:Name used locally when AutomationId is unavailable.")] string? name = null)
        => string.IsNullOrWhiteSpace(property)
            ? Task.FromResult(ToolResult<ProbeResponse>.Fail("PROBE_PROPERTY_REQUIRED", "Binding metadata requires an allowlisted dependency-property name."))
            : Run("wpf_binding_info", "binding", processId, probe, auth, cancellationToken, automationId, name, property);

    [McpServerTool(Name = "wpf_binding_errors", UseStructuredContent = true), Description("Returns bounded binding-error metadata: element code identity, dependency property, binding path, and status. Runtime bound values are never returned.")]
    public static Task<ToolResult<ProbeResponse>> BindingErrors(
        [Description("Operating-system process identifier of an allowlisted target process with the explicit WPF probe installed.")] int processId,
        WpfProbeClient probe, ToolAuthorization auth, CancellationToken cancellationToken,
        [Description("AutomationId used locally to narrow the inspected visual subtree.")] string? automationId = null,
        [Description("WPF x:Name used locally when AutomationId is unavailable.")] string? name = null)
        => Run("wpf_binding_errors", "binding_errors", processId, probe, auth, cancellationToken, automationId, name);

    [McpServerTool(Name = "wpf_command_state", UseStructuredContent = true), Description("Returns command type/presence and observed element enabled state. The probe never invokes ICommand.CanExecute or Execute.")]
    public static Task<ToolResult<ProbeResponse>> CommandState(
        [Description("Operating-system process identifier of an allowlisted target process with the explicit WPF probe installed.")] int processId,
        WpfProbeClient probe, ToolAuthorization auth, CancellationToken cancellationToken,
        [Description("AutomationId used locally to select the command source.")] string? automationId = null,
        [Description("WPF x:Name used locally when AutomationId is unavailable.")] string? name = null)
        => Run("wpf_command_state", "command", processId, probe, auth, cancellationToken, automationId, name);

    [McpServerTool(Name = "wpf_validation_summary", UseStructuredContent = true), Description("Returns validation error counts and rule type names only. Validation messages, entered values, and bound business objects are omitted.")]
    public static Task<ToolResult<ProbeResponse>> ValidationSummary(
        [Description("Operating-system process identifier of an allowlisted target process with the explicit WPF probe installed.")] int processId,
        WpfProbeClient probe, ToolAuthorization auth, CancellationToken cancellationToken,
        [Description("AutomationId used locally to narrow the inspected visual subtree.")] string? automationId = null,
        [Description("WPF x:Name used locally when AutomationId is unavailable.")] string? name = null)
        => Run("wpf_validation_summary", "validation_summary", processId, probe, auth, cancellationToken, automationId, name);

    [McpServerTool(Name = "wpf_datacontext_type", UseStructuredContent = true), Description("Returns only the DataContext CLR type name and null state. No ViewModel properties or values are read.")]
    public static Task<ToolResult<ProbeResponse>> DataContextType(
        [Description("Operating-system process identifier of an allowlisted target process with the explicit WPF probe installed.")] int processId,
        WpfProbeClient probe, ToolAuthorization auth, CancellationToken cancellationToken,
        [Description("AutomationId used locally to select the target element.")] string? automationId = null,
        [Description("WPF x:Name used locally when AutomationId is unavailable.")] string? name = null)
        => Run("wpf_datacontext_type", "datacontext", processId, probe, auth, cancellationToken, automationId, name);

    [McpServerTool(Name = "wpf_dispatcher_status", UseStructuredContent = true), Description("Returns WPF dispatcher thread/access/shutdown metadata only; queued delegates and application data are not inspected.")]
    public static Task<ToolResult<ProbeResponse>> DispatcherStatus(
        [Description("Operating-system process identifier of an allowlisted target process with the explicit WPF probe installed.")] int processId,
        WpfProbeClient probe, ToolAuthorization auth, CancellationToken cancellationToken)
        => Run("wpf_dispatcher_status", "dispatcher", processId, probe, auth, cancellationToken);

    private static Task<ToolResult<ProbeResponse>> Run(
        string tool,
        string operation,
        int processId,
        WpfProbeClient probe,
        ToolAuthorization auth,
        CancellationToken cancellationToken,
        string? automationId = null,
        string? name = null,
        string? property = null)
        => ToolRun.Async(
            auth,
            ToolPolicyCatalog.Get(tool).ToPolicy(),
            processId.ToString(),
            () => probe.RequestAsync(processId,
                new ProbeRequest(string.Empty, operation, AutomationId: automationId, Name: name, Property: property),
                cancellationToken));
}
