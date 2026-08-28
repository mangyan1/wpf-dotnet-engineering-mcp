using System.Net;
using EngineeringMcp.Host;
using EngineeringMcp.Security;
using EngineeringMcp.Contracts;
using EngineeringMcp.FailureCorrelation;
using EngineeringMcp.Diagnostics;
using EngineeringMcp.Source;
using EngineeringMcp.Wpf;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Server;

var launch = McpHostLaunchOptions.Parse(args);

if (launch.Transport == McpHostTransport.Http)
{
    await RunHttpAsync(args, launch);
}
else
{
    await RunStdioAsync(args);
}

static async Task RunStdioAsync(string[] args)
{
    var builder = Host.CreateApplicationBuilder(args);

    // stdio MCP requires stdout to remain protocol-clean, so logs go to stderr.
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
    builder.Logging.AddProvider(new EngineeringFileLoggerProvider());

    RegisterEngineeringServices(builder.Services);
    builder.Services
        .AddMcpServer()
        .WithStdioServerTransport()
        .WithToolsFromAssembly()
        .AddEngineeringContractFilters();

    await builder.Build().RunAsync();
}

static async Task RunHttpAsync(string[] args, McpHostLaunchOptions launch)
{
    var httpToken = Environment.GetEnvironmentVariable(McpRuntimeDefaults.HttpTokenEnvironmentVariable);
    if (!HttpBearerAuthentication.IsStrongToken(httpToken))
    {
        throw new InvalidOperationException(
            $"HTTP transport requires {McpRuntimeDefaults.HttpTokenEnvironmentVariable} with at least 32 characters. " +
            "Use stdio transport when a bearer token cannot be provided securely.");
    }

    var builder = WebApplication.CreateBuilder(args);
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole();
    builder.Logging.AddProvider(new EngineeringFileLoggerProvider());

    // The shared development service is deliberately loopback-only. It is not a LAN/remote server.
    builder.WebHost.UseUrls(launch.ListenUrl);
    builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 1_048_576);
    builder.Configuration["AllowedHosts"] = "127.0.0.1;localhost;[::1]";

    RegisterEngineeringServices(builder.Services);
    builder.Services
        .AddMcpServer()
        .WithHttpTransport(options => options.Stateless = true)
        .WithToolsFromAssembly()
        .AddEngineeringContractFilters();

    var app = builder.Build();
    var requestGate = new SemaphoreSlim(8, 8);
    var allowedOrigin = new Uri(launch.ListenUrl).GetLeftPart(UriPartial.Authority);
    var clientActivity = app.Services.GetRequiredService<McpClientActivityTracker>();

    // Defense in depth for a local engineering server: reject non-loopback peers and Host headers.
    // No CORS middleware is enabled, so browser origins are not granted access.
    app.Use(async (context, next) =>
    {
        var remote = context.Connection.RemoteIpAddress;
        if (remote is not null && !IPAddress.IsLoopback(remote))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var host = context.Request.Host.Host;
        if (!IsAllowedLoopbackHost(host))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        if (context.Request.ContentLength is > 1_048_576)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }

        var origin = context.Request.Headers.Origin.ToString();
        if (!string.IsNullOrWhiteSpace(origin) && !string.Equals(origin, allowedOrigin, StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        // The concurrency gate is taken before the bearer check so that token-guessing traffic is
        // rate-limited by the same semaphore as authorized traffic.
        if (!await requestGate.WaitAsync(TimeSpan.FromSeconds(2), context.RequestAborted))
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.Headers.RetryAfter = "2";
            return;
        }

        try
        {
            var isProtectedPath = context.Request.Path.StartsWithSegments(McpRuntimeDefaults.McpPath) ||
                                  context.Request.Path.StartsWithSegments(McpRuntimeDefaults.HealthPath);
            if (isProtectedPath && !HttpBearerAuthentication.IsAuthorized(context.Request.Headers.Authorization, httpToken))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers.WWWAuthenticate = "Bearer";
                return;
            }

            context.Response.Headers.CacheControl = "no-store";
            var clientName = context.Request.Headers[McpRuntimeDefaults.ClientNameHeader].ToString();
            if (string.IsNullOrWhiteSpace(clientName) && context.Request.Query.ContainsKey(McpRuntimeDefaults.VsCodeClientQueryFlag))
                clientName = McpRuntimeDefaults.VsCodeClientName;
            using var activityScope = context.Request.Path.StartsWithSegments(McpRuntimeDefaults.McpPath)
                ? clientActivity.BeginRequest(clientName)
                : null;
            using var clientScope = app.Services.GetRequiredService<SessionContext>()
                .BeginClientScope(HttpBearerAuthentication.DeriveClientId(httpToken));
            await next();
        }
        finally
        {
            requestGate.Release();
        }
    });

    var advertisedEndpoint = launch.ListenUrl.TrimEnd('/') + McpRuntimeDefaults.McpPath;

    app.MapGet(McpRuntimeDefaults.HealthPath, () =>
    {
        var activity = clientActivity.Snapshot();
        return Results.Json(new
        {
            status = "ok",
            server = McpRuntimeDefaults.ServerName,
            transport = "streamable-http",
            endpoint = advertisedEndpoint,
            processId = Environment.ProcessId,
            vsCodeActive = activity.VsCodeActive,
            lastVsCodeActivityUtc = activity.LastVsCodeActivityUtc
        });
    });

    app.MapMcp(McpRuntimeDefaults.McpPath);

    Console.WriteLine($"Engineering MCP Streamable HTTP listening on {advertisedEndpoint}");
    await app.RunAsync();
}

