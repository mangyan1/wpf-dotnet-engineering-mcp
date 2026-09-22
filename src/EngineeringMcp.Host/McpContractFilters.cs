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
                var policy = services.GetRequiredService<FilePolicyProvider>().Current;

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
                }

                return result;
            });

            filters.AddCallToolFilter(next => async (context, cancellationToken) =>
            {
                var services = context.Services ?? throw new InvalidOperationException("MCP request services are unavailable.");
                var policy = services.GetRequiredService<FilePolicyProvider>().Current;
                if (!ToolContractCatalog.IsEnabled(context.Params.Name, policy))
                    throw new McpException("This tool is disabled by the active capability profile or tool policy.");

                IAsyncDisposable? processLease = null;
                if (context.Params.Arguments?.TryGetValue("processId", out var processIdValue) == true &&
                    processIdValue.ValueKind == JsonValueKind.Number && processIdValue.TryGetInt32(out var processId))
                {
                    processLease = await services.GetRequiredService<ProcessOperationCoordinator>()
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

                // isError derivation and defense-in-depth redaction both live in
                // RedactOutput below; the structured content is parsed to JsonNode once there and
                // re-serialized only when redaction changed a string.
                RedactOutput(result, services.GetRequiredService<RedactionService>(), policy.Pii);

                return result;
            });
        });

    private static void RedactOutput(CallToolResult result, RedactionService redaction, PiiMode pii)
    {
        foreach (var block in result.Content)
        {
            if (block is TextContentBlock text && !string.IsNullOrEmpty(text.Text))
                text.Text = redaction.Redact(text.Text, pii);
        }

        if (result.StructuredContent is not JsonElement structured)
            return;
        var node = JsonNode.Parse(structured.GetRawText());
        if (node is null)
            return;

        // isError is derived from the serialized structured result because the SDK
        // owns the ToolResult->CallToolResult mapping; McpHttpIntegrationTests guards this key.
        if (node is JsonObject obj && obj["success"] is JsonValue success && success.TryGetValue<bool>(out var ok) && !ok)
            result.IsError = true;

        var changed = false;
        RedactNode(node, redaction, pii, 0, ref changed);
        if (changed)
            result.StructuredContent = JsonSerializer.SerializeToElement(node);
    }

    private static JsonNode? RedactNode(JsonNode? node, RedactionService redaction, PiiMode pii, int depth, ref bool changed)
    {
        if (node is null || depth > 32) return node;
        switch (node)
        {
            case JsonObject obj:
            {
                // Clear-then-rebuild: reassigning a node still parented to obj throws
                // "The node already has a parent", so detach children before walking them.
                var entries = obj.ToArray();
                obj.Clear();
                foreach (var (key, value) in entries)
                    obj[key] = RedactNode(value, redaction, pii, depth + 1, ref changed);
                return obj;
            }
            case JsonArray array:
            {
                var items = array.ToArray();
                array.Clear();
                foreach (var item in items)
                    array.Add(RedactNode(item, redaction, pii, depth + 1, ref changed));
                return array;
            }
            case JsonValue value when value.TryGetValue<string>(out var text):
                var redacted = redaction.Redact(text, pii);
                if (!string.Equals(redacted, text, StringComparison.Ordinal)) changed = true;
                return JsonValue.Create(redacted);
            default:
                return node;
        }
    }
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

    public static bool IsEnabled(string name, McpPolicy policy)
        => ToolPolicyCatalog.Publication(name, policy).Published;

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
}
