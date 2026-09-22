using System.Collections.Concurrent;
using System.Text.Json;
using EngineeringMcp.Contracts;
using EngineeringMcp.Diagnostics;
using EngineeringMcp.Source;
using EngineeringMcp.Wpf;

namespace EngineeringMcp.FailureCorrelation;

public sealed class DiagnosisService(
    IWpfAutomationService wpf,
    IDotNetDiagnosticsService diagnostics,
    IWpfProbeClient probe,
    IBackendProbeClient backend,
    SourceIntelligenceService source)
{
    public async Task<ToolResult<DiagnosisReport>> DiagnoseObserveAsync(
        int wpfProcessId,
        UiSelector selector,
        int? backendProcessId = null,
        string? sourceRoot = null,
        CancellationToken cancellationToken = default)
    {
        var correlationId = Guid.NewGuid().ToString("N");
        var started = DateTimeOffset.UtcNow;
        var evidence = new List<EvidenceItem>();
        var unknowns = new List<string>();
        var next = new List<string>();

        cancellationToken.ThrowIfCancellationRequested();
        var selected = wpf.Query(wpfProcessId, selector);
        if (!selected.Success || selected.Value is null)
            return ToolResult<DiagnosisReport>.Fail(selected.Error!.Code, selected.Error.Message, selected.Error.Retryable);
        evidence.Add(new EvidenceItem(EvidenceKind.Observed,
            $"Selected UI element is {selected.Value.ControlType} '{selected.Value.Name}', enabled={selected.Value.IsEnabled}, offscreen={selected.Value.IsOffscreen}.",
            "wpf_query", correlationId, DateTimeOffset.UtcNow));

        var wpfExceptions = await CollectProbeExceptionsAsync(
            wpfProcessId, correlationId, started,
            exceptionWindow: TimeSpan.FromMinutes(5), exceptionTakeCount: 50,
            exceptionEvidencePrefix: "Recent WPF exception observed",
            exceptionUnavailableUnknown: "The optional in-process WPF exception probe was unavailable.",
            exceptionUnknownOnInnerFailure: false,
            evidence, unknowns, cancellationToken).ConfigureAwait(false);

        await CollectBindingErrorsAsync(wpfProcessId, selector, correlationId, evidence, cancellationToken).ConfigureAwait(false);
        await CollectValidationEvidenceAsync(wpfProcessId, selector, correlationId, evidence, cancellationToken).ConfigureAwait(false);
        CollectSnapshotErrorEvidence(wpfProcessId, correlationId, "Current", evidence);

        var backendRequests = await CollectBackendEvidenceAsync(
            backendProcessId, correlationId, started, ObserveBackendPolicy,
            correlation: null, evidence, unknowns, cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(sourceRoot))
        {
            MapSourceEvidence(
                wpfExceptions.Select(item => item.StackTrace)
                    .Concat(backendRequests.Select(item => item.ExceptionStackTrace)),
                sourceRoot, correlationId, evidence);
        }
        else unknowns.Add("No approved source root was supplied, so source mapping was not attempted.");

        var failureObserved = wpfExceptions.Count > 0 ||
            evidence.Any(item => item.Source is "wpf_probe_binding_errors" or "wpf_probe_validation" or "wpf_snapshot") ||
            backendRequests.Any(request => request.StatusCode >= 500 || !string.IsNullOrWhiteSpace(request.ExceptionType));
        if (!failureObserved)
            unknowns.Add("No failure evidence was observed; absence of evidence is not proof that the workflow is correct.");
        if (backendRequests.Any(request => !string.IsNullOrWhiteSpace(request.TraceId)))
            next.Add("Use the observed backend TraceId to inspect the corresponding application trace.");

        return ToolResult<DiagnosisReport>.Ok(new DiagnosisReport(
            correlationId,
            failureObserved ? "FAILED_EVIDENCE_PRESENT" : "NO_CONFIRMED_FAILURE",
            evidence,
            unknowns.Distinct().ToArray(),
            next.Distinct().ToArray(),
            DateTimeOffset.UtcNow));
    }

    public async Task<ToolResult<DiagnosisReport>> DiagnoseClickAsync(
        int wpfProcessId,
        UiSelector selector,
        int? backendProcessId = null,
        string? sourceRoot = null,
        int observationWindowMs = 1500,
        CancellationToken cancellationToken = default)
    {
        observationWindowMs = Math.Clamp(observationWindowMs, 100, 10_000);
        var correlationId = Guid.NewGuid().ToString("N");
        var started = DateTimeOffset.UtcNow;
        var evidence = new List<EvidenceItem>();
        var unknowns = new List<string>();
        var next = new List<string>();
        var exceptionQueue = new ConcurrentQueue<ExceptionObservation>();

        var before = wpf.Query(wpfProcessId, selector);
        if (!before.Success || before.Value is null)
            return ToolResult<DiagnosisReport>.Fail(before.Error!.Code, before.Error.Message, before.Error.Retryable);

        evidence.Add(new EvidenceItem(EvidenceKind.Observed,
            $"Target UI element resolved as {before.Value.ControlType} '{before.Value.Name}'.",
            "wpf_query", correlationId, DateTimeOffset.UtcNow));

        BackendCorrelationObservation? backendCorrelation = null;
        if (backendProcessId is int correlationBackendPid)
        {
            var begin = await backend.RequestAsync(
                correlationBackendPid, "begin_correlation", 1, cancellationToken, correlationId).ConfigureAwait(false);
            if (begin.Success && begin.Value is { Success: true } beginResponse)
                backendCorrelation = ReadProbeValue<BackendCorrelationObservation>(beginResponse.Value);
            if (backendCorrelation is null)
                unknowns.Add("The backend adapter did not establish an action correlation marker; backend evidence will use bounded time-window correlation.");
        }

        var actionCapture = await diagnostics.CaptureExceptionsDuringAsync(
            wpfProcessId,
            _ => Task.FromResult(wpf.Click(wpfProcessId, selector)),
            exceptionQueue, observationWindowMs, cancellationToken).ConfigureAwait(false);

        if (!actionCapture.Success || actionCapture.Value is null)
        {
            // Release the backend correlation marker even when the diagnosis aborts early; the
            // stuck-lock unknown is discarded because a Fail result carries no report body.
            if (backendProcessId is int failedBackendPid && backendCorrelation is not null)
                await EndBackendCorrelationAsync(failedBackendPid, correlationId, new List<string>(), cancellationToken).ConfigureAwait(false);
            return ToolResult<DiagnosisReport>.Fail(actionCapture.Error!.Code, actionCapture.Error.Message, actionCapture.Error.Retryable);
        }

        var clickResult = actionCapture.Value.ActionResult;
        if (!actionCapture.Value.CaptureAvailable)
        {
            unknowns.Add("Frontend EventPipe exception capture was unavailable during the action; the UI action was still executed exactly once.");
            evidence.Add(new EvidenceItem(EvidenceKind.Observed,
                $"Runtime exception capture warning: {actionCapture.Value.CaptureWarningCode ?? "EVENTPIPE_CAPTURE_UNAVAILABLE"}.",
                "dotnet.eventpipe", correlationId, DateTimeOffset.UtcNow));
        }
        else if (!string.IsNullOrWhiteSpace(actionCapture.Value.CaptureWarningCode))
        {
            unknowns.Add($"Runtime capture completed with warning {actionCapture.Value.CaptureWarningCode}; diagnostic evidence may be incomplete.");
        }

        if (!clickResult.Success)
        {
            evidence.Add(new EvidenceItem(EvidenceKind.Observed,
                $"UI invocation failed: {clickResult.Error?.Code}.", "wpf_click", correlationId, DateTimeOffset.UtcNow));
        }
        else
        {
            evidence.Add(new EvidenceItem(EvidenceKind.Observed,
                "UI invocation completed through the automation provider.", "wpf_click", correlationId, DateTimeOffset.UtcNow));
        }

        foreach (var exception in exceptionQueue)
        {
            evidence.Add(new EvidenceItem(EvidenceKind.Observed,
                $"Frontend runtime exception observed: {exception.Type}: {exception.Message}",
                exception.Source, correlationId, exception.TimestampUtc));
        }

        var wpfExceptions = await CollectProbeExceptionsAsync(
            wpfProcessId, correlationId, started,
            exceptionWindow: TimeSpan.FromMilliseconds(250), exceptionTakeCount: int.MaxValue,
            exceptionEvidencePrefix: "WPF in-process exception observed",
            exceptionUnavailableUnknown: "The optional in-process WPF exception probe was unavailable; dispatcher/domain exception evidence may be incomplete.",
            exceptionUnknownOnInnerFailure: true,
            evidence, unknowns, cancellationToken).ConfigureAwait(false);

        await CollectBindingErrorsAsync(wpfProcessId, selector, correlationId, evidence, cancellationToken).ConfigureAwait(false);
        await CollectValidationEvidenceAsync(wpfProcessId, selector, correlationId, evidence, cancellationToken).ConfigureAwait(false);
        CollectSnapshotErrorEvidence(wpfProcessId, correlationId, "Post-action", evidence);

        var backendRequests = await CollectBackendEvidenceAsync(
            backendProcessId, correlationId, started, ClickBackendPolicy,
            backendCorrelation, evidence, unknowns, cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(sourceRoot))
        {
            var mappedAny = MapSourceEvidence(
                backendRequests.Select(x => x.ExceptionStackTrace)
                    .Concat(exceptionQueue.Select(x => x.StackTrace))
                    .Concat(wpfExceptions.Select(x => x.StackTrace)),
                sourceRoot, correlationId, evidence);
            if (!mappedAny)
            {
                unknowns.Add("No approved source location could be mapped from the captured exception evidence.");
                next.Add("Capture a stack-bearing backend exception or trace and retry source mapping.");
            }
        }
        else
        {
            unknowns.Add("No approved source root was supplied, so source correlation was not attempted.");
        }

        if (exceptionQueue.IsEmpty && wpfExceptions.Count == 0 && backendRequests.All(r => string.IsNullOrWhiteSpace(r.ExceptionType)))
            unknowns.Add("No exception was observed in the configured diagnostic window; absence of evidence is not proof that no failure occurred.");

        if (backendRequests.Count > 0)
            next.Add("Use backend TraceId/ActivityId where available to strengthen causal correlation beyond the time window.");

        var failed = !clickResult.Success || exceptionQueue.Count > 0 || wpfExceptions.Count > 0 || backendRequests.Any(r => r.StatusCode >= 500 || !string.IsNullOrWhiteSpace(r.ExceptionType));
        var report = new DiagnosisReport(
            correlationId,
            failed ? "FAILED" : "NO_CONFIRMED_FAILURE",
            evidence,
            unknowns.Distinct().ToArray(),
            next.Distinct().ToArray(),
            DateTimeOffset.UtcNow);
        return ToolResult<DiagnosisReport>.Ok(report);
    }

    // The two diagnosis paths intentionally differ only in evidence windows and message wording;
    // the collectors below keep those differences in one policy record per path so the
    // orchestration, evidence ordering, and observable payloads stay identical between both.
    // Windows are positive durations looked back from the diagnosis start (started - window).
    private sealed record BackendEvidencePolicy(
        TimeSpan ObservationWindow,
        bool BypassWindowWhenCorrelated,
        EvidenceKind ClaimKind,
        string ClaimPrefix,
        string UncorrelatedSource,
        string CorrelatedSource,
        bool IncludeExceptionEvidence,
        string UnavailableUnknown,
        string MissingProcessUnknown);

    private static readonly BackendEvidencePolicy ObserveBackendPolicy = new(
        ObservationWindow: TimeSpan.FromMinutes(5),
        BypassWindowWhenCorrelated: false,
        ClaimKind: EvidenceKind.Observed,
        ClaimPrefix: "Recent backend",
        UncorrelatedSource: "aspnet.recent",
        CorrelatedSource: "aspnet.recent",
        IncludeExceptionEvidence: false,
        UnavailableUnknown: "Backend state could not be inspected through the configured adapter.",
        MissingProcessUnknown: "No backend process was supplied, so backend state was not observed.");

    private static readonly BackendEvidencePolicy ClickBackendPolicy = new(
        ObservationWindow: TimeSpan.FromSeconds(1),
        BypassWindowWhenCorrelated: true,
        ClaimKind: EvidenceKind.Correlated,
        ClaimPrefix: "Backend",
        UncorrelatedSource: "aspnet.recent (time-window correlation)",
        CorrelatedSource: "aspnet.correlated (action marker)",
        IncludeExceptionEvidence: true,
        UnavailableUnknown: "Backend state could not be inspected through the configured backend probe.",
        MissingProcessUnknown: "No backend process was supplied, so backend behavior was not observed.");

    private async Task<IReadOnlyList<WpfExceptionObservation>> CollectProbeExceptionsAsync(
        int processId,
        string correlationId,
        DateTimeOffset started,
        TimeSpan exceptionWindow,
        int exceptionTakeCount,
        string exceptionEvidencePrefix,
        string exceptionUnavailableUnknown,
        bool exceptionUnknownOnInnerFailure,
        List<EvidenceItem> evidence,
        List<string> unknowns,
        CancellationToken cancellationToken)
    {
        var probeExceptions = await probe.RequestAsync(processId, new ProbeRequest(string.Empty, "exceptions"), cancellationToken).ConfigureAwait(false);
        var innerSucceeded = false;
        IReadOnlyList<WpfExceptionObservation> exceptions = [];
        if (probeExceptions.Success && probeExceptions.Value is { Success: true } response)
        {
            innerSucceeded = true;
            // exceptionWindow is a positive duration looked back from the diagnosis start.
            exceptions = ReadProbeList<WpfExceptionObservation>(response.Value)
                .Where(item => item.TimestampUtc >= started - exceptionWindow)
                .Take(exceptionTakeCount)
                .ToArray();
            foreach (var exception in exceptions)
            {
                evidence.Add(new EvidenceItem(EvidenceKind.Observed,
                    $"{exceptionEvidencePrefix}: {exception.Type}: {exception.Message}",
                    exception.Source, correlationId, exception.TimestampUtc));
            }
        }
        if (exceptionUnknownOnInnerFailure ? !innerSucceeded : !probeExceptions.Success)
            unknowns.Add(exceptionUnavailableUnknown);
        return exceptions;
    }

    private async Task CollectBindingErrorsAsync(
        int processId,
        UiSelector selector,
        string correlationId,
        List<EvidenceItem> evidence,
        CancellationToken cancellationToken)
    {
        var bindingResult = await probe.RequestAsync(processId,
            new ProbeRequest(string.Empty, "binding_errors", AutomationId: selector.AutomationId, Name: selector.Name), cancellationToken).ConfigureAwait(false);
        if (bindingResult.Success && bindingResult.Value is { Success: true } bindingResponse)
        {
            foreach (var binding in ReadProbeList<BindingDiagnostic>(bindingResponse.Value).Take(20))
            {
                evidence.Add(new EvidenceItem(EvidenceKind.Observed,
                    $"WPF binding error observed on {binding.Element}.{binding.Property}; Path={binding.Path ?? "<unknown>"}; Status={binding.Status ?? "<unknown>"}.",
                    "wpf_probe_binding_errors", correlationId, DateTimeOffset.UtcNow));
            }
        }
    }

    private async Task CollectValidationEvidenceAsync(
        int processId,
        UiSelector selector,
        string correlationId,
        List<EvidenceItem> evidence,
        CancellationToken cancellationToken)
    {
        var validationResult = await probe.RequestAsync(processId,
            new ProbeRequest(string.Empty, "validation", AutomationId: selector.AutomationId, Name: selector.Name), cancellationToken).ConfigureAwait(false);
        if (validationResult.Success && validationResult.Value is { Success: true } validationResponse &&
            TryGetArrayLength(validationResponse.Value, out var validationCount) && validationCount > 0)
        {
            evidence.Add(new EvidenceItem(EvidenceKind.Observed,
                $"WPF validation reports {validationCount} error item(s) in the selected subtree.",
                "wpf_probe_validation", correlationId, DateTimeOffset.UtcNow));
        }
    }

    private void CollectSnapshotErrorEvidence(int processId, string correlationId, string evidencePrefix, List<EvidenceItem> evidence)
    {
        var snapshot = wpf.Snapshot(processId, maxElements: 300, maxDepth: 10);
        if (snapshot.Success && snapshot.Value is not null)
        {
            foreach (var item in snapshot.Value.Elements.Where(element => ContainsErrorSignal(element.Name)).Take(10))
            {
                evidence.Add(new EvidenceItem(EvidenceKind.Observed,
                    $"{evidencePrefix} UI contains possible error/status text on {item.ControlType}: '{item.Name}'.",
                    "wpf_snapshot", correlationId, DateTimeOffset.UtcNow));
            }
        }
    }

    private async Task<IReadOnlyList<BackendRequestObservation>> CollectBackendEvidenceAsync(
        int? backendProcessId,
        string correlationId,
        DateTimeOffset started,
        BackendEvidencePolicy policy,
        BackendCorrelationObservation? correlation,
        List<EvidenceItem> evidence,
        List<string> unknowns,
        CancellationToken cancellationToken)
    {
        if (backendProcessId is not int backendPid)
        {
            unknowns.Add(policy.MissingProcessUnknown);
            return [];
        }

        IReadOnlyList<BackendRequestObservation> requests = [];
        var backendResult = correlation is null
            ? await backend.RequestAsync(backendPid, "recent", 200, cancellationToken).ConfigureAwait(false)
            : await backend.RequestAsync(backendPid, "correlated", 200, cancellationToken,
                correlationId, correlation.AfterSequence).ConfigureAwait(false);
        if (backendResult.Success && backendResult.Value is { Success: true } response)
        {
            requests = ReadProbeList<BackendRequestObservation>(response.Value)
                .Where(request => (policy.BypassWindowWhenCorrelated && correlation is not null) || request.TimestampUtc >= started - policy.ObservationWindow)
                .ToArray();
            foreach (var request in requests)
            {
                evidence.Add(new EvidenceItem(policy.ClaimKind,
                    $"{policy.ClaimPrefix} {request.Method} {request.Path} returned HTTP {request.StatusCode} in {request.DurationMs:F1} ms.",
                    correlation is null ? policy.UncorrelatedSource : policy.CorrelatedSource,
                    request.TraceId ?? correlationId, request.TimestampUtc));
                if (policy.IncludeExceptionEvidence && !string.IsNullOrWhiteSpace(request.ExceptionType))
                {
                    evidence.Add(new EvidenceItem(EvidenceKind.Observed,
                        $"Backend exception observed: {request.ExceptionType}: {request.ExceptionMessage}",
                        "aspnet_exceptions", request.TraceId ?? correlationId, request.TimestampUtc));
                }
            }
        }
        else
        {
            unknowns.Add(policy.UnavailableUnknown);
        }

        if (correlation is not null)
            await EndBackendCorrelationAsync(backendPid, correlationId, unknowns, cancellationToken).ConfigureAwait(false);
        return requests;
    }

    private async Task EndBackendCorrelationAsync(int backendProcessId, string correlationId, List<string> unknowns, CancellationToken cancellationToken)
    {
        // A failed end marker would leave the backend correlation lock held until it expires;
        // that stuck-lock state must be visible in the report instead of silently discarded.
        var end = await backend.RequestAsync(backendProcessId, "end_correlation", 1, cancellationToken, correlationId).ConfigureAwait(false);
        if (!end.Success || end.Value is not { Success: true })
            unknowns.Add("The backend adapter did not acknowledge the correlation end marker; the backend may retain the action correlation lock until it expires.");
    }

    private bool MapSourceEvidence(IEnumerable<string?> stacks, string sourceRoot, string correlationId, List<EvidenceItem> evidence)
    {
        var mappedAny = false;
        foreach (var stack in stacks.Where(stack => !string.IsNullOrWhiteSpace(stack)).Take(10))
        {
            var mapped = source.MapStackTrace(stack!, sourceRoot, 20);
            if (!mapped.Success || mapped.Value is null) continue;
            foreach (var location in mapped.Value)
            {
                mappedAny = true;
                evidence.Add(new EvidenceItem(EvidenceKind.Observed,
                    $"Exception stack maps to approved source: {location.File}:{location.Line}.",
                    "source_map_stacktrace", correlationId, DateTimeOffset.UtcNow));
            }
        }
        return mappedAny;
    }

    private static readonly JsonSerializerOptions ProbeJsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static IReadOnlyList<T> ReadProbeList<T>(object? value)
    {
        if (value is JsonElement element)
        {
            try
            {
                return element.Deserialize<List<T>>(ProbeJsonOptions) ?? [];
            }
            catch (JsonException) { return []; }
        }
        return value as IReadOnlyList<T> ?? [];
    }

    private static T? ReadProbeValue<T>(object? value) where T : class
    {
        if (value is JsonElement element)
        {
            try
            {
                return element.Deserialize<T>(ProbeJsonOptions);
            }
            catch (JsonException) { return null; }
        }
        return value as T;
    }

    private static bool TryGetArrayLength(object? value, out int count)
    {
        count = 0;
        if (value is JsonElement { ValueKind: JsonValueKind.Array } element)
        {
            count = element.GetArrayLength();
            return true;
        }
        if (value is System.Collections.ICollection collection)
        {
            count = collection.Count;
            return true;
        }
        return false;
    }

    private static bool ContainsErrorSignal(string? text)
        => !string.IsNullOrWhiteSpace(text) &&
           (text.Contains("error", StringComparison.OrdinalIgnoreCase)
            || text.Contains("failed", StringComparison.OrdinalIgnoreCase)
            || text.Contains("unable", StringComparison.OrdinalIgnoreCase)
            || text.Contains("exception", StringComparison.OrdinalIgnoreCase));
}
