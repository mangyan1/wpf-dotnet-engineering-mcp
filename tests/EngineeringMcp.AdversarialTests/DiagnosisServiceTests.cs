using System.Collections.Concurrent;
using System.Text.Json;
using EngineeringMcp.Contracts;
using EngineeringMcp.Diagnostics;
using EngineeringMcp.FailureCorrelation;
using EngineeringMcp.Security;
using EngineeringMcp.Source;
using EngineeringMcp.TestSupport;
using EngineeringMcp.Wpf;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EngineeringMcp.AdversarialTests;

/// <summary>
/// Unit tests for DiagnosisService's collector/decision logic. The service is driven through
/// its injection seams (IWpfAutomationService, IWpfProbeClient, IBackendProbeClient,
/// IDotNetDiagnosticsService) with probe responses shaped like real framed-JSON payloads
/// (JsonElement values), so the evidence windows, verdict mapping, and correlation unknowns
/// are exercised without a live UIA session or probe pipe.
/// </summary>
[TestClass]
public sealed class DiagnosisServiceTests
{
    private static readonly UiSelector Selector = new(AutomationId: "SaveButton");
    private const int ProcessId = 4242;

    private static DiagnosisService NewService(
        FakeWpfService wpf,
        FakeDiagnostics? diagnostics = null,
        FakeProbeClient? probe = null,
        FakeBackendClient? backend = null,
        SourceIntelligenceService? source = null)
        => new(
            wpf,
            diagnostics ?? new FakeDiagnostics(),
            probe ?? new FakeProbeClient(),
            backend ?? new FakeBackendClient(),
            source ?? NewSourceService(Path.Combine(Path.GetTempPath(), "engineering-mcp-absent-source-root")));

    private static SourceIntelligenceService NewSourceService(string root)
    {
        var provider = new FixedPolicyProvider(McpPolicy.LockedDownDefault with
        {
            Filesystem = new FileSystemPolicy([root], Array.Empty<string>())
        });
        return new SourceIntelligenceService(new FileGuard(provider), provider, new RedactionService());
    }

    private static ToolResult<DiagnosisReport> Observe(
        FakeWpfService wpf,
        FakeProbeClient? probe = null,
        FakeBackendClient? backend = null,
        SourceIntelligenceService? source = null,
        int? backendProcessId = null,
        string? sourceRoot = null)
    {
        var service = NewService(wpf, probe: probe, backend: backend, source: source);
        return service.DiagnoseObserveAsync(ProcessId, Selector, backendProcessId, sourceRoot).GetAwaiter().GetResult();
    }

    private static ToolResult<DiagnosisReport> Click(
        FakeWpfService wpf,
        FakeDiagnostics? diagnostics = null,
        FakeProbeClient? probe = null,
        FakeBackendClient? backend = null,
        SourceIntelligenceService? source = null,
        int? backendProcessId = null,
        string? sourceRoot = null,
        int observationWindowMs = 1500)
    {
        var service = NewService(wpf, diagnostics, probe, backend, source);
        return service.DiagnoseClickAsync(ProcessId, Selector, backendProcessId, sourceRoot, observationWindowMs).GetAwaiter().GetResult();
    }

    private static FakeWpfService QuietWpf(params UiElementSnapshot[] snapshotElements)
        => new(
            Ok(UiElement("SaveButton")),
            Ok(new UiSnapshot(ProcessId, "MainWindow", DateTimeOffset.UtcNow, snapshotElements, false, 300)));

    private static ToolResult<T> Ok<T>(T value) => ToolResult<T>.Ok(value);

    private static UiElementSnapshot UiElement(string name, string controlType = "Button")
        => new($"ref-{name}", null, controlType, name, "SaveButton", "Window", "WPF",
            new RectDto(0, 0, 10, 10), true, false, true, false, Array.Empty<string>(), 0);

    private static WpfExceptionObservation WpfException(DateTimeOffset timestamp, string type = "System.InvalidOperationException")
        => new(timestamp, "dispatcher", type, "Synthetic diagnosis exception", StackTrace: null);

    private static BackendRequestObservation BackendRequest(DateTimeOffset timestamp, int statusCode = 200, string? exceptionType = null)
        => new(timestamp, "GET", "/api/value", statusCode, 12.5, "trace-1", exceptionType,
            exceptionType is null ? null : "Synthetic backend failure", ExceptionStackTrace: null);

