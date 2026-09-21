using EngineeringMcp.Contracts;

namespace EngineeringMcp.Wpf;

/// <summary>
/// Diagnostic seam for the WPF in-process probe client: the single request surface consumed by
/// failure-correlation orchestration. Implemented by <see cref="WpfProbeClient"/>; consumed via
/// dependency injection so orchestration code can be exercised against a test double without a
/// live named-pipe probe.
/// </summary>
public interface IWpfProbeClient
{
    /// <summary>Sends one bounded request to the authenticated in-process probe of an allowlisted process.</summary>
    Task<ToolResult<ProbeResponse>> RequestAsync(int processId, ProbeRequest request, CancellationToken cancellationToken = default);
}