static bool IsAllowedLoopbackHost(string host)
    => string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
       || string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
       || string.Equals(host, "::1", StringComparison.OrdinalIgnoreCase)
       || string.Equals(host, "[::1]", StringComparison.OrdinalIgnoreCase);

static void RegisterEngineeringServices(IServiceCollection services)
{
    services.AddSingleton<FilePolicyProvider>();
    services.AddSingleton<PolicyEngine>();
    services.AddSingleton<ToolGate>();
    services.AddSingleton<ProcessGuard>();
    services.AddSingleton<FileGuard>();
    services.AddSingleton<UiActionRiskClassifier>();
    services.AddSingleton<RedactionService>();
    services.AddSingleton<SessionContext>();
    services.AddSingleton<IAuditSink>(sp =>
    {
        var policy = sp.GetRequiredService<FilePolicyProvider>().Current;
        if (!policy.Audit.Enabled) return new NullAuditSink();
        var directory = string.IsNullOrWhiteSpace(policy.Audit.Directory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DotNetEngineeringMcp", "audit")
            : Path.GetFullPath(policy.Audit.Directory);
        return new JsonLinesAuditSink(directory, policy.Audit.RetentionDays);
    });

    services.AddSingleton<CapabilityRegistry>();
    services.AddSingleton<McpClientActivityTracker>();
    services.AddSingleton<ProcessOperationCoordinator>();
    services.AddSingleton<ToolAuthorization>();
    services.AddSingleton<WpfAutomationService>();
    services.AddSingleton<WpfSafeInspectionService>();
    services.AddSingleton<WpfProbeClient>();
    services.AddSingleton<WpfUiInspectionService>();
    services.AddSingleton<UiAuditService>();
    services.AddSingleton<DotNetDiagnosticsService>();
    services.AddSingleton<ClrMdService>();
    services.AddSingleton<SourceIntelligenceService>();
    services.AddSingleton<BackendProbeClient>();
    services.AddSingleton<DiagnosisService>();
}

internal enum McpHostTransport
{
    Stdio,
    Http
}

internal sealed record McpHostLaunchOptions(McpHostTransport Transport, string ListenUrl)
{
    public static McpHostLaunchOptions Parse(IReadOnlyList<string> args)
    {
        var transport = McpHostTransport.Stdio;
        var listenUrl = McpRuntimeDefaults.ListenUrl;

        for (var i = 0; i < args.Count; i++)
        {
            if (string.Equals(args[i], "--transport", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count)
            {
                var value = args[++i];
                transport = value.ToLowerInvariant() switch
                {
                    "stdio" => McpHostTransport.Stdio,
                    "http" => McpHostTransport.Http,
                    _ => throw new ArgumentException("--transport must be 'stdio' or 'http'.")
                };
                continue;
            }

            if (string.Equals(args[i], "--url", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count)
                listenUrl = ValidateLoopbackUrl(args[++i]);
        }

        return new McpHostLaunchOptions(transport, listenUrl);
    }

    private static string ValidateLoopbackUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
            !(string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
              string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
              string.Equals(uri.Host, "::1", StringComparison.OrdinalIgnoreCase)) ||
            uri.Port is < 1024 or > 65535)
        {
            throw new ArgumentException("HTTP MCP listen URL must be an explicit loopback http:// URL on a non-privileged port.");
        }

        return uri.GetLeftPart(UriPartial.Authority);
    }
}
