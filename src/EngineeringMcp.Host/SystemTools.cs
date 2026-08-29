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

    [McpServerTool(Name = "system_tool_preflight", UseStructuredContent = true), Description("Authoritatively reports whether one exact tool is published and allowed by the active policy and runtime capability registry. Agents must call this before claiming that a tool is policy-disabled; target, selector, and input validation still occur only when the tool is invoked.")]
    public static ToolPreflightReport ToolPreflight(
        [Description("Exact lowercase_with_underscores MCP tool name from tools/list, for example wpf_click.")] string toolName,
        FilePolicyProvider policy,
        PolicyEngine engine,
        CapabilityRegistry capabilities)
    {
        toolName = toolName?.Trim() ?? string.Empty;
        if (!ToolPolicyCatalog.TryGet(toolName, out var definition))
        {
            return new(
                toolName,
                Known: false,
                Published: false,
                AllowedByPolicy: false,
                Status: "UNKNOWN",
                Code: "UNKNOWN_TOOL",
                Reason: $"Tool '{toolName}' is not in the Engineering MCP contract.",
                Remediation: "Call tools/list and pass an exact published tool name.",
                Profile: null,
                RequiredPermission: null,
                PermissionCeiling: policy.Current.PermissionCeiling.ToString(),
                Risk: null,
                CapabilityId: null,
                CapabilityAvailable: false,
                TargetRiskIsDynamic: false,
                InvocationConditions: ["No execution claim can be made for an unknown tool."],
                AgentDirective: "Do not guess whether this tool exists. Refresh tools/list and retry with an exact name.");
        }

        var publication = ToolPolicyCatalog.Publication(toolName, policy.Current);
        var capabilityAvailable = capabilities.IsAvailable(definition.CapabilityId);
        if (!publication.Published)
        {
            return Report(
                definition, policy.Current, capabilityAvailable, published: false, allowed: false, status: "DENIED",
                publication.Code, publication.Reason, publication.Remediation,
                "The active tool publication policy denies this tool. Report the exact code and remediation; do not bypass it.");
        }

        var decision = engine.Authorize(definition.ToPolicy(), policy.Current, capabilityAvailable);
        if (!decision.Allowed)
        {
            return Report(
                definition, policy.Current, capabilityAvailable, published: true, allowed: false, status: "DENIED",
                decision.Code, decision.Reason, decision.Remediation,
                "The active authorization policy denies this tool. Report the exact code and remediation; do not bypass it.");
        }

        return Report(
            definition, policy.Current, capabilityAvailable, published: true, allowed: true, status: "ALLOWED_BY_POLICY",
            "ALLOW", "The tool is published and allowed by the active policy and runtime capability registry.", null,
            $"Policy allows '{toolName}'. Do not report it as policy-disabled. Invoke it with valid inputs; only report a later denial using the exact structured error returned by that invocation.");
    }

    private static ToolPreflightReport Report(
        ToolAccessDefinition definition,
        McpPolicy policy,
        bool capabilityAvailable,
        bool published,
        bool allowed,
        string status,
        string code,
        string reason,
        string? remediation,
        string directive)
    {
        var conditions = new List<string>
        {
            "This preflight covers tool publication, permission ceiling, risk flags, and runtime capability only.",
            "A real invocation still validates its inputs and any applicable process, filesystem, selector, adapter, screenshot, and audit requirements."
        };
        if (definition.TargetRiskIsDynamic)
            conditions.Add("The selected UI target is classified at invocation time; an explicitly denied or destructive target can require stricter authorization.");

        return new(
            definition.ToolName,
            Known: true,
            Published: published,
            AllowedByPolicy: allowed,
            Status: status,
            Code: code,
            Reason: reason,
            Remediation: remediation,
            Profile: definition.Profile,
            RequiredPermission: definition.RequiredPermission.ToString(),
            PermissionCeiling: policy.PermissionCeiling.ToString(),
            Risk: definition.TargetRiskIsDynamic ? $"{definition.Risk} (baseline; target-classified)" : definition.Risk.ToString(),
            CapabilityId: definition.CapabilityId,
            CapabilityAvailable: capabilityAvailable,
            TargetRiskIsDynamic: definition.TargetRiskIsDynamic,
            InvocationConditions: conditions,
            AgentDirective: directive);
    }
}

public sealed record ToolPreflightReport(
    string ToolName,
    bool Known,
    bool Published,
    bool AllowedByPolicy,
    string Status,
    string Code,
    string Reason,
    string? Remediation,
    string? Profile,
    string? RequiredPermission,
    string PermissionCeiling,
    string? Risk,
    string? CapabilityId,
    bool CapabilityAvailable,
    bool TargetRiskIsDynamic,
    IReadOnlyList<string> InvocationConditions,
    string AgentDirective);
