using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Globalization;
using EngineeringMcp.Contracts;
using EngineeringMcp.Security;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;

namespace EngineeringMcp.Diagnostics;

public sealed class DotNetDiagnosticsService(
    ProcessGuard processGuard,
    FilePolicyProvider policyProvider,
    RedactionService redactor)
{
    private const int MaxActiveTraces = 2;
    private const long MaxTraceBytes = 64L * 1024 * 1024;
    private static readonly TimeSpan MaxTraceDuration = TimeSpan.FromSeconds(30);

    private sealed class ActiveTrace : IAsyncDisposable
    {
        public required string Id { get; init; }
        public required int ProcessId { get; init; }
        public required string Path { get; init; }
        public required DateTimeOffset StartedAtUtc { get; init; }
        public required EventPipeSession Session { get; init; }
        public required FileStream File { get; init; }
        public required Task CopyTask { get; init; }

        public async ValueTask DisposeAsync()
        {
            try { Session.Stop(); } catch { }
            try { await CopyTask.ConfigureAwait(false); } catch { }
            Session.Dispose();
            await File.DisposeAsync().ConfigureAwait(false);
            try { System.IO.File.Delete(Path); } catch { }
        }
    }

    private readonly ConcurrentDictionary<string, ActiveTrace> _traces = new(StringComparer.Ordinal);

    public ToolResult<RuntimeProcessInfo> GetRuntimeInfo(int processId)
    {
        var allowed = processGuard.RequireAllowed(processId);
        if (!allowed.Success || allowed.Value is null)
            return ToolResult<RuntimeProcessInfo>.Fail(allowed.Error!.Code, allowed.Error.Message, allowed.Error.Retryable, allowed.Error.Remediation);
        using var process = allowed.Value;

        try
        {
            var published = DiagnosticsClient.GetPublishedProcesses().Contains(processId);
            if (!published)
                return ToolResult<RuntimeProcessInfo>.Fail("DOTNET_DIAGNOSTICS_UNAVAILABLE", "Target process is allowed but does not expose a .NET diagnostics endpoint.");

            string? path = null;
            try { path = process.MainModule?.FileName; } catch { }
            return ToolResult<RuntimeProcessInfo>.Ok(new RuntimeProcessInfo(
                processId,
                RuntimeVersion: null,
                CommandLine: path is null ? null : Path.GetFileName(path),
                OperatingSystem: OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsLinux() ? "Linux" : OperatingSystem.IsMacOS() ? "macOS" : "Unknown",
                Architecture: System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString()));
        }
        catch (Exception ex)
        {
            return ToolResult<RuntimeProcessInfo>.Fail("DOTNET_RUNTIME_INFO_FAILED", Safe(ex.Message));
        }
    }

    public async Task<ToolResult<IReadOnlyList<RuntimeCounterObservation>>> CaptureCountersAsync(int processId, int durationMs, CancellationToken cancellationToken = default)
    {
        durationMs = Math.Clamp(durationMs, 1000, 30_000);
        var allowed = processGuard.RequireAllowed(processId);
        if (!allowed.Success) return ToolResult<IReadOnlyList<RuntimeCounterObservation>>.Fail(allowed.Error!.Code, allowed.Error.Message, allowed.Error.Retryable, allowed.Error.Remediation);
        allowed.Value?.Dispose();

        EventPipeSession? session = null;
        EventPipeEventSource? source = null;
        try
        {
            var provider = new EventPipeProvider("System.Runtime", EventLevel.Informational, 0,
                new Dictionary<string, string> { ["EventCounterIntervalSec"] = "1" });
            session = new DiagnosticsClient(processId).StartEventPipeSession(provider, requestRundown: false, circularBufferMB: 16);
            source = new EventPipeEventSource(session.EventStream);
            var latest = new ConcurrentDictionary<string, RuntimeCounterObservation>(StringComparer.Ordinal);
            source.Dynamic.All += data =>
            {
                if (!string.Equals(data.EventName, "EventCounters", StringComparison.Ordinal)) return;
                try
                {
                    if (data.PayloadValue(0) is not IDictionary<string, object> envelope ||
                        !envelope.TryGetValue("Payload", out var payloadObj) ||
                        payloadObj is not IDictionary<string, object> payload ||
                        !payload.TryGetValue("Name", out var nameObj)) return;
                    var name = nameObj?.ToString();
                    if (string.IsNullOrWhiteSpace(name)) return;
                    var kind = payload.ContainsKey("Mean") ? "Mean" : payload.ContainsKey("Increment") ? "Increment" : "Value";
                    var valueObj = payload.TryGetValue("Mean", out var mean) ? mean : payload.TryGetValue("Increment", out var increment) ? increment : null;
                    if (valueObj is null || !double.TryParse(valueObj.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) return;
                    var unit = payload.TryGetValue("DisplayUnits", out var unitObj) ? unitObj?.ToString() : null;
                    latest[name] = new RuntimeCounterObservation(name, value, unit, kind, DateTimeOffset.UtcNow);
                }
                catch { }
            };
            var processing = Task.Run(() => { try { source.Process(); } catch { } }, CancellationToken.None);
            await Task.Delay(durationMs, cancellationToken).ConfigureAwait(false);
            try { session.Stop(); } catch { }
            await processing.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
            return ToolResult<IReadOnlyList<RuntimeCounterObservation>>.Ok(latest.Values.OrderBy(x => x.Name, StringComparer.Ordinal).ToArray());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ToolResult<IReadOnlyList<RuntimeCounterObservation>>.Fail("OPERATION_CANCELLED", "Counter capture was cancelled.", true);
        }
        catch (Exception ex)
        {
            return ToolResult<IReadOnlyList<RuntimeCounterObservation>>.Fail("EVENTCOUNTER_CAPTURE_FAILED", Safe(ex.Message), true);
        }
        finally
        {
            try { session?.Stop(); } catch { }
            source?.Dispose();
            session?.Dispose();
        }
    }

    public ToolResult<IReadOnlyList<ProcessThreadObservation>> GetThreads(int processId, int maxThreads = 256)
    {
        var allowed = processGuard.RequireAllowed(processId);
        if (!allowed.Success || allowed.Value is null) return ToolResult<IReadOnlyList<ProcessThreadObservation>>.Fail(allowed.Error!.Code, allowed.Error.Message, allowed.Error.Retryable, allowed.Error.Remediation);
        using var process = allowed.Value;
        maxThreads = Math.Clamp(maxThreads, 1, 2_000);
        try
        {
            var list = new List<ProcessThreadObservation>();
            foreach (ProcessThread thread in process.Threads)
            {
                if (list.Count >= maxThreads) break;
                string? wait = null;
                try { if (thread.ThreadState == System.Diagnostics.ThreadState.Wait) wait = thread.WaitReason.ToString(); } catch { }
                list.Add(new ProcessThreadObservation(thread.Id, thread.ThreadState.ToString(), wait, thread.BasePriority));
            }
            return ToolResult<IReadOnlyList<ProcessThreadObservation>>.Ok(list);
        }
        catch (Exception ex) { return ToolResult<IReadOnlyList<ProcessThreadObservation>>.Fail("THREAD_ENUMERATION_FAILED", Safe(ex.Message)); }
    }

    public ToolResult<IReadOnlyList<ProcessModuleObservation>> GetModules(int processId, int maxModules = 512)
    {
        var allowed = processGuard.RequireAllowed(processId);
        if (!allowed.Success || allowed.Value is null) return ToolResult<IReadOnlyList<ProcessModuleObservation>>.Fail(allowed.Error!.Code, allowed.Error.Message, allowed.Error.Retryable, allowed.Error.Remediation);
        using var process = allowed.Value;
        maxModules = Math.Clamp(maxModules, 1, 5_000);
        try
        {
            var names = new List<ProcessModuleObservation>();
            foreach (ProcessModule module in process.Modules)
            {
                if (names.Count >= maxModules) break;
                names.Add(new ProcessModuleObservation(Safe(module.ModuleName)));
            }
            return ToolResult<IReadOnlyList<ProcessModuleObservation>>.Ok(names);
        }
        catch (Exception ex) { return ToolResult<IReadOnlyList<ProcessModuleObservation>>.Fail("MODULE_ENUMERATION_FAILED", Safe(ex.Message)); }
    }

    public async Task<ToolResult<IReadOnlyList<ExceptionObservation>>> CaptureExceptionsAsync(int processId, int durationMs, CancellationToken cancellationToken = default)
    {
        durationMs = Math.Clamp(durationMs, 100, 30_000);
        var queue = new ConcurrentQueue<ExceptionObservation>();
        var result = await CaptureExceptionsDuringAsync(processId, async token =>
        {
            await Task.Delay(durationMs, token).ConfigureAwait(false);
            return true;
        }, queue, 0, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
            return ToolResult<IReadOnlyList<ExceptionObservation>>.Fail(result.Error!.Code, result.Error.Message, result.Error.Retryable);
        return ToolResult<IReadOnlyList<ExceptionObservation>>.Ok(queue.ToArray());
    }

    public async Task<ToolResult<DiagnosticActionResult<T>>> CaptureExceptionsDuringAsync<T>(
        int processId,
        Func<CancellationToken, Task<T>> action,
        ConcurrentQueue<ExceptionObservation> exceptions,
        int postActionObservationMs = 0,
        CancellationToken cancellationToken = default)
    {
        var allowed = processGuard.RequireAllowed(processId);
        if (!allowed.Success)
            return ToolResult<DiagnosticActionResult<T>>.Fail(allowed.Error!.Code, allowed.Error.Message, allowed.Error.Retryable, allowed.Error.Remediation);
        allowed.Value?.Dispose();

        postActionObservationMs = Math.Clamp(postActionObservationMs, 0, 10_000);
        EventPipeSession? session = null;
        EventPipeEventSource? source = null;
        Task? processing = null;
        string? captureWarning = null;

        // Diagnostics are best-effort. A failure to start EventPipe must never cause the caller
        // to execute an already-authorized UI action twice.
        try
        {
            var providers = new[]
            {
                new EventPipeProvider(
                    ClrTraceEventParser.ProviderName,
                    EventLevel.Informational,
                    (long)(ClrTraceEventParser.Keywords.Exception | ClrTraceEventParser.Keywords.Stack))
            };
            var client = new DiagnosticsClient(processId);
            session = client.StartEventPipeSession(providers, requestRundown: false, circularBufferMB: 32);
            source = new EventPipeEventSource(session.EventStream);
            source.Clr.ExceptionStart += data =>
            {
                var type = Safe(data.ExceptionType ?? "UnknownException");
                var message = Safe(data.ExceptionMessage ?? string.Empty);
                exceptions.Enqueue(new ExceptionObservation(
                    DateTimeOffset.UtcNow,
                    type,
                    message,
                    StackTrace: null,
                    processId,
                    "EventPipe:Microsoft-Windows-DotNETRuntime/ExceptionStart"));
            };
            processing = Task.Run(() =>
            {
                try { source.Process(); }
                catch { }
            }, CancellationToken.None);
        }
        catch (Exception)
        {
            captureWarning = "EVENTPIPE_CAPTURE_UNAVAILABLE";
            try { source?.Dispose(); } catch { }
            try { session?.Dispose(); } catch { }
            source = null;
            session = null;
            processing = null;
        }

        var captureWasAvailable = session is not null;
        T actionValue;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            actionValue = await action(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await DisposeCaptureAsync(session, source, processing).ConfigureAwait(false);
            return ToolResult<DiagnosticActionResult<T>>.Fail("OPERATION_CANCELLED", "Action was cancelled before a result was returned.", true);
        }
        catch (Exception ex)
        {
            await DisposeCaptureAsync(session, source, processing).ConfigureAwait(false);
            return ToolResult<DiagnosticActionResult<T>>.Fail("ACTION_EXECUTION_FAILED", Safe(ex.Message), false);
        }

        // At this point the action has executed exactly once. Cancellation/teardown after this point
        // must not turn the result into a retry signal that could repeat a destructive operation.
        if (session is not null && postActionObservationMs > 0)
        {
            try { await Task.Delay(postActionObservationMs, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                captureWarning ??= "POST_ACTION_OBSERVATION_CANCELLED";
            }
        }

        try { session?.Stop(); }
        catch { captureWarning ??= "EVENTPIPE_TEARDOWN_INCOMPLETE"; }
        if (processing is not null)
        {
            try { await processing.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false); }
            catch { captureWarning ??= "EVENTPIPE_PROCESSING_INCOMPLETE"; }
        }

        try { source?.Dispose(); } catch { }
        try { session?.Dispose(); } catch { }

        return ToolResult<DiagnosticActionResult<T>>.Ok(new DiagnosticActionResult<T>(actionValue, captureWasAvailable, captureWarning));
    }

    private static async Task DisposeCaptureAsync(EventPipeSession? session, EventPipeEventSource? source, Task? processing)
    {
        try { session?.Stop(); } catch { }
        if (processing is not null)
        {
            try { await processing.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false); }
            catch { }
        }
        try { source?.Dispose(); } catch { }
        try { session?.Dispose(); } catch { }
    }

    public ToolResult<TraceHandle> StartTrace(int processId)
    {
        var allowed = processGuard.RequireAllowed(processId);
        if (!allowed.Success)
            return ToolResult<TraceHandle>.Fail(allowed.Error!.Code, allowed.Error.Message, allowed.Error.Retryable, allowed.Error.Remediation);
        allowed.Value?.Dispose();

        if (_traces.Count >= MaxActiveTraces)
            return ToolResult<TraceHandle>.Fail("TRACE_LIMIT_REACHED", $"At most {MaxActiveTraces} traces may be active at once.");

        try
        {
            var id = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12));
            var directory = GetSecureTraceDirectory();
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"trace-{id}.nettrace");
            var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);
            var providers = new[]
            {
                new EventPipeProvider(ClrTraceEventParser.ProviderName, EventLevel.Informational,
                    (long)(ClrTraceEventParser.Keywords.Exception | ClrTraceEventParser.Keywords.GC | ClrTraceEventParser.Keywords.Threading | ClrTraceEventParser.Keywords.Contention))
            };
            var session = new DiagnosticsClient(processId).StartEventPipeSession(providers, requestRundown: true, circularBufferMB: 64);
            var copyTask = CopyBoundedTraceAsync(session, file);
            var active = new ActiveTrace { Id = id, ProcessId = processId, Path = path, StartedAtUtc = DateTimeOffset.UtcNow, Session = session, File = file, CopyTask = copyTask };
            if (!_traces.TryAdd(id, active))
            {
                active.DisposeAsync().AsTask().GetAwaiter().GetResult();
                return ToolResult<TraceHandle>.Fail("TRACE_ID_COLLISION", "Could not allocate a unique trace identifier.");
            }
            _ = AutoStopTraceAsync(id);
            return ToolResult<TraceHandle>.Ok(new TraceHandle(id, processId, "[LOCAL-REDACTED]", active.StartedAtUtc, "running"));
        }
        catch (Exception ex)
        {
            return ToolResult<TraceHandle>.Fail("TRACE_START_FAILED", Safe(ex.Message), true);
        }
    }

    public async Task<ToolResult<TraceHandle>> StopTraceAsync(string traceId, CancellationToken cancellationToken = default)
    {
        if (!_traces.TryRemove(traceId, out var active))
            return ToolResult<TraceHandle>.Fail("TRACE_NOT_FOUND", "Trace identifier is unknown, already stopped, or expired after the 30-second safety window.");
        try
        {
            await active.DisposeAsync().ConfigureAwait(false);
            return ToolResult<TraceHandle>.Ok(new TraceHandle(traceId, active.ProcessId, "[LOCAL-REDACTED]", active.StartedAtUtc, "stopped"));
        }
        catch (Exception ex)
        {
            return ToolResult<TraceHandle>.Fail("TRACE_STOP_FAILED", Safe(ex.Message), true);
        }
    }

    private static async Task CopyBoundedTraceAsync(EventPipeSession session, FileStream file)
    {
        var buffer = new byte[64 * 1024];
        long written = 0;
        try
        {
            while (written < MaxTraceBytes)
            {
                var remaining = (int)Math.Min(buffer.Length, MaxTraceBytes - written);
                var read = await session.EventStream.ReadAsync(buffer.AsMemory(0, remaining)).ConfigureAwait(false);
                if (read == 0) break;
                await file.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
                written += read;
            }
            await file.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            try { session.Stop(); } catch { }
        }
    }

    private async Task AutoStopTraceAsync(string traceId)
    {
        try
        {
            await Task.Delay(MaxTraceDuration).ConfigureAwait(false);
            if (_traces.TryRemove(traceId, out var active))
                await active.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // The safety cleanup is best-effort and must not surface as an unobserved task failure.
        }
    }

    private string GetSecureTraceDirectory()
    {
        var configured = policyProvider.Current.Audit.Directory;
        var root = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DotNetEngineeringMcp")
            : Path.GetFullPath(configured);
        return Path.Combine(root, "traces");
    }

    private string Safe(string value) => redactor.Redact(value, policyProvider.Current.Pii);

    public async ValueTask DisposeAsync()
    {
        foreach (var pair in _traces.ToArray())
        {
            if (_traces.TryRemove(pair.Key, out var trace))
                await trace.DisposeAsync().ConfigureAwait(false);
        }
    }
}
