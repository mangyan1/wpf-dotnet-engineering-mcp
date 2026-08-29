using System.ComponentModel;
using EngineeringMcp.Contracts;
using EngineeringMcp.Security;
using EngineeringMcp.Wpf;
using ModelContextProtocol.Server;

namespace EngineeringMcp.Host;

[McpServerToolType]
public static class ProbeTools
{
    [McpServerTool(Name = "wpf_probe", UseStructuredContent = true), Description("Authorized in-process WPF probe; one bounded operation per call. Operations: status (probe health), visual_tree, logical_tree, datacontext (TYPE evidence only, never ViewModel values), binding (needs property), binding_errors, command (never invokes commands or CanExecute), validation, validation_summary (counts/rule types only), resource (needs resourceKey), property (needs property; allowlisted names only, PasswordBox refused), dispatcher, exceptions. The probe is explicitly installed; it is never injected by this MCP server.")]
    public static Task<ToolResult<ProbeResponse>> Probe(
        [Description("Operating-system process identifier of an allowlisted target process.")] int processId,
        [Description("Probe operation: status, visual_tree, logical_tree, datacontext, binding, binding_errors, command, validation, validation_summary, resource, property, dispatcher, or exceptions.")] string operation,
        WpfProbeClient probe,
        ToolAuthorization auth,
        CancellationToken cancellationToken,
        [Description("AutomationId of the target element; narrows tree, binding, and validation operations.")] string? automationId = null,
        [Description("Element name of the target element; narrows tree, binding, and validation operations.")] string? name = null,
        [Description("Allowlisted WPF dependency-property name; required by the binding and property operations.")] string? property = null,
        [Description("Exact WPF resource key; required by the resource operation.")] string? resourceKey = null)
    {
        if (!TryMapOperation(operation, out var op, out var error))
            return Task.FromResult(ToolResult<ProbeResponse>.Fail("PROBE_OPERATION_UNKNOWN", error!));
        if ((string.Equals(op, "binding", StringComparison.Ordinal) || string.Equals(op, "property", StringComparison.Ordinal)) && string.IsNullOrWhiteSpace(property))
            return Task.FromResult(ToolResult<ProbeResponse>.Fail("PROBE_PROPERTY_REQUIRED", $"The '{op}' operation requires the 'property' parameter."));
        if (string.Equals(op, "resource", StringComparison.Ordinal) && string.IsNullOrWhiteSpace(resourceKey))
            return Task.FromResult(ToolResult<ProbeResponse>.Fail("PROBE_RESOURCE_KEY_REQUIRED", "The 'resource' operation requires the 'resourceKey' parameter."));

        return ToolRun.Async(auth, ToolPolicyCatalog.Get("wpf_probe").ToPolicy(), processId.ToString(),
            () => probe.RequestAsync(processId, new ProbeRequest(string.Empty, op, AutomationId: automationId, Name: name, Property: property, ResourceKey: resourceKey), cancellationToken));
    }

    private static bool TryMapOperation(string operation, out string op, out string? error)
    {
        op = operation.Trim().ToLowerInvariant().Replace('-', '_');
        error = null;
        var known = op is "status" or "visual_tree" or "logical_tree" or "datacontext" or "binding" or "binding_errors"
            or "command" or "validation" or "validation_summary" or "resource" or "property" or "dispatcher" or "exceptions";
        if (known) return true;
        error = "Unknown probe operation. Allowed: status, visual_tree, logical_tree, datacontext, binding, binding_errors, command, validation, validation_summary, resource, property, dispatcher, exceptions.";
        return false;
    }
}

[McpServerToolType]
public static class WpfUiTools
{
    [McpServerTool(Name = "wpfui_inspect", UseStructuredContent = true), Description("WPF-UI/design-system inspection through the authorized in-process probe. Operations: resource (needs automationId and resourceKey), property (needs automationId and property), theme_evidence (no selector). Evidence is observed only; missing evidence is never guessed.")]
    public static Task<ToolResult<object>> Inspect(
        [Description("Operating-system process identifier of an allowlisted target process.")] int processId,
        [Description("Inspection operation: resource, property, or theme_evidence.")] string operation,
        WpfUiInspectionService service,
        ToolAuthorization auth,
        CancellationToken cancellationToken,
        [Description("AutomationId of the target element; required by the resource and property operations.")] string? automationId = null,
        [Description("Allowlisted WPF-UI effective property name; required by the property operation.")] string? property = null,
        [Description("Exact WPF resource key; required by the resource operation.")] string? resourceKey = null)
    {
        return ToolRun.Async(auth, ToolPolicyCatalog.Get("wpfui_inspect").ToPolicy(), processId.ToString(),
            () => operation.Trim().ToLowerInvariant().Replace('-', '_') switch
            {
                "resource" when !string.IsNullOrWhiteSpace(automationId) && !string.IsNullOrWhiteSpace(resourceKey)
                    => service.GetResourceAsync(processId, automationId, resourceKey, cancellationToken),
                "property" when !string.IsNullOrWhiteSpace(automationId) && !string.IsNullOrWhiteSpace(property)
                    => service.GetPropertyAsync(processId, automationId, property, cancellationToken),
                "theme_evidence" => service.GetThemeEvidenceAsync(processId, cancellationToken),
                "resource" or "property" => Task.FromResult(ToolResult<object>.Fail("WPFUI_ARGUMENT_REQUIRED", "The resource and property operations require both 'automationId' and the referenced key/property.")),
                _ => Task.FromResult(ToolResult<object>.Fail("WPFUI_OPERATION_UNKNOWN", "Unknown inspection operation. Allowed: resource, property, theme_evidence."))
            });
    }
}
