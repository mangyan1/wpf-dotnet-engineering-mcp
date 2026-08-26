using System.ComponentModel;
using EngineeringMcp.AspNetCore;
using EngineeringMcp.Contracts;
using EngineeringMcp.Security;
using ModelContextProtocol.Server;

namespace EngineeringMcp.Host;

[McpServerToolType]
public static class BackendTools
{
    [McpServerTool(Name = "aspnet_health", UseStructuredContent = true), Description("Reads health of an explicitly installed, locally authenticated ASP.NET diagnostic adapter. No raw request bodies, headers, cookies or query strings are exposed.")]
    public static Task<ToolResult<BackendProbeResponse>> Health(int processId, IBackendProbeClient backend, IToolAuthorization auth, CancellationToken cancellationToken)
        => Run("aspnet_health", processId, "health", 1, backend, auth, cancellationToken);

    [McpServerTool(Name = "aspnet_requests", UseStructuredContent = true), Description("Returns a bounded sanitized buffer of ASP.NET request observations: method, route template, status, duration and trace id only.")]
    public static Task<ToolResult<BackendProbeResponse>> Requests(int processId, int limit, IBackendProbeClient backend, IToolAuthorization auth, CancellationToken cancellationToken)
        => Run("aspnet_requests", processId, "recent", limit, backend, auth, cancellationToken);

    [McpServerTool(Name = "aspnet_exceptions", UseStructuredContent = true), Description("Returns bounded sanitized backend request observations containing exceptions. Bodies, auth headers, cookies and raw URLs are never captured by the adapter.")]
    public static Task<ToolResult<BackendProbeResponse>> Exceptions(int processId, int limit, IBackendProbeClient backend, IToolAuthorization auth, CancellationToken cancellationToken)
        => Run("aspnet_exceptions", processId, "exceptions", limit, backend, auth, cancellationToken);

    private static async Task<ToolResult<BackendProbeResponse>> Run(string tool, int processId, string op, int limit, IBackendProbeClient backend, IToolAuthorization auth, CancellationToken cancellationToken)
    {
        var policy = new ToolPolicy(tool, PermissionLevel.ApplicationDiagnostics, RiskClass.Read, "aspnet.telemetry");
        var allowed = auth.Authorize(policy, processId.ToString());
        if (!allowed.Success) return ToolResult<BackendProbeResponse>.Fail(allowed.Error!.Code, allowed.Error.Message);
        var result = await backend.RequestAsync(processId, op, limit, cancellationToken).ConfigureAwait(false);
        auth.Complete(allowed.Value!, policy, processId.ToString(), result.Success, result.Success ? "OK" : result.Error?.Code ?? "FAILED");
        return result;
    }
}
