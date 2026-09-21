using System.Collections.Concurrent;
using EngineeringMcp.Contracts;

namespace EngineeringMcp.Diagnostics;

/// <summary>
/// Diagnostic seam for the .NET runtime diagnostics service: the exception-capture surface
/// consumed by failure-correlation orchestration. Implemented by
/// <see cref="DotNetDiagnosticsService"/>; consumed via dependency injection so orchestration
/// code can be exercised against a test double without a live EventPipe session.
/// </summary>
public interface IDotNetDiagnosticsService
{
    /// <summary>Runs the action once while capturing runtime exceptions into the supplied queue.</summary>
    Task<ToolResult<DiagnosticActionResult<T>>> CaptureExceptionsDuringAsync<T>(
        int processId,
        Func<CancellationToken, Task<T>> action,
        ConcurrentQueue<ExceptionObservation> exceptions,
        int postActionObservationMs = 0,
        CancellationToken cancellationToken = default);
}
