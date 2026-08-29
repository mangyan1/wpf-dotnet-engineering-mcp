using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EngineeringMcp.Contracts;
using EngineeringMcp.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace EngineeringMcp.Diagnostics;

public sealed record EngineeringMcpBackendOptions(
    int Capacity = 500,
    string? Token = null,
    string? PipeName = null);

public sealed class BackendObservationBuffer
{
    private readonly ConcurrentQueue<BackendRequestObservation> _queue = new();
    private readonly int _capacity;
    private long _sequence;

    public BackendObservationBuffer(int capacity) => _capacity = Math.Clamp(capacity, 10, 10_000);

    public void Add(BackendRequestObservation item)
    {
        _queue.Enqueue(item with { Sequence = Interlocked.Increment(ref _sequence) });
        while (_queue.Count > _capacity && _queue.TryDequeue(out _)) { }
    }

    public IReadOnlyList<BackendRequestObservation> Recent(int limit)
        => _queue.Reverse().Take(Math.Clamp(limit, 1, 1_000)).Reverse().ToArray();

    public IReadOnlyList<BackendRequestObservation> Correlated(string correlationId, long afterSequence, int limit)
        => _queue.Where(item => item.Sequence > afterSequence && string.Equals(item.CorrelationId, correlationId, StringComparison.Ordinal))
            .TakeLast(Math.Clamp(limit, 1, 1_000)).ToArray();

    public int Count => _queue.Count;
    public long LastSequence => Interlocked.Read(ref _sequence);
}

public sealed class BackendActionCorrelation
{
    private readonly object _sync = new();
    private string? _correlationId;
    private DateTimeOffset _expiresAtUtc;

    public bool TryBegin(string correlationId)
    {
        lock (_sync)
        {
            if (_correlationId is not null && _expiresAtUtc > DateTimeOffset.UtcNow &&
                !string.Equals(_correlationId, correlationId, StringComparison.Ordinal))
                return false;
            _correlationId = correlationId;
            _expiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(30);
            return true;
        }
    }

    public string? Current
    {
        get
        {
            lock (_sync)
            {
                if (_expiresAtUtc <= DateTimeOffset.UtcNow) _correlationId = null;
                return _correlationId;
            }
        }
    }

    public void End(string correlationId)
    {
        lock (_sync)
        {
            if (string.Equals(_correlationId, correlationId, StringComparison.Ordinal))
                _correlationId = null;
        }
    }
}

public sealed class EngineeringMcpBackendMiddleware(
    RequestDelegate next,
    BackendObservationBuffer buffer,
    BackendActionCorrelation correlation,
    RedactionService redactor)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var started = Stopwatch.GetTimestamp();
        var actionCorrelationId = correlation.Current;
        Exception? captured = null;
        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            captured = ex;
            throw;
        }
        finally
        {
            var endpoint = context.GetEndpoint();
            var route = endpoint is RouteEndpoint routeEndpoint
                ? routeEndpoint.RoutePattern.RawText ?? routeEndpoint.DisplayName ?? "<route>"
                : endpoint?.DisplayName ?? "<unmatched>";
            buffer.Add(new BackendRequestObservation(
                DateTimeOffset.UtcNow,
                context.Request.Method,
                route,
                captured is null ? context.Response.StatusCode : Math.Max(500, context.Response.StatusCode),
                Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                Activity.Current?.TraceId.ToString(),
                captured?.GetType().FullName,
                captured is null ? null : redactor.Redact(captured.Message),
                captured is null ? null : Truncate(redactor.Redact(captured.StackTrace ?? string.Empty), 16_384),
                actionCorrelationId));
        }
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max] + "…";
}

