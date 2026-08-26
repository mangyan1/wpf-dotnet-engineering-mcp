using EngineeringMcp.Contracts;

namespace EngineeringMcp.Security;

public sealed record ToolPolicy(
    string ToolName,
    PermissionLevel RequiredPermission,
    RiskClass Risk,
    string CapabilityId);

public sealed record PolicyDecision(bool Allowed, string Code, string Reason)
{
    public static PolicyDecision Allow() => new(true, "ALLOW", "Policy allows this operation.");
    public static PolicyDecision Deny(string code, string reason) => new(false, code, reason);
}

public interface IPolicyEngine
{
    PolicyDecision Authorize(ToolPolicy policy, McpPolicy configuredPolicy, bool capabilityAvailable);
}

public sealed class PolicyEngine : IPolicyEngine
{
    public PolicyDecision Authorize(ToolPolicy policy, McpPolicy configuredPolicy, bool capabilityAvailable)
    {
        if (!capabilityAvailable)
            return PolicyDecision.Deny("CAPABILITY_UNAVAILABLE", "The requested capability is not enabled.");

        if (configuredPolicy.PermissionCeiling < policy.RequiredPermission)
            return PolicyDecision.Deny("PERMISSION_DENIED", "The configured permission ceiling is insufficient.");

        if (configuredPolicy.DisabledTools?.Contains(policy.ToolName, StringComparer.Ordinal) == true)
            return PolicyDecision.Deny("TOOL_DISABLED", "The requested tool is disabled by policy.");

        if (configuredPolicy.EnabledTools is { Count: > 0 } &&
            !configuredPolicy.EnabledTools.Contains(policy.ToolName, StringComparer.Ordinal))
            return PolicyDecision.Deny("TOOL_NOT_ENABLED", "The requested tool is not present in the policy tool allowlist.");

        if (policy.Risk == RiskClass.Destructive && !configuredPolicy.AllowDestructiveActions)
            return PolicyDecision.Deny("EXPLICIT_APPROVAL_REQUIRED", "Destructive operations are disabled by policy.");

        if (policy.Risk == RiskClass.Privileged && !configuredPolicy.AllowPrivilegedDiagnostics)
            return PolicyDecision.Deny("PRIVILEGED_DIAGNOSTICS_DISABLED", "Privileged diagnostics are disabled by policy.");

        return PolicyDecision.Allow();
    }
}
