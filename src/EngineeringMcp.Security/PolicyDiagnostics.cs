using EngineeringMcp.Contracts;

namespace EngineeringMcp.Security;

public sealed record PolicyDiagnosticFinding(
    string Code,
    string Severity,
    string Summary,
    string Remediation);

public sealed record PolicyDiagnosticReport(
    string PolicySource,
    string PermissionCeiling,
    int ProcessRuleCount,
    int SourceRootCount,
    IReadOnlyList<string> EnabledToolProfiles,
    IReadOnlyList<PolicyDiagnosticFinding> Findings);

public static class PolicyDiagnostics
{
    public static PolicyDiagnosticReport Analyze(McpPolicy policy, string policySource)
    {
        // The actual source (including its path, which Control Center already displays) is reported
        // here; MCP-facing tools collapse it to "configured-file" to keep policy paths out of the
        // tool contract.
        var findings = new List<PolicyDiagnosticFinding>();

        if (string.Equals(policySource, "locked-down-default", StringComparison.Ordinal))
        {
            findings.Add(new(
                "POLICY_NOT_CONFIGURED",
                "warning",
                "Engineering MCP is using its metadata-only fallback policy.",
                "In Control Center, choose Authorize WPF workspace or Select policy, then restart the MCP server."));
        }

        if (policy.PermissionCeiling == PermissionLevel.Metadata)
        {
            findings.Add(new(
                "PERMISSION_CEILING_METADATA",
                "warning",
                "Only metadata tools can pass the current permission ceiling.",
                "Select an approved policy whose permissionCeiling matches the intended diagnostic or UI workflow."));
        }

        if (policy.Processes.Allow.Count == 0)
        {
            findings.Add(new(
                "PROCESS_ALLOWLIST_EMPTY",
                "warning",
                "No target processes are allowlisted, so process and WPF tools fail closed.",
                "Use Authorize WPF workspace to discover built WPF applications, or select an approved policy with exact executable names and paths."));
        }

        if (policy.Filesystem.ReadRoots.Count == 0)
        {
            findings.Add(new(
                "SOURCE_ROOTS_EMPTY",
                "warning",
                "No source roots are allowlisted, so source tools fail closed.",
                "Use an approved policy with the narrowest required local repository root in filesystem.readRoots."));
        }

        if (!policy.Audit.Enabled)
        {
            findings.Add(new(
                "AUDIT_DISABLED",
                "warning",
                "Audit recording is disabled.",
                "Enable audit recording before using Engineering MCP for diagnostic or UI operations."));
        }

        if (policy.Processes.Allow.Any(rule => !string.IsNullOrWhiteSpace(rule.Publisher)))
        {
            findings.Add(new(
                "PROCESS_PUBLISHER_RULE_FAILS_CLOSED",
                "warning",
                "Publisher-constrained process rules fail closed because Authenticode publisher verification is not implemented.",
                "Use an exact executable path and optional SHA-256 rule instead of publisher in an approved policy, then restart the MCP server."));
        }

        return new PolicyDiagnosticReport(
            policySource,
            policy.PermissionCeiling.ToString(),
            policy.Processes.Allow.Count,
            policy.Filesystem.ReadRoots.Count,
            policy.EnabledToolProfiles?.OrderBy(value => value, StringComparer.Ordinal).ToArray() ?? [],
            findings);
    }
}
