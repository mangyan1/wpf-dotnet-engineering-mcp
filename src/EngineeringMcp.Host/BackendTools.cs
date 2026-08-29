using System.ComponentModel;
using EngineeringMcp.Diagnostics;
using EngineeringMcp.Contracts;
using EngineeringMcp.Security;
using ModelContextProtocol.Server;

namespace EngineeringMcp.Host;

[McpServerToolType]
public static class BackendTools
{
    [McpServerTool(Name = "aspnet_health", UseStructuredContent = true), Description("Reads health of an explicitly installed, locally authenticated ASP.NET diagnostic adapter. No raw request bodies, headers, cookies or query strings are exposed.")]
    public static Task<ToolResult<BackendProbeResponse>> Health([Description("Operating-system process identifier of an allowlisted ASP.NET target process.")] int processId, BackendProbeClient backend, ToolAuthorization auth, CancellationToken cancellationToken)
        => Request("aspnet_health", processId, "health", 1, backend, auth, cancellationToken);

    [McpServerTool(Name = "aspnet_requests", UseStructuredContent = true), Description("Returns a bounded sanitized buffer of ASP.NET request observations: method, route template, status, duration and trace id only.")]
    public static Task<ToolResult<BackendProbeResponse>> Requests(
        [Description("Operating-system process identifier of an allowlisted ASP.NET target process.")] int processId,
        BackendProbeClient backend,
        ToolAuthorization auth,
        CancellationToken cancellationToken,
        [Description("Maximum number of observations to return; the server applies a hard upper bound.")] int limit = 100)
        => Request("aspnet_requests", processId, "recent", limit, backend, auth, cancellationToken);

    [McpServerTool(Name = "aspnet_exceptions", UseStructuredContent = true), Description("Returns bounded sanitized backend request observations containing exceptions. Bodies, auth headers, cookies and raw URLs are never captured by the adapter.")]
    public static Task<ToolResult<BackendProbeResponse>> Exceptions(
        [Description("Operating-system process identifier of an allowlisted ASP.NET target process.")] int processId,
        BackendProbeClient backend,
        ToolAuthorization auth,
        CancellationToken cancellationToken,
        [Description("Maximum number of observations to return; the server applies a hard upper bound.")] int limit = 100)
        => Request("aspnet_exceptions", processId, "exceptions", limit, backend, auth, cancellationToken);

    private static Task<ToolResult<BackendProbeResponse>> Request(string tool, int processId, string op, int limit, BackendProbeClient backend, ToolAuthorization auth, CancellationToken cancellationToken)
        => ToolRun.Async(auth, ToolPolicyCatalog.Get(tool).ToPolicy(), processId.ToString(),
            () => backend.RequestAsync(processId, op, limit, cancellationToken));
}
