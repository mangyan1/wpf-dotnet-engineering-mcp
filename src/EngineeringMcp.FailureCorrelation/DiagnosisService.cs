using System.Collections.Concurrent;
using System.Text.Json;
using EngineeringMcp.Contracts;
using EngineeringMcp.Diagnostics;
using EngineeringMcp.Source;
using EngineeringMcp.Wpf;

namespace EngineeringMcp.FailureCorrelation;

public sealed class DiagnosisService(
    WpfAutomationService wpf,
    DotNetDiagnosticsService diagnostics,
    WpfProbeClient probe,
    BackendProbeClient backend,
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

        var probeExceptions = await probe.RequestAsync(wpfProcessId, new ProbeRequest(string.Empty, "exceptions"), cancellationToken).ConfigureAwait(false);
        var wpfExceptions = probeExceptions.Success && probeExceptions.Value is { Success: true } exceptionResponse
            ? ReadProbeList<WpfExceptionObservation>(exceptionResponse.Value).Where(item => item.TimestampUtc >= started.AddMinutes(-5)).Take(50).ToArray()
            : [];
        foreach (var exception in wpfExceptions)
        {
            evidence.Add(new EvidenceItem(EvidenceKind.Observed,
                $"Recent WPF exception observed: {exception.Type}: {exception.Message}",
                exception.Source, correlationId, exception.TimestampUtc));
        }
        if (!probeExceptions.Success)
            unknowns.Add("The optional in-process WPF exception probe was unavailable.");

        var bindingResult = await probe.RequestAsync(wpfProcessId,
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

        var validationResult = await probe.RequestAsync(wpfProcessId,
            new ProbeRequest(string.Empty, "validation", AutomationId: selector.AutomationId, Name: selector.Name), cancellationToken).ConfigureAwait(false);
        if (validationResult.Success && validationResult.Value is { Success: true } validationResponse &&
            TryGetArrayLength(validationResponse.Value, out var validationCount) && validationCount > 0)
        {
            evidence.Add(new EvidenceItem(EvidenceKind.Observed,
                $"WPF validation reports {validationCount} error item(s) in the selected subtree.",
                "wpf_probe_validation", correlationId, DateTimeOffset.UtcNow));
        }

        var snapshot = wpf.Snapshot(wpfProcessId, maxElements: 300, maxDepth: 10);
        if (snapshot.Success && snapshot.Value is not null)
        {
            foreach (var item in snapshot.Value.Elements.Where(element => ContainsErrorSignal(element.Name)).Take(10))
            {
                evidence.Add(new EvidenceItem(EvidenceKind.Observed,
                    $"Current UI contains possible error/status text on {item.ControlType}: '{item.Name}'.",
                    "wpf_snapshot", correlationId, DateTimeOffset.UtcNow));
            }
        }

        IReadOnlyList<BackendRequestObservation> backendRequests = [];
        if (backendProcessId is int backendPid)
        {
            var backendResult = await backend.RequestAsync(backendPid, "recent", 200, cancellationToken).ConfigureAwait(false);
            if (backendResult.Success && backendResult.Value is { Success: true } response)
            {
                backendRequests = ReadBackendObservations(response.Value).Where(request => request.TimestampUtc >= started.AddMinutes(-5)).ToArray();
                foreach (var request in backendRequests)
                {
                    evidence.Add(new EvidenceItem(EvidenceKind.Observed,
                        $"Recent backend {request.Method} {request.Path} returned HTTP {request.StatusCode} in {request.DurationMs:F1} ms.",
                        "aspnet.recent", request.TraceId ?? correlationId, request.TimestampUtc));
                }
            }
            else unknowns.Add("Backend state could not be inspected through the configured adapter.");
        }
        else unknowns.Add("No backend process was supplied, so backend state was not observed.");

        if (!string.IsNullOrWhiteSpace(sourceRoot))
        {
            var stacks = wpfExceptions.Select(item => item.StackTrace)
                .Concat(backendRequests.Select(item => item.ExceptionStackTrace))
                .Where(stack => !string.IsNullOrWhiteSpace(stack)).Cast<string>();
            foreach (var stack in stacks.Take(10))
            {
                var mapped = source.MapStackTrace(stack, sourceRoot, 20);
                if (!mapped.Success || mapped.Value is null) continue;
                foreach (var location in mapped.Value)
                    evidence.Add(new EvidenceItem(EvidenceKind.Observed, $"Exception stack maps to approved source: {location.File}:{location.Line}.", "source_map_stacktrace", correlationId, DateTimeOffset.UtcNow));
            }
        }
        else unknowns.Add("No approved source root was supplied, so source mapping was not attempted.");

        var failureObserved = wpfExceptions.Length > 0 ||
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
                backendCorrelation = ReadBackendCorrelation(beginResponse.Value);
            if (backendCorrelation is null)
                unknowns.Add("The backend adapter did not establish an action correlation marker; backend evidence will use bounded time-window correlation.");
        }

        var actionCapture = await diagnostics.CaptureExceptionsDuringAsync(
            wpfProcessId,
            _ => Task.FromResult(wpf.Click(wpfProcessId, selector)),
            exceptionQueue, observationWindowMs, cancellationToken).ConfigureAwait(false);

        if (!actionCapture.Success || actionCapture.Value is null)
        {
            if (backendProcessId is int failedBackendPid && backendCorrelation is not null)
                _ = await backend.RequestAsync(failedBackendPid, "end_correlation", 1, cancellationToken, correlationId).ConfigureAwait(false);
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

        IReadOnlyList<WpfExceptionObservation> wpfExceptions = Array.Empty<WpfExceptionObservation>();
        var probeExceptions = await probe.RequestAsync(wpfProcessId, new ProbeRequest(string.Empty, "exceptions"), cancellationToken).ConfigureAwait(false);
        if (probeExceptions.Success && probeExceptions.Value is { Success: true } probeExceptionResponse)
        {
            wpfExceptions = ReadProbeList<WpfExceptionObservation>(probeExceptionResponse.Value)
                .Where(x => x.TimestampUtc >= started.AddMilliseconds(-250))
                .ToArray();
            foreach (var exception in wpfExceptions)
            {
                evidence.Add(new EvidenceItem(EvidenceKind.Observed,
                    $"WPF in-process exception observed: {exception.Type}: {exception.Message}",
                    exception.Source, correlationId, exception.TimestampUtc));
            }
        }
        else
        {
            unknowns.Add("The optional in-process WPF exception probe was unavailable; dispatcher/domain exception evidence may be incomplete.");
        }

        var bindingRequest = new ProbeRequest(string.Empty, "binding_errors", AutomationId: selector.AutomationId, Name: selector.Name);
        var bindingErrors = await probe.RequestAsync(wpfProcessId, bindingRequest, cancellationToken).ConfigureAwait(false);
        if (bindingErrors.Success && bindingErrors.Value is { Success: true } bindingResponse)
        {
            foreach (var binding in ReadProbeList<BindingDiagnostic>(bindingResponse.Value).Take(20))
            {
                evidence.Add(new EvidenceItem(EvidenceKind.Observed,
                    $"WPF binding error observed on {binding.Element}.{binding.Property}; Path={binding.Path ?? "<unknown>"}; Status={binding.Status ?? "<unknown>"}.",
                    "wpf_probe_binding_errors", correlationId, DateTimeOffset.UtcNow));
            }
        }

        var validation = await probe.RequestAsync(wpfProcessId, new ProbeRequest(string.Empty, "validation", AutomationId: selector.AutomationId, Name: selector.Name), cancellationToken).ConfigureAwait(false);
        if (validation.Success && validation.Value is { Success: true } validationResponse && TryGetArrayLength(validationResponse.Value, out var validationCount) && validationCount > 0)
        {
            evidence.Add(new EvidenceItem(EvidenceKind.Observed,
                $"WPF validation reports {validationCount} error item(s) in the selected subtree.",
                "wpf_probe_validation", correlationId, DateTimeOffset.UtcNow));
        }

        var after = wpf.Snapshot(wpfProcessId, maxElements: 300, maxDepth: 10);
        if (after.Success && after.Value is not null)
        {
            var likelyErrors = after.Value.Elements
                .Where(e => ContainsErrorSignal(e.Name))
                .Take(10)
                .ToArray();
            foreach (var item in likelyErrors)
            {
                evidence.Add(new EvidenceItem(EvidenceKind.Observed,
                    $"Post-action UI contains possible error/status text on {item.ControlType}: '{item.Name}'.",
                    "wpf_snapshot", correlationId, DateTimeOffset.UtcNow));
            }
        }

        IReadOnlyList<BackendRequestObservation> backendRequests = Array.Empty<BackendRequestObservation>();
        if (backendProcessId is int backendPid)
        {
            var backendResult = backendCorrelation is null
                ? await backend.RequestAsync(backendPid, "recent", 200, cancellationToken).ConfigureAwait(false)
                : await backend.RequestAsync(backendPid, "correlated", 200, cancellationToken,
                    correlationId, backendCorrelation.AfterSequence).ConfigureAwait(false);
            if (backendResult.Success && backendResult.Value is { Success: true } response)
            {
                backendRequests = ReadBackendObservations(response.Value)
                    .Where(x => backendCorrelation is not null || x.TimestampUtc >= started.AddSeconds(-1))
                    .ToArray();
                foreach (var request in backendRequests)
                {
                    var claim = $"Backend {request.Method} {request.Path} returned HTTP {request.StatusCode} in {request.DurationMs:F1} ms.";
                    evidence.Add(new EvidenceItem(EvidenceKind.Correlated, claim,
                        backendCorrelation is null ? "aspnet.recent (time-window correlation)" : "aspnet.correlated (action marker)",
                        request.TraceId ?? correlationId, request.TimestampUtc));
                    if (!string.IsNullOrWhiteSpace(request.ExceptionType))
                    {
                        evidence.Add(new EvidenceItem(EvidenceKind.Observed,
                            $"Backend exception observed: {request.ExceptionType}: {request.ExceptionMessage}",
                            "aspnet_exceptions", request.TraceId ?? correlationId, request.TimestampUtc));
                    }
                }
            }
            else
            {
                unknowns.Add("Backend state could not be inspected through the configured backend probe.");
            }

            if (backendCorrelation is not null)
                _ = await backend.RequestAsync(backendPid, "end_correlation", 1, cancellationToken, correlationId).ConfigureAwait(false);
        }
        else
        {
            unknowns.Add("No backend process was supplied, so backend behavior was not observed.");
        }

        if (!string.IsNullOrWhiteSpace(sourceRoot))
        {
            var stacks = backendRequests.Select(x => x.ExceptionStackTrace)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Cast<string>()
                .Concat(exceptionQueue.Select(x => x.StackTrace).Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>())
                .Concat(wpfExceptions.Select(x => x.StackTrace).Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>());
            var mappedAny = false;
            foreach (var stack in stacks.Take(10))
            {
                var mapped = source.MapStackTrace(stack, sourceRoot, 20);
                if (!mapped.Success || mapped.Value is null) continue;
                foreach (var loc in mapped.Value)
                {
                    mappedAny = true;
                    evidence.Add(new EvidenceItem(EvidenceKind.Observed,
                        $"Exception stack maps to approved source: {loc.File}:{loc.Line}.",
                        "source_map_stacktrace", correlationId, DateTimeOffset.UtcNow));
                }
            }
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

    private static IReadOnlyList<T> ReadProbeList<T>(object? value)
    {
        if (value is JsonElement element)
        {
            try
            {
                return element.Deserialize<List<T>>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
            }
            catch (JsonException) { return []; }
        }
        return value as IReadOnlyList<T> ?? [];
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

    private static IReadOnlyList<BackendRequestObservation> ReadBackendObservations(object? value)
    {
        if (value is JsonElement element)
        {
            try
            {
                return element.Deserialize<List<BackendRequestObservation>>(new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                }) ?? [];
            }
            catch (JsonException) { return []; }
        }
        return value as IReadOnlyList<BackendRequestObservation> ?? [];
    }

    private static BackendCorrelationObservation? ReadBackendCorrelation(object? value)
    {
        if (value is JsonElement element)
        {
            try
            {
                return element.Deserialize<BackendCorrelationObservation>(new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
            }
            catch (JsonException) { return null; }
        }
        return value as BackendCorrelationObservation;
    }

    private static bool ContainsErrorSignal(string? text)
        => !string.IsNullOrWhiteSpace(text) &&
           (text.Contains("error", StringComparison.OrdinalIgnoreCase)
            || text.Contains("failed", StringComparison.OrdinalIgnoreCase)
            || text.Contains("unable", StringComparison.OrdinalIgnoreCase)
            || text.Contains("exception", StringComparison.OrdinalIgnoreCase));
}