    [TestMethod]
    public void Observe_QueryFailure_ForwardsPolicyFailureWithoutDiagnosis()
    {
        var wpf = new FakeWpfService(query: ToolResult<UiElementSnapshot>.Fail("WPF_QUERY_DENIED", "Process is not allowlisted.", false));

        var result = Observe(wpf);

        Assert.IsFalse(result.Success);
        Assert.AreEqual("WPF_QUERY_DENIED", result.Error?.Code);
    }

    [TestMethod]
    public void Observe_QuietSurface_StatesUnknownsInsteadOfClaimingFailure()
    {
        var wpf = QuietWpf();
        var probe = new FakeProbeClient();

        var result = Observe(wpf, probe: probe);

        Assert.IsTrue(result.Success);
        Assert.AreEqual("NO_CONFIRMED_FAILURE", result.Value?.Status);
        CollectionAssert.AreEquivalent(
            new[]
            {
                "No failure evidence was observed; absence of evidence is not proof that the workflow is correct.",
                "No backend process was supplied, so backend state was not observed.",
                "No approved source root was supplied, so source mapping was not attempted."
            },
            result.Value!.Unknowns.ToArray());
        Assert.IsTrue(result.Value!.Evidence.All(item => item.Source == "wpf_query"),
            "A quiet surface must not fabricate probe, backend, or source evidence.");
    }

