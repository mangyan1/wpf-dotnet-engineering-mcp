using EngineeringMcp.Contracts;
using EngineeringMcp.Security;

namespace EngineeringMcp.Host;

public sealed class CapabilityRegistry(FilePolicyProvider policyProvider)
{
    private IReadOnlyDictionary<string, bool> Current
    {
        get
        {
            var windows = OperatingSystem.IsWindows();
            var probe = windows && HasStrongToken("ENGINEERING_MCP_PROBE_TOKEN");
            var backend = HasStrongToken("ENGINEERING_MCP_BACKEND_TOKEN");
            return new Dictionary<string, bool>(StringComparer.Ordinal)
            {
                ["system.metadata"] = true,
                ["security.policy"] = true,
                ["security.redaction"] = true,
                ["audit.events"] = policyProvider.Current.Audit.Enabled,
                ["wpf.uia.read"] = windows,
                ["wpf.uia.interact"] = windows,
                ["wpf.screenshot.redacted"] = windows && policyProvider.Current.Screenshots.Enabled,
                ["wpf.probe"] = probe,
                ["wpfui.resources"] = probe,
                ["wpfui.static_audit"] = true,
                ["a11y.audit"] = windows,
                ["gui.audit"] = windows,
                ["ux.heuristics"] = windows,
                ["dotnet.eventpipe"] = true,
                ["dotnet.clrmd"] = true,
                ["source.roslyn"] = true,
                ["source.xaml"] = true,
                ["source.symbols"] = false,
                ["aspnet.telemetry"] = backend,
                ["diagnose.correlation"] = windows
            };
        }
    }

    public CapabilityManifest GetManifest() => new(
        ServerVersion: typeof(CapabilityRegistry).Assembly.GetName().Version?.ToString() ?? "0.1.0-dev",
        Capabilities: Current,
        GeneratedAtUtc: DateTimeOffset.UtcNow);

    public bool IsAvailable(string capabilityId)
        => Current.TryGetValue(capabilityId, out var available) && available;

    private static bool HasStrongToken(string name)
        => Environment.GetEnvironmentVariable(name) is { Length: >= 32 } value && !string.IsNullOrWhiteSpace(value);
}
