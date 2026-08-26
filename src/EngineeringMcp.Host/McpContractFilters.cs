using System.Text.Json;
using System.Text.Json.Nodes;
using EngineeringMcp.Contracts;
using EngineeringMcp.Security;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace EngineeringMcp.Host;

internal static class McpContractFilters
{
    public static void AddEngineeringContractFilters(this IMcpServerBuilder builder)
        => builder.WithRequestFilters(filters =>
        {
            filters.AddListToolsFilter(next => async (context, cancellationToken) =>
            {
                var result = await next(context, cancellationToken);
                var services = context.Services ?? throw new InvalidOperationException("MCP request services are unavailable.");
                var policy = services.GetRequiredService<IPolicyProvider>().Current;

                for (var index = result.Tools.Count - 1; index >= 0; index--)
                {
                    var tool = result.Tools[index];
                    if (!ToolContractCatalog.IsEnabled(tool.Name, policy))
                    {
                        result.Tools.RemoveAt(index);
                        continue;
                    }

                    tool.Title ??= ToolContractCatalog.Title(tool.Name);
                    tool.Annotations = ToolContractCatalog.Annotations(tool.Name);
                    tool.InputSchema = ToolContractCatalog.DescribeInputSchema(tool.InputSchema);
                }

                return result;
            });

            filters.AddCallToolFilter(next => async (context, cancellationToken) =>
            {
                var services = context.Services ?? throw new InvalidOperationException("MCP request services are unavailable.");
                var policy = services.GetRequiredService<IPolicyProvider>().Current;
                if (!ToolContractCatalog.IsEnabled(context.Params.Name, policy))
                    throw new McpException("This tool is disabled by the active capability profile or tool policy.");

                IAsyncDisposable? processLease = null;
                if (context.Params.Arguments?.TryGetValue("processId", out var processIdValue) == true &&
                    processIdValue.ValueKind == JsonValueKind.Number && processIdValue.TryGetInt32(out var processId))
                {
                    processLease = await services.GetRequiredService<IProcessOperationCoordinator>()
                        .EnterAsync(processId, cancellationToken);
                }

                CallToolResult result;
                try
                {
                    result = await next(context, cancellationToken);
                }
                finally
                {
                    if (processLease is not null) await processLease.DisposeAsync();
                }
                if (result.StructuredContent is JsonElement structured &&
                    structured.ValueKind == JsonValueKind.Object &&
                    structured.TryGetProperty("success", out var success) &&
                    success.ValueKind == JsonValueKind.False)
                {
                    result.IsError = true;
                }

                return result;
            });
        });
}

internal static class ToolContractCatalog
{
    private static readonly HashSet<string> MutatingTools = new(StringComparer.Ordinal)
    {
        "wpf_attach", "wpf_detach", "wpf_click", "wpf_type", "wpf_select", "wpf_toggle",
        "wpf_expand", "wpf_collapse", "wpf_scroll", "wpf_focus", "dotnet_trace_start",
        "dotnet_trace_stop", "dotnet_capture_dump", "diagnose_click"
    };

    private static readonly HashSet<string> IdempotentMutations = new(StringComparer.Ordinal)
    {
        "wpf_attach", "wpf_detach", "wpf_expand", "wpf_collapse", "wpf_scroll", "wpf_focus",
        "dotnet_trace_stop"
    };

