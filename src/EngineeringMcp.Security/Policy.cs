using EngineeringMcp.Contracts;

namespace EngineeringMcp.Security;

public sealed record ToolPolicy(
    string ToolName,
    PermissionLevel RequiredPermission,
    RiskClass Risk,
    string CapabilityId);

public sealed record PolicyDecision(bool Allowed, string Code, string Reason, string? Remediation = null)
{
    public static PolicyDecision Allow() => new(true, "ALLOW", "Policy allows this operation.");
    public static PolicyDecision Deny(string code, string reason, string remediation) => new(false, code, reason, remediation);
}

public sealed class PolicyEngine
{
    public PolicyDecision Authorize(ToolPolicy policy, McpPolicy configuredPolicy, bool capabilityAvailable)
    {
        if (!capabilityAvailable)
            return PolicyDecision.Deny(
                "CAPABILITY_UNAVAILABLE",
                $"Capability '{policy.CapabilityId}' required by '{policy.ToolName}' is unavailable.",
                "Call system_capabilities to confirm runtime support. Install or enable the required component, then restart Engineering MCP; do not weaken policy to bypass a missing capability.");

        if (configuredPolicy.PermissionCeiling < policy.RequiredPermission)
            return PolicyDecision.Deny(
                "PERMISSION_DENIED",
                $"Tool '{policy.ToolName}' requires {policy.RequiredPermission}, but the configured permission ceiling is {configuredPolicy.PermissionCeiling}.",
                $"In Control Center, select or configure an approved policy with permissionCeiling set to at least {policy.RequiredPermission}, then restart the MCP server.");

        if (configuredPolicy.DisabledTools?.Contains(policy.ToolName, StringComparer.Ordinal) == true)
            return PolicyDecision.Deny(
                "TOOL_DISABLED",
                $"Tool '{policy.ToolName}' is listed in disabledTools.",
                $"Keep the denial or remove '{policy.ToolName}' from disabledTools in an approved policy, then restart the MCP server.");

        if (configuredPolicy.EnabledTools is { Count: > 0 } &&
            !configuredPolicy.EnabledTools.Contains(policy.ToolName, StringComparer.Ordinal))
            return PolicyDecision.Deny(
                "TOOL_NOT_ENABLED",
                $"Tool '{policy.ToolName}' is not present in the enabled tool allowlist.",
                $"Enable the approved profile containing '{policy.ToolName}' or add the exact tool to enabledTools, then restart the MCP server.");

        if (policy.Risk == RiskClass.Destructive && !configuredPolicy.AllowDestructiveActions)
            return PolicyDecision.Deny(
                "EXPLICIT_APPROVAL_REQUIRED",
                $"Tool '{policy.ToolName}' is destructive and allowDestructiveActions is false.",
                "Do not bypass this gate. Obtain explicit approval, use a narrowly scoped policy with allowDestructiveActions enabled, and restart the MCP server.");

        if (policy.Risk == RiskClass.Privileged && !configuredPolicy.AllowPrivilegedDiagnostics)
            return PolicyDecision.Deny(
                "PRIVILEGED_DIAGNOSTICS_DISABLED",
                $"Tool '{policy.ToolName}' is privileged and allowPrivilegedDiagnostics is false.",
                "Keep privileged diagnostics disabled unless explicitly approved. If approved, enable allowPrivilegedDiagnostics in a narrowly scoped policy and restart the MCP server.");

        return PolicyDecision.Allow();
    }
}
