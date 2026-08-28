using System.ComponentModel;
using EngineeringMcp.Security;
using EngineeringMcp.Contracts;
using ModelContextProtocol.Server;

namespace EngineeringMcp.Host;

[McpServerToolType]
public static class SystemTools
{
    [McpServerTool(Name = "system_version", UseStructuredContent = true), Description("Returns MCP server version and runtime metadata. Does not inspect a target application.")]
    public static object Version() => new
    {
        server = "DotNetEngineeringMcp",
        version = typeof(SystemTools).Assembly.GetName().Version?.ToString() ?? "0.1.0-dev",
        runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
        os = System.Runtime.InteropServices.RuntimeInformation.OSDescription
    };

    [McpServerTool(Name = "system_health", UseStructuredContent = true), Description("Returns local MCP host readiness only. It does not imply that any target WPF or backend application is healthy.")]
    public static object Health() => new { status = "ready", scope = "mcp-host-only" };

    [McpServerTool(Name = "system_capabilities", UseStructuredContent = true), Description("Returns the authoritative capability manifest. Capabilities marked false must be treated as unavailable.")]
    public static CapabilityManifest Capabilities(CapabilityRegistry registry) => registry.GetManifest();

    [McpServerTool(Name = "system_permissions", UseStructuredContent = true), Description("Returns configured permission ceiling and policy source. It never returns policy secrets or tokens. The server is stateless: audit identifiers are per server process, not per MCP session.")]
    public static object Permissions(FilePolicyProvider policy) => new
    {
        permissionCeiling = policy.Current.PermissionCeiling.ToString(),
        policySource = policy.Source == "locked-down-default" ? policy.Source : "configured-file",
        allowDestructiveActions = policy.Current.AllowDestructiveActions,
        allowPrivilegedDiagnostics = policy.Current.AllowPrivilegedDiagnostics,
        piiMode = policy.Current.Pii.ToString(),
        mode = "default-deny"
    };

    [McpServerTool(Name = "system_policy_diagnostics", UseStructuredContent = true), Description("Explains effective policy restrictions and safe remediation steps without returning policy paths, process paths, source roots, secrets, or tokens.")]
    public static PolicyDiagnosticReport PolicyDiagnosticReport(FilePolicyProvider policy)
        => PolicyDiagnostics.Analyze(policy.Current, policy.Source);
}