    [TestMethod]
    public void Observe_ProbeExceptionWithinPositiveWindow_IsObservedFailureEvidence()
    {
        var wpf = QuietWpf();
        var probe = new FakeProbeClient
        {
            Handler = request => request.Operation == "exceptions"
                ? OkResponse(new[] { WpfException(DateTimeOffset.UtcNow) })
                : EmptyResponse()
        };

        var result = Observe(wpf, probe: probe);

        Assert.AreEqual("FAILED_EVIDENCE_PRESENT", result.Value?.Status);
        Assert.IsTrue(result.Value!.Evidence.Any(item =>
            item.Source == "dispatcher" &&
            item.Claim.Contains("Recent WPF exception observed", StringComparison.Ordinal) &&
            item.Claim.Contains("System.InvalidOperationException", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Observe_ProbeExceptionOlderThanWindow_IsExcludedFromEvidence()
    {
        var wpf = QuietWpf();
        var probe = new FakeProbeClient
        {
            Handler = request => request.Operation == "exceptions"
                ? OkResponse(new[] { WpfException(DateTimeOffset.UtcNow.AddMinutes(-6)) })
                : EmptyResponse()
        };

        var result = Observe(wpf, probe: probe);

        Assert.AreEqual("NO_CONFIRMED_FAILURE", result.Value?.Status);
        Assert.IsFalse(result.Value!.Evidence.Any(item => item.Source == "dispatcher"),
            "The five-minute observe window is a positive duration looked back from diagnosis start.");
    }

    [TestMethod]
    public void Observe_ProbeTransportFailure_StatesProbeUnknownWithoutEvidence()
    {
        var wpf = QuietWpf();
        var probe = new FakeProbeClient { FailTransport = true };

        var result = Observe(wpf, probe: probe);

        Assert.AreEqual("NO_CONFIRMED_FAILURE", result.Value?.Status);
        Assert.IsTrue(result.Value!.Unknowns.Contains(
            "The optional in-process WPF exception probe was unavailable."));
        Assert.IsFalse(result.Value.Evidence.Any(item => item.Source == "dispatcher"));
    }

    [TestMethod]
    public void Observe_ProbeInnerFailure_IsDistinguishedFromTransportFailure()
    {
        var wpf = QuietWpf();
        var probe = new FakeProbeClient
        {
            Handler = _ => new ProbeResponse(false, ErrorCode: "PROBE_BUSY", ErrorMessage: "Synthetic probe failure.")
        };

        var result = Observe(wpf, probe: probe);

        // The observe path treats a reachable-but-failed probe as "no exception evidence"
        // and reserves the probe-unavailable unknown for transport-level failures.
        Assert.IsFalse(result.Value!.Unknowns.Contains(
            "The optional in-process WPF exception probe was unavailable."));
        Assert.IsFalse(result.Value.Evidence.Any(item => item.Source == "dispatcher"));
    }

    [TestMethod]
    public void Observe_BindingAndValidationEvidence_CarrySelectorAndFailTheVerdict()
    {
        var wpf = QuietWpf();
        var probe = new FakeProbeClient
        {
            Handler = request => request.Operation switch
            {
                "binding_errors" => OkResponse(new[] { new BindingDiagnostic("MainWindow", "Text", Path: "Name", Status: "BindingExpressionPathError", Error: null) }),
                "validation" => OkResponse(new[] { 1, 2, 3 }),
                _ => EmptyResponse()
            }
        };

        var result = Observe(wpf, probe: probe);

        Assert.AreEqual("FAILED_EVIDENCE_PRESENT", result.Value?.Status);
        Assert.IsTrue(result.Value!.Evidence.Any(item => item.Source == "wpf_probe_binding_errors"));
        Assert.IsTrue(result.Value.Evidence.Any(item =>
            item.Source == "wpf_probe_validation" && item.Claim.Contains("3 error item(s)", StringComparison.Ordinal)));
        var bindingRequest = probe.Requests.Single(request => request.Operation == "binding_errors");
        Assert.AreEqual("SaveButton", bindingRequest.AutomationId);
        Assert.IsNull(bindingRequest.Name);
    }

    [TestMethod]
    public void Observe_SnapshotErrorText_IsBoundedAndUsesBoundedSnapshotRequest()
    {
        var elements = Enumerable.Range(1, 14)
            .Select(index => UiElement($"Error {index}")).ToArray();
        var wpf = QuietWpf(elements);

        var result = Observe(wpf);

        Assert.AreEqual(10, result.Value!.Evidence.Count(item => item.Source == "wpf_snapshot"));
        Assert.AreEqual((300, 10), (wpf.LastSnapshotMaxElements, wpf.LastSnapshotMaxDepth));
        Assert.AreEqual("FAILED_EVIDENCE_PRESENT", result.Value.Status);
    }

    [TestMethod]
    public void Observe_BackendProbeFailure_AddsAdapterUnknownInsteadOfEvidence()
    {
        var wpf = QuietWpf();
        var probe = new FakeProbeClient();
        var backend = new FakeBackendClient { Handler = (_, _, _) => new BackendProbeResponse(false, ErrorCode: "BACKEND_TOKEN_UNAVAILABLE") };

        var result = Observe(wpf, probe: probe, backend: backend, backendProcessId: 99);

        Assert.AreEqual("NO_CONFIRMED_FAILURE", result.Value?.Status);
        Assert.IsTrue(result.Value!.Unknowns.Contains(
            "Backend state could not be inspected through the configured adapter."));
        CollectionAssert.DoesNotContain(backend.Operations.ToArray(), "correlated");
    }

    [TestMethod]
    public void Observe_BackendServerErrorWithinWindow_IsObservedFailureEvidence()
    {
        var wpf = QuietWpf();
        var probe = new FakeProbeClient();
        var backend = new FakeBackendClient
        {
            Handler = (operation, _, _) => operation == "recent"
                ? BackendOk(new[] { BackendRequest(DateTimeOffset.UtcNow, statusCode: 500) })
                : EmptyBackendResponse()
        };

        var result = Observe(wpf, probe: probe, backend: backend, backendProcessId: 99);

        Assert.AreEqual("FAILED_EVIDENCE_PRESENT", result.Value?.Status);
        var evidence = result.Value!.Evidence.Single(item => item.Source == "aspnet.recent");
        Assert.AreEqual(EvidenceKind.Observed, evidence.Kind);
        Assert.AreEqual("trace-1", evidence.CorrelationId);
        Assert.IsTrue(result.Value.NextVerification.Any(item =>
            item.Contains("TraceId", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Observe_BackendRequestOutsideWindow_IsExcluded()
    {
        var wpf = QuietWpf();
        var probe = new FakeProbeClient();
        var backend = new FakeBackendClient
        {
            Handler = (operation, _, _) => operation == "recent"
                ? BackendOk(new[] { BackendRequest(DateTimeOffset.UtcNow.AddMinutes(-6), statusCode: 500) })
                : EmptyBackendResponse()
        };

        var result = Observe(wpf, probe: probe, backend: backend, backendProcessId: 99);

        Assert.AreEqual("NO_CONFIRMED_FAILURE", result.Value?.Status);
        Assert.IsFalse(result.Value!.Evidence.Any(item => item.Source == "aspnet.recent"));
    }

    [TestMethod]
    public void Click_BeforeQueryFailure_FailsForwardedWithoutCaptureOrClick()
    {
        var wpf = new FakeWpfService(query: ToolResult<UiElementSnapshot>.Fail("WPF_QUERY_DENIED", "Process is not allowlisted.", false));
        var diagnostics = new FakeDiagnostics();

        var result = Click(wpf, diagnostics);

        Assert.IsFalse(result.Success);
        Assert.AreEqual("WPF_QUERY_DENIED", result.Error?.Code);
        Assert.AreEqual(0, diagnostics.CaptureCalls);
        Assert.IsFalse(wpf.ClickInvoked);
    }

    [TestMethod]
    public void Click_FailedInvocation_ClampsWindowAndReportsFailedStatus()
    {
        var wpf = new FakeWpfService(
            Ok(UiElement("SaveButton")),
            click: ToolResult<object>.Fail("WPF_INVOKE_DENIED", "The element is disabled.", false));
        var diagnostics = new FakeDiagnostics();

        var result = Click(wpf, diagnostics, observationWindowMs: 50_000);

        Assert.IsTrue(result.Success);
        Assert.AreEqual("FAILED", result.Value?.Status);
        Assert.AreEqual(10_000, diagnostics.LastObservationWindowMs);
        Assert.IsTrue(result.Value!.Evidence.Any(item =>
            item.Source == "wpf_click" && item.Claim.Contains("WPF_INVOKE_DENIED", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Click_UnavailableCapture_StatesExecutionStillHappenedExactlyOnce()
    {
        var wpf = new FakeWpfService(Ok(UiElement("SaveButton")));
        var diagnostics = new FakeDiagnostics { CaptureAvailable = false, WarningCode = "EVENTPIPE_ATTACH_FAILED" };

        var result = Click(wpf, diagnostics);

        Assert.AreEqual("NO_CONFIRMED_FAILURE", result.Value?.Status);
        Assert.IsTrue(result.Value!.Unknowns.Any(unknown =>
            unknown.Contains("capture was unavailable", StringComparison.Ordinal) &&
            unknown.Contains("executed exactly once", StringComparison.Ordinal)));
        Assert.IsTrue(result.Value.Evidence.Any(item =>
            item.Source == "dotnet.eventpipe" && item.Claim.Contains("EVENTPIPE_ATTACH_FAILED", StringComparison.Ordinal)));
        Assert.IsTrue(wpf.ClickInvoked);
    }

    [TestMethod]
    public void Click_RuntimeExceptionDuringAction_IsObservedEvidenceAndFailsVerdict()
    {
        var wpf = new FakeWpfService(Ok(UiElement("SaveButton")));
        var diagnostics = new FakeDiagnostics
        {
            CapturedExceptions =
            [
                new ExceptionObservation(DateTimeOffset.UtcNow, "System.DivideByZeroException",
                    "Synthetic runtime failure", StackTrace: null, ProcessId, "EventPipe:Microsoft-Windows-DotNETRuntime/ExceptionStart")
            ]
        };

        var result = Click(wpf, diagnostics);

        Assert.AreEqual("FAILED", result.Value?.Status);
        Assert.IsTrue(result.Value!.Evidence.Any(item =>
            item.Source == "EventPipe:Microsoft-Windows-DotNETRuntime/ExceptionStart" &&
            item.Claim.Contains("Frontend runtime exception observed", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Click_AcceptedCorrelation_UsesMarkerSequencesAndBypassesWindow()
    {
        var wpf = new FakeWpfService(Ok(UiElement("SaveButton")));
        var probe = new FakeProbeClient();
        var backend = new FakeBackendClient
        {
            Handler = (operation, correlationId, afterSequence) => operation switch
            {
                "begin_correlation" => new BackendProbeResponse(true,
                    JsonSerializer.SerializeToElement(new BackendCorrelationObservation(correlationId!, 42, DateTimeOffset.UtcNow))),
                "correlated" => BackendOk(new[] { BackendRequest(DateTimeOffset.UtcNow.AddMinutes(-10)) }),
                _ => new BackendProbeResponse(true)
            }
        };

        var result = Click(wpf, probe: probe, backend: backend, backendProcessId: 99);

        Assert.IsTrue(result.Success);
        CollectionAssert.AreEqual(
            new[] { "begin_correlation", "correlated", "end_correlation" },
            backend.Operations.ToArray());
        Assert.AreEqual(42L, backend.AfterSequences[1]);
        Assert.AreEqual(result.Value!.CorrelationId, backend.CorrelationIds[1]);
        var evidence = result.Value.Evidence.Single(item => item.Source == "aspnet.correlated (action marker)");
        Assert.AreEqual(EvidenceKind.Correlated, evidence.Kind);
        Assert.IsFalse(result.Value.Unknowns.Any(unknown => unknown.Contains("correlation lock", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Click_DeclinedCorrelation_FallsBackToTimeWindowWithExceptionEvidence()
    {
        var wpf = new FakeWpfService(Ok(UiElement("SaveButton")));
        var probe = new FakeProbeClient();
        var backend = new FakeBackendClient
        {
            Handler = (operation, _, _) => operation switch
            {
                "begin_correlation" => new BackendProbeResponse(false, ErrorCode: "CORRELATION_BUSY"),
                "recent" => BackendOk(new[] { BackendRequest(DateTimeOffset.UtcNow, statusCode: 500, exceptionType: "System.Data.SqlClient.SqlException") }),
                _ => new BackendProbeResponse(true)
            }
        };

        var result = Click(wpf, probe: probe, backend: backend, backendProcessId: 99);

        Assert.AreEqual("FAILED", result.Value?.Status);
        Assert.IsTrue(result.Value!.Unknowns.Any(unknown =>
            unknown.Contains("did not establish an action correlation marker", StringComparison.Ordinal)));
        var evidence = result.Value.Evidence.Single(item => item.Source == "aspnet.recent (time-window correlation)");
        // The click path labels backend evidence CORRELATED even on the time-window fallback;
        // only the read-only observe path reports backend requests as OBSERVED.
        Assert.AreEqual(EvidenceKind.Correlated, evidence.Kind);
        Assert.IsTrue(result.Value.Evidence.Any(item =>
            item.Source == "aspnet_exceptions" &&
            item.Claim.Contains("System.Data.SqlClient.SqlException", StringComparison.Ordinal)));
        CollectionAssert.DoesNotContain(backend.Operations.ToArray(), "correlated");
    }

    [TestMethod]
    public void Click_DeclinedCorrelation_ExcludesRequestsOutsideOneSecondWindow()
    {
        var wpf = new FakeWpfService(Ok(UiElement("SaveButton")));
        var probe = new FakeProbeClient();
        var backend = new FakeBackendClient
        {
            Handler = (operation, _, _) => operation switch
            {
                "begin_correlation" => new BackendProbeResponse(false, ErrorCode: "CORRELATION_BUSY"),
                "recent" => BackendOk(new[] { BackendRequest(DateTimeOffset.UtcNow.AddSeconds(-30), statusCode: 500) }),
                _ => new BackendProbeResponse(true)
            }
        };

        var result = Click(wpf, probe: probe, backend: backend, backendProcessId: 99);

        Assert.AreEqual("NO_CONFIRMED_FAILURE", result.Value?.Status);
        Assert.IsFalse(result.Value!.Evidence.Any(item => item.Source == "aspnet.recent (time-window correlation)"));
    }

    [TestMethod]
    public void Click_UnacknowledgedEndMarker_SurfacesStuckCorrelationLock()
    {
        var wpf = new FakeWpfService(Ok(UiElement("SaveButton")));
        var probe = new FakeProbeClient();
        var backend = new FakeBackendClient
        {
            Handler = (operation, _, _) => operation switch
            {
                "begin_correlation" => new BackendProbeResponse(true,
                    JsonSerializer.SerializeToElement(new BackendCorrelationObservation("synthetic", 7, DateTimeOffset.UtcNow))),
                "end_correlation" => new BackendProbeResponse(false, ErrorCode: "END_REJECTED"),
                _ => new BackendProbeResponse(true)
            }
        };

        var result = Click(wpf, probe: probe, backend: backend, backendProcessId: 99);

        Assert.IsTrue(result.Success);
        Assert.IsTrue(result.Value!.Unknowns.Any(unknown =>
            unknown.Contains("did not acknowledge the correlation end marker", StringComparison.Ordinal) &&
            unknown.Contains("correlation lock", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Click_AbortedCapture_StillReleasesBackendCorrelationMarker()
    {
        var wpf = new FakeWpfService(Ok(UiElement("SaveButton")));
        var diagnostics = new FakeDiagnostics { FailCapture = true };
        var backend = new FakeBackendClient
        {
            Handler = (operation, _, _) => operation == "begin_correlation"
                ? new BackendProbeResponse(true,
                    JsonSerializer.SerializeToElement(new BackendCorrelationObservation("synthetic", 7, DateTimeOffset.UtcNow)))
                : new BackendProbeResponse(true)
        };

        var result = Click(wpf, diagnostics, backend: backend, backendProcessId: 99);

        Assert.IsFalse(result.Success);
        Assert.AreEqual("DOTNET_DIAGNOSTICS_UNAVAILABLE", result.Error?.Code);
        CollectionAssert.Contains(backend.Operations.ToArray(), "end_correlation");
    }

    [TestMethod]
    public void Click_QuietAction_StatesAbsenceOfEvidenceAndBackendNextStep()
    {
        var wpf = new FakeWpfService(Ok(UiElement("SaveButton")));
        var probe = new FakeProbeClient();
        var backend = new FakeBackendClient
        {
            Handler = (operation, _, _) => operation == "begin_correlation"
                ? new BackendProbeResponse(true,
                    JsonSerializer.SerializeToElement(new BackendCorrelationObservation("synthetic", 7, DateTimeOffset.UtcNow)))
                : operation == "correlated"
                    ? BackendOk(new[] { BackendRequest(DateTimeOffset.UtcNow) })
                    : new BackendProbeResponse(true)
        };

        var result = Click(wpf, probe: probe, backend: backend, backendProcessId: 99);

        Assert.AreEqual("NO_CONFIRMED_FAILURE", result.Value?.Status);
        Assert.IsTrue(result.Value!.Unknowns.Any(unknown =>
            unknown.Contains("No exception was observed in the configured diagnostic window", StringComparison.Ordinal)));
        Assert.IsTrue(result.Value.NextVerification.Any(next =>
            next.Contains("TraceId/ActivityId", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Click_SourceMappingOverApprovedRoot_ProducesSourceEvidence()
    {
        var root = Path.Combine(Path.GetTempPath(), "engineering-mcp-diagnosis-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var sourceFile = Path.Combine(root, "ViewModel.cs");
        File.WriteAllText(sourceFile, "// synthetic source file\n");
        try
        {
            var wpf = new FakeWpfService(Ok(UiElement("SaveButton")));
            var source = NewSourceService(root);
            var diagnostics = new FakeDiagnostics
            {
                CapturedExceptions =
                [
                    new ExceptionObservation(DateTimeOffset.UtcNow, "System.InvalidOperationException",
                        "Synthetic", $"   at App.ViewModel.Save() in {sourceFile}:line 2", ProcessId, "EventPipe")
                ]
            };

            var result = Click(wpf, diagnostics, source: source, sourceRoot: root);

            Assert.IsTrue(result.Success);
            Assert.IsTrue(result.Value!.Evidence.Any(item =>
                item.Source == "source_map_stacktrace" &&
                item.Claim.Contains($"{sourceFile}:2", StringComparison.Ordinal)));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void Click_SourceMappingWithoutStackLocations_StatesUnknownAndNextStep()
    {
        var root = Path.Combine(Path.GetTempPath(), "engineering-mcp-diagnosis-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var wpf = new FakeWpfService(Ok(UiElement("SaveButton")));
            var probe = new FakeProbeClient();
            var backend = new FakeBackendClient();
            var source = NewSourceService(root);

            var result = Click(wpf, probe: probe, backend: backend, source: source, sourceRoot: root);

            Assert.IsTrue(result.Value!.Unknowns.Any(unknown =>
                unknown.Contains("No approved source location could be mapped", StringComparison.Ordinal)));
            Assert.IsTrue(result.Value.NextVerification.Any(next =>
                next.Contains("stack-bearing backend exception", StringComparison.Ordinal)));
        }
        finally { Directory.Delete(root, true); }
    }

    private static ProbeResponse OkResponse<T>(T value) => new(true, JsonSerializer.SerializeToElement(value));

    private static ProbeResponse EmptyResponse() => new(true, JsonSerializer.SerializeToElement(Array.Empty<object>()));

    private static BackendProbeResponse BackendOk<T>(T value)
        => new(true, JsonSerializer.SerializeToElement(value));

    private static BackendProbeResponse EmptyBackendResponse()
        => new(true, JsonSerializer.SerializeToElement(Array.Empty<BackendRequestObservation>()));

    private sealed class FakeWpfService(
        ToolResult<UiElementSnapshot>? query = null,
        ToolResult<UiSnapshot>? snapshot = null,
        ToolResult<object>? click = null) : IWpfAutomationService
    {
        public bool ClickInvoked { get; private set; }
        public int LastSnapshotMaxElements { get; private set; }
        public int LastSnapshotMaxDepth { get; private set; }

        public ToolResult<UiElementSnapshot> Query(int processId, UiSelector selector)
            => query ?? Ok(UiElement(selector.AutomationId ?? "SaveButton"));

        public ToolResult<UiSnapshot> Snapshot(int processId, string? windowReference = null, int maxElements = 500, int maxDepth = 12)
        {
            LastSnapshotMaxElements = maxElements;
            LastSnapshotMaxDepth = maxDepth;
            return snapshot ?? Ok(new UiSnapshot(processId, "MainWindow", DateTimeOffset.UtcNow, [], false, maxElements));
        }

        public ToolResult<object> Click(int processId, UiSelector selector)
        {
            ClickInvoked = true;
            return click ?? Ok(new object());
        }
    }

    private sealed class FakeProbeClient : IWpfProbeClient
    {
        public List<ProbeRequest> Requests { get; } = [];
        public Func<ProbeRequest, ProbeResponse>? Handler { get; init; }

        // When set, the request itself fails at the transport level (the ToolResult is a
        // failure) instead of returning a reachable probe that answered Success=false.
        public bool FailTransport { get; init; }

        public Task<ToolResult<ProbeResponse>> RequestAsync(int processId, ProbeRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (FailTransport)
            {
                return Task.FromResult(ToolResult<ProbeResponse>.Fail(
                    "PROBE_TRANSPORT_FAILED", "Synthetic probe transport failure.", false));
            }
            var response = Handler?.Invoke(request)
                ?? new ProbeResponse(false, ErrorCode: "PROBE_UNAVAILABLE", ErrorMessage: "No handler configured.");
            return Task.FromResult(ToolResult<ProbeResponse>.Ok(response));
        }
    }

    private sealed class FakeBackendClient : IBackendProbeClient
    {
        public List<string> Operations { get; } = [];
        public List<string?> CorrelationIds { get; } = [];
        public List<long?> AfterSequences { get; } = [];
        public Func<string, string?, long?, BackendProbeResponse>? Handler { get; init; }

        public Task<ToolResult<BackendProbeResponse>> RequestAsync(
            int processId,
            string operation,
            int limit = 100,
            CancellationToken cancellationToken = default,
            string? correlationId = null,
            long? afterSequence = null)
        {
            Operations.Add(operation);
            CorrelationIds.Add(correlationId);
            AfterSequences.Add(afterSequence);
            var response = Handler?.Invoke(operation, correlationId, afterSequence)
                ?? new BackendProbeResponse(true, JsonSerializer.SerializeToElement(Array.Empty<BackendRequestObservation>()));
            return Task.FromResult(ToolResult<BackendProbeResponse>.Ok(response));
        }
    }

    private sealed class FakeDiagnostics : IDotNetDiagnosticsService
    {
        public bool CaptureAvailable { get; init; } = true;
        public string? WarningCode { get; init; }
        public bool FailCapture { get; init; }
        public IReadOnlyList<ExceptionObservation> CapturedExceptions { get; init; } = [];
        public int CaptureCalls { get; private set; }
        public int? LastObservationWindowMs { get; private set; }

        public Task<ToolResult<DiagnosticActionResult<T>>> CaptureExceptionsDuringAsync<T>(
            int processId,
            Func<CancellationToken, Task<T>> action,
            ConcurrentQueue<ExceptionObservation> exceptions,
            int postActionObservationMs = 0,
            CancellationToken cancellationToken = default)
        {
            CaptureCalls++;
            LastObservationWindowMs = postActionObservationMs;
            if (FailCapture)
                return Task.FromResult(ToolResult<DiagnosticActionResult<T>>.Fail(
                    "DOTNET_DIAGNOSTICS_UNAVAILABLE", "Synthetic capture failure.", false));

            var result = action(cancellationToken).GetAwaiter().GetResult();
            foreach (var exception in CapturedExceptions)
                exceptions.Enqueue(exception);
            return Task.FromResult(ToolResult<DiagnosticActionResult<T>>.Ok(
                new DiagnosticActionResult<T>(result, CaptureAvailable, WarningCode)));
        }
    }
}
