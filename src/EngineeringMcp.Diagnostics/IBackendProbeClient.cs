using EngineeringMcp.Contracts;

namespace EngineeringMcp.Diagnostics;

/// <summary>
/// Diagnostic seam for the ASP.NET backend probe client: the request surface consumed by
/// failure-correlation orchestration. Implemented by <see cref="BackendProbeClient"/>; consumed
/// via dependency injection so orchestration code can be exercised against a test double
/// without a live adapter pipe.
/// </summary>
public interface IBackendProbeClient
{
    /// <summary>Sends one bounded request to the authenticated backend probe adapter of an allowlisted process.</summary>
    Task<ToolResult<BackendProbeResponse>> RequestAsync(
        int processId,
        string operation,
        int limit = 100,
        CancellationToken cancellationToken = default,
        string? correlationId = null,
        long? afterSequence = null);
}
