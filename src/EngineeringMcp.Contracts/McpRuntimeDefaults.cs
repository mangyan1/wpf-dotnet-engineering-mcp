namespace EngineeringMcp.Contracts;

/// <summary>
/// Single source of truth for local MCP runtime endpoints and server identity.
/// Keep client integrations aligned with these values instead of duplicating ports/routes.
/// </summary>
public static class McpRuntimeDefaults
{
    public const string ServerName = "dotnetWpfEngineering";
    public const string HttpTokenEnvironmentVariable = "ENGINEERING_MCP_HTTP_TOKEN";
    public const string RepositoryRootEnvironmentVariable = "ENGINEERING_MCP_REPOSITORY_ROOT";
    public const string ArtifactsPathEnvironmentVariable = "ENGINEERING_MCP_ARTIFACTS_PATH";
    public const string ListenUrl = "http://127.0.0.1:8765";
    public const string McpPath = "/mcp";
    public const string HealthPath = "/healthz";
    public const string ClientNameHeader = "X-Engineering-Mcp-Client";
    public const string VsCodeClientQueryFlag = "vscode";
    public const string VsCodeClientName = "vscode";
    public const string McpEndpoint = ListenUrl + McpPath;
    public const string VsCodeMcpEndpoint = McpEndpoint + "?" + VsCodeClientQueryFlag;
    public const string HealthEndpoint = ListenUrl + HealthPath;

    public static string WithVsCodeClientMarker(string endpoint)
        => endpoint + (endpoint.Contains('?') ? "&" : "?") + VsCodeClientQueryFlag;
}