public sealed class BackendProbeHostedService(
    BackendObservationBuffer buffer,
    BackendActionCorrelation correlation,
    EngineeringMcpBackendOptions options) : BackgroundService
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
    private const int MaxRequestBytes = 32 * 1024;
    private const int MaxResponseBytes = 2 * 1024 * 1024;
    private readonly RedactionService _redactor = new();
    private string PipeName => options.PipeName ?? $"EngineeringMcp.AspNetProbe.{Environment.ProcessId}";
    private string Token => options.Token ?? Environment.GetEnvironmentVariable("ENGINEERING_MCP_BACKEND_TOKEN") ?? string.Empty;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (Token.Length < 32) return;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);
                BackendProbeResponse response;
                try
                {
                    var request = await BoundedJsonPipeProtocol.ReadAsync<BackendProbeRequest>(pipe, MaxRequestBytes, stoppingToken)
                        .AsTask().WaitAsync(RequestTimeout, stoppingToken).ConfigureAwait(false);
                    response = request is null ? Fail("INVALID_REQUEST", "Request was empty.") : Dispatch(request);
                }
                catch (TimeoutException) { response = Fail("REQUEST_TIMEOUT", "Backend probe request was not received within five seconds."); }
                catch (JsonException) { response = Fail("INVALID_JSON", "Request JSON was invalid."); }
                catch (Exception ex) { response = Fail("BACKEND_PROBE_ERROR", _redactor.Redact(ex.Message)); }
                await BoundedJsonPipeProtocol.WriteAsync(pipe, response, MaxResponseBytes, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch { await Task.Delay(100, stoppingToken).ConfigureAwait(false); }
        }
    }

    private BackendProbeResponse Dispatch(BackendProbeRequest request)
    {
        if (!FixedTimeEquals(request.Token, Token)) return Fail("AUTH_FAILED", "Backend probe authentication failed.");
        return request.Operation switch
        {
            "health" => new BackendProbeResponse(true, new BackendHealthObservation(
                "ready", DateTimeOffset.UtcNow, buffer.Count,
                typeof(BackendProbeHostedService).Assembly.GetName().Version?.ToString() ?? "dev",
                Environment.ProcessId, ActionCorrelationSupported: true)),
            "recent" => new BackendProbeResponse(true, buffer.Recent(request.Limit)),
            "exceptions" => new BackendProbeResponse(true, buffer.Recent(request.Limit).Where(item => item.ExceptionType is not null).ToArray()),
            "begin_correlation" => BeginCorrelation(request),
            "correlated" => Correlated(request),
            "end_correlation" => EndCorrelation(request),
            _ => Fail("OPERATION_NOT_ALLOWED", "Backend probe operation is not in the allowlist.")
        };
    }

    private BackendProbeResponse BeginCorrelation(BackendProbeRequest request)
    {
        if (!IsValidCorrelationId(request.CorrelationId))
            return Fail("CORRELATION_ID_INVALID", "A 16-64 character hexadecimal correlation identifier is required.");
        if (!correlation.TryBegin(request.CorrelationId!))
            return Fail("CORRELATION_BUSY", "Another diagnostic action correlation is active for this backend.");
        return new BackendProbeResponse(true, new BackendCorrelationObservation(request.CorrelationId!, buffer.LastSequence, DateTimeOffset.UtcNow));
    }

    private BackendProbeResponse Correlated(BackendProbeRequest request)
        => !IsValidCorrelationId(request.CorrelationId) || request.AfterSequence is null
            ? Fail("CORRELATION_ARGUMENT_REQUIRED", "CorrelationId and afterSequence are required.")
            : new BackendProbeResponse(true, buffer.Correlated(request.CorrelationId!, request.AfterSequence.Value, request.Limit));

    private BackendProbeResponse EndCorrelation(BackendProbeRequest request)
    {
        if (!IsValidCorrelationId(request.CorrelationId))
            return Fail("CORRELATION_ID_INVALID", "A valid correlation identifier is required.");
        correlation.End(request.CorrelationId!);
        return new BackendProbeResponse(true, new { ended = true });
    }

    private static bool IsValidCorrelationId(string? value)
        => value is { Length: >= 16 and <= 64 } && value.All(character => char.IsAsciiHexDigit(character));

    private static BackendProbeResponse Fail(string code, string message) => new(false, ErrorCode: code, ErrorMessage: message);

    private static bool FixedTimeEquals(string supplied, string expected)
    {
        var a = Encoding.UTF8.GetBytes(supplied ?? string.Empty);
        var b = Encoding.UTF8.GetBytes(expected);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}

public static class EngineeringMcpBackendExtensions
{
    public static IServiceCollection AddEngineeringMcpBackendDiagnostics(
        this IServiceCollection services,
        EngineeringMcpBackendOptions? options = null)
    {
        options ??= new EngineeringMcpBackendOptions();
        services.AddSingleton(options);
        services.AddSingleton(new BackendObservationBuffer(options.Capacity));
        services.AddSingleton<BackendActionCorrelation>();
        services.AddSingleton<RedactionService>();
        services.AddHostedService<BackendProbeHostedService>();
        return services;
    }

    public static IApplicationBuilder UseEngineeringMcpBackendDiagnostics(this IApplicationBuilder app)
        => app.UseMiddleware<EngineeringMcpBackendMiddleware>();
}
