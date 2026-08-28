using System.Diagnostics;
using System.Security.Cryptography;
using EngineeringMcp.Contracts;

namespace EngineeringMcp.Security;

public sealed class ProcessGuard(FilePolicyProvider policyProvider)
{
    public ProcessDescriptor Describe(Process process)
    {
        string? path = null;
        try { path = process.MainModule?.FileName; } catch { }

        var (allowed, reason) = Evaluate(process.ProcessName, path);
        return new ProcessDescriptor(process.Id, process.ProcessName, path, allowed, reason);
    }

    public ToolResult<Process> RequireAllowed(int processId)
    {
        Process process;
        try { process = Process.GetProcessById(processId); }
        catch (ArgumentException)
        {
            return ToolResult<Process>.Fail(
                "PROCESS_NOT_FOUND",
                "The target process does not exist.",
                remediation: "Refresh the target process list and retry with a currently running process identifier.");
        }

        string? path = null;
        try { path = process.MainModule?.FileName; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            process.Dispose();
            return ToolResult<Process>.Fail(
                "PROCESS_PATH_UNAVAILABLE",
                "The executable path could not be verified; fail-closed policy rejected the target.",
                remediation: "Run Engineering MCP and the target in the same user session and elevation level, then retry. Do not bypass path verification.");
        }

        var match = FindMatchingRule(process.ProcessName, path);
        if (match is null)
        {
            process.Dispose();
            return ToolResult<Process>.Fail(
                "PROCESS_NOT_ALLOWED",
                "The target process name or executable path does not match the configured allowlist.",
                remediation: "In Control Center, select or provision a policy containing the exact trusted executable name and local path, then restart the MCP server.");
        }

        if (!string.IsNullOrWhiteSpace(match.Publisher))
        {
            process.Dispose();
            return ToolResult<Process>.Fail(
                "PROCESS_PUBLISHER_VERIFICATION_UNAVAILABLE",
                "Publisher verification is not implemented in V1; a policy requiring Publisher therefore fails closed.",
                remediation: "Use an exact executable path and optional SHA-256 rule instead of Publisher until publisher verification is implemented.");
        }

        if (!string.IsNullOrWhiteSpace(match.Sha256))
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                process.Dispose();
                return ToolResult<Process>.Fail(
                    "PROCESS_HASH_UNVERIFIABLE",
                    "Process hash was required but the executable path was unavailable.",
                    remediation: "Run Engineering MCP and the target in the same user session and elevation level so the executable can be verified.");
            }

            var expectedText = NormalizeHash(match.Sha256);
            if (expectedText.Length != 64)
            {
                process.Dispose();
                return ToolResult<Process>.Fail(
                    "PROCESS_POLICY_INVALID",
                    "Configured SHA-256 must contain exactly 64 hexadecimal characters.",
                    remediation: "Correct the process allowlist sha256 value in the selected policy, validate the policy, and restart the MCP server.");
            }
            byte[] expected;
            try { expected = Convert.FromHexString(expectedText); }
            catch (FormatException)
            {
                process.Dispose();
                return ToolResult<Process>.Fail(
                    "PROCESS_POLICY_INVALID",
                    "Configured SHA-256 is not valid hexadecimal.",
                    remediation: "Correct the process allowlist sha256 value in the selected policy, validate the policy, and restart the MCP server.");
            }
            using var stream = File.OpenRead(path);
            var actual = SHA256.HashData(stream);
            if (!CryptographicOperations.FixedTimeEquals(expected, actual))
            {
                process.Dispose();
                return ToolResult<Process>.Fail(
                    "PROCESS_HASH_MISMATCH",
                    "The executable hash does not match policy.",
                    remediation: "Treat the binary as untrusted. Verify the deployment source before approving and recording a new SHA-256 value.");
            }
        }

        return ToolResult<Process>.Ok(process);
    }

    private (bool Allowed, string Reason) Evaluate(string processName, string? path)
    {
        var rule = FindMatchingRule(processName, path);
        if (rule is null) return (false, "No process allowlist rule matched.");
        if (!string.IsNullOrWhiteSpace(rule.Publisher)) return (false, "Publisher-constrained rules fail closed until Authenticode publisher verification is implemented.");
        return (true, "Matched configured process allowlist.");
    }

    private AllowedProcessRule? FindMatchingRule(string processName, string? path)
    {
        foreach (var rule in policyProvider.Current.Processes.Allow)
        {
            var ruleName = Path.GetFileNameWithoutExtension(rule.Name);
            if (!string.Equals(ruleName, processName, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!string.IsNullOrWhiteSpace(rule.Path))
            {
                if (string.IsNullOrWhiteSpace(path)) continue;
                if (!PathsEqual(rule.Path, path)) continue;
            }

            return rule;
        }
        return null;
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
                         Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
                         OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string NormalizeHash(string hash)
        => hash.Replace("-", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal);
}