    private static readonly Dictionary<string, string> ParameterDescriptions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["processId"] = "Operating-system process identifier of an allowlisted target process.",
        ["automationId"] = "Exact WPF AutomationId. Prefer this stable semantic selector over visible text.",
        ["name"] = "Exact accessible element name when AutomationId is unavailable.",
        ["controlType"] = "WPF UI Automation control type, such as Button, TextBox, or Window.",
        ["reference"] = "Opaque UI element reference returned by an earlier MCP UI query.",
        ["maxElements"] = "Maximum number of UI elements to return; the server applies a hard upper bound.",
        ["maxDepth"] = "Maximum traversal depth; the server applies a hard upper bound.",
        ["timeoutMs"] = "Bounded timeout in milliseconds before the operation is cancelled.",
        ["requireEnabled"] = "When true, wait for or assert that the selected element is enabled.",
        ["requireVisible"] = "When true, wait for or assert that the selected element is visible.",
        ["expectedValue"] = "Expected sanitized value used by the requested assertion.",
        ["value"] = "Non-sensitive value to enter or compare. Credentials and secret-looking values are rejected.",
        ["itemName"] = "Exact accessible name of the item to select.",
        ["durationMs"] = "Bounded observation duration in milliseconds.",
        ["traceId"] = "Opaque trace identifier previously returned by this MCP session.",
        ["dumpId"] = "Opaque dump identifier previously returned by this MCP session.",
        ["root"] = "Path beneath a source root explicitly allowed by policy.",
        ["path"] = "File path beneath a source root explicitly allowed by policy.",
        ["pattern"] = "Bounded file-search pattern applied only within an approved source root.",
        ["startLine"] = "One-based first source line to return.",
        ["lineCount"] = "Maximum number of source lines to return.",
        ["symbol"] = "Exact C# identifier to locate in approved source.",
        ["bindingPath"] = "Exact WPF Binding Path to locate in approved XAML.",
        ["stackTrace"] = "Redacted stack trace containing source file and line evidence to map.",
        ["backendName"] = "Configured local ASP.NET diagnostic adapter name.",
        ["limit"] = "Maximum number of observations to return; the server applies a hard upper bound.",
        ["sinceUtc"] = "Optional UTC lower bound for returned observations.",
        ["windowMs"] = "Bounded correlation window in milliseconds.",
        ["propertyName"] = "Explicitly allowlisted WPF dependency property or safe property name.",
        ["resourceKey"] = "Exact WPF resource key to inspect through the authorized probe.",
        ["targetAutomationId"] = "Exact AutomationId of the target element within the attached WPF process.",
        ["includeChildren"] = "Whether the bounded inspection may include descendants of the selected element.",
        ["forceGc"] = "Whether to request a target GC before collecting the diagnostic observation; requires policy approval.",
        ["typeName"] = "Exact or bounded partial managed type name used for dump analysis.",
        ["maxFrames"] = "Maximum number of stack frames returned per bounded diagnostic result.",
        ["maxTypes"] = "Maximum number of managed types returned by bounded dump analysis.",
        ["offset"] = "Zero-based result offset for deterministic bounded pagination.",
        ["pageSize"] = "Number of results requested for one page; the server applies a hard upper bound."
    };

    public static bool IsEnabled(string name, McpPolicy policy)
    {
        if (policy.DisabledTools?.Contains(name, StringComparer.Ordinal) == true)
            return false;
        if (policy.EnabledTools is { Count: > 0 } && !policy.EnabledTools.Contains(name, StringComparer.Ordinal))
            return false;
        if (policy.EnabledToolProfiles is not { Count: > 0 })
            return true;

        var profile = Profile(name);
        return policy.EnabledToolProfiles.Contains(profile, StringComparer.OrdinalIgnoreCase);
    }

    public static string Title(string name)
        => string.Join(' ', name.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => char.ToUpperInvariant(part[0]) + part[1..]));

    public static ToolAnnotations Annotations(string name)
    {
        var readOnly = !MutatingTools.Contains(name);
        return new ToolAnnotations
        {
            Title = Title(name),
            ReadOnlyHint = readOnly,
            DestructiveHint = string.Equals(name, "wpf_click", StringComparison.Ordinal),
            IdempotentHint = readOnly || IdempotentMutations.Contains(name),
            OpenWorldHint = false
        };
    }

    public static JsonElement DescribeInputSchema(JsonElement schema)
    {
        var root = JsonNode.Parse(schema.GetRawText())?.AsObject()
            ?? throw new InvalidDataException("Generated tool input schema is not a JSON object.");
        if (root["properties"] is not JsonObject properties)
            return schema;

        foreach (var property in properties)
        {
            if (property.Value is not JsonObject definition || definition.ContainsKey("description"))
                continue;
            definition["description"] = ParameterDescriptions.TryGetValue(property.Key, out var description)
                ? description
                : $"Value for the '{property.Key}' parameter. Follow the tool description and schema constraints.";
        }

        return JsonSerializer.SerializeToElement(root);
    }

    private static string Profile(string name)
    {
        if (name.StartsWith("system_", StringComparison.Ordinal)) return "core";
        if (name.StartsWith("source_", StringComparison.Ordinal) || name == "wpfui_audit_resources") return "source";
        if (name.StartsWith("dotnet_", StringComparison.Ordinal) || name.StartsWith("aspnet_", StringComparison.Ordinal) || name.StartsWith("diagnose_", StringComparison.Ordinal)) return "diagnostics";
        if (MutatingTools.Contains(name)) return "wpf-interact";
        return "wpf-read";
    }
}
