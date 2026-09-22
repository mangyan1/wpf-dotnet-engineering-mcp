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

/// <summary>
/// Shared enabledTools/disabledTools evaluation used by both the execution gate (PolicyEngine)
/// and tool publication (ToolPolicyCatalog) so the two surfaces cannot drift.
/// </summary>
internal static class ToolListRules
{
    /// <summary>Returns the deny decision for the configured tool lists, or null when neither list denies the tool.</summary>
    public static PolicyDecision? DenyFor(McpPolicy policy, string toolName)
    {
        if (policy.DisabledTools?.Contains(toolName, StringComparer.Ordinal) == true)
            return PolicyDecision.Deny(
                "TOOL_DISABLED",
                $"Tool '{toolName}' is listed in disabledTools.",
                $"Keep the denial or remove '{toolName}' from disabledTools in an approved policy, then restart the MCP server.");

        if (policy.EnabledTools is { Count: > 0 } &&
            !policy.EnabledTools.Contains(toolName, StringComparer.Ordinal))
            return PolicyDecision.Deny(
                "TOOL_NOT_ENABLED",
                $"Tool '{toolName}' is not present in enabledTools.",
                $"Add '{toolName}' to enabledTools in an approved policy, then restart the MCP server.");

        return null;
    }
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

        if (ToolListRules.DenyFor(configuredPolicy, policy.ToolName) is { } toolListDenial)
            return toolListDenial;

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
