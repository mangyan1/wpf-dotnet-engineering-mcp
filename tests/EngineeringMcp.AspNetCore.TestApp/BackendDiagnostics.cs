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

namespace EngineeringMcp.AspNetCore.TestApp;

public sealed record EngineeringMcpBackendOptions(
    int Capacity = 500,
    string? Token = null,
    string? PipeName = null);

public sealed class BackendObservationBuffer
{
    private readonly ConcurrentQueue<BackendRequestObservation> _queue = new();
    private readonly int _capacity;
    public BackendObservationBuffer(int capacity) => _capacity = Math.Clamp(capacity, 10, 10_000);

    public void Add(BackendRequestObservation item)
    {
        _queue.Enqueue(item);
        while (_queue.Count > _capacity && _queue.TryDequeue(out _)) { }
    }

    public IReadOnlyList<BackendRequestObservation> Recent(int limit)
        => _queue.Reverse().Take(Math.Clamp(limit, 1, 1_000)).Reverse().ToArray();

    public int Count => _queue.Count;
}

public sealed class EngineeringMcpBackendMiddleware(
    RequestDelegate next,
    BackendObservationBuffer buffer,
    RedactionService redactor)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var started = Stopwatch.GetTimestamp();
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
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var endpoint = context.GetEndpoint();
            var route = endpoint is RouteEndpoint routeEndpoint
                ? routeEndpoint.RoutePattern.RawText ?? routeEndpoint.DisplayName ?? "<route>"
                : endpoint?.DisplayName ?? "<unmatched>";

            // Never collect query strings, request/response bodies, cookies, authorization headers, or arbitrary headers.
            buffer.Add(new BackendRequestObservation(
                DateTimeOffset.UtcNow,
                context.Request.Method,
                route,
                captured is null ? context.Response.StatusCode : Math.Max(500, context.Response.StatusCode),
                elapsed,
                Activity.Current?.TraceId.ToString(),
                captured?.GetType().FullName,
                captured is null ? null : redactor.Redact(captured.Message),
                captured is null ? null : Truncate(redactor.Redact(captured.StackTrace ?? string.Empty), 16_384)));
        }
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max] + "…";
}

public sealed class BackendProbeHostedService(
    BackendObservationBuffer buffer,
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
        if (Token.Length < 32) return; // Fail closed: adapter remains unavailable without a strong shared token.

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
                        .AsTask()
                        .WaitAsync(RequestTimeout, stoppingToken)
                        .ConfigureAwait(false);
                    response = request is null ? Fail("INVALID_REQUEST", "Request was empty.") : Dispatch(request);
                }
                catch (TimeoutException) { response = Fail("REQUEST_TIMEOUT", "Backend probe request was not received within 5 seconds."); }
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
            "health" => new BackendProbeResponse(true, new BackendHealthObservation("ready", DateTimeOffset.UtcNow, buffer.Count, typeof(BackendProbeHostedService).Assembly.GetName().Version?.ToString() ?? "dev")),
            "recent" => new BackendProbeResponse(true, buffer.Recent(request.Limit)),
            "exceptions" => new BackendProbeResponse(true, buffer.Recent(request.Limit).Where(x => x.ExceptionType is not null).ToArray()),
            _ => Fail("OPERATION_NOT_ALLOWED", "Backend probe operation is not in the allowlist.")
        };
    }

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
        services.AddSingleton<RedactionService, RedactionService>();
        services.AddHostedService<BackendProbeHostedService>();
        return services;
    }

    public static IApplicationBuilder UseEngineeringMcpBackendDiagnostics(this IApplicationBuilder app)
        => app.UseMiddleware<EngineeringMcpBackendMiddleware>();
}
