using System.Net;
using EngineeringMcp.AspNetCore;
using EngineeringMcp.Audit;
using EngineeringMcp.Contracts;
using EngineeringMcp.Diagnosis;
using EngineeringMcp.Diagnostics;
using EngineeringMcp.Host;
using EngineeringMcp.Redaction;
using EngineeringMcp.Security;
using EngineeringMcp.Source;
using EngineeringMcp.Wpf;
using EngineeringMcp.Wpf.WpfUi;
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

        if (context.Request.Path.StartsWithSegments(McpRuntimeDefaults.McpPath) &&
            !HttpBearerAuthentication.IsAuthorized(context.Request.Headers.Authorization, httpToken))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.WWWAuthenticate = "Bearer";
            return;
        }

        if (!await requestGate.WaitAsync(TimeSpan.FromSeconds(2), context.RequestAborted))
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.Headers.RetryAfter = "2";
            return;
        }

        try
        {
            context.Response.Headers.CacheControl = "no-store";
            using var clientScope = app.Services.GetRequiredService<ISessionContext>()
                .BeginClientScope(HttpBearerAuthentication.DeriveClientId(httpToken));
            await next();
        }
        finally
        {
            requestGate.Release();
        }
    });

    var advertisedEndpoint = launch.ListenUrl.TrimEnd('/') + McpRuntimeDefaults.McpPath;

    app.MapGet(McpRuntimeDefaults.HealthPath, () => Results.Json(new
    {
        status = "ok",
        server = McpRuntimeDefaults.ServerName,
        transport = "streamable-http",
        endpoint = advertisedEndpoint,
        processId = Environment.ProcessId
    }));

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
    services.AddSingleton<IPolicyProvider, FilePolicyProvider>();
    services.AddSingleton<IPolicyEngine, PolicyEngine>();
    services.AddSingleton<IToolGate, ToolGate>();
    services.AddSingleton<IProcessGuard, ProcessGuard>();
    services.AddSingleton<IFileGuard, FileGuard>();
    services.AddSingleton<IUiActionRiskClassifier, UiActionRiskClassifier>();
    services.AddSingleton<IRedactionService, RedactionService>();
    services.AddSingleton<ISessionContext, SessionContext>();
    services.AddSingleton<IAuditSink>(sp =>
    {
        var policy = sp.GetRequiredService<IPolicyProvider>().Current;
        if (!policy.Audit.Enabled) return new NullAuditSink();
        var directory = string.IsNullOrWhiteSpace(policy.Audit.Directory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DotNetEngineeringMcp", "audit")
            : Path.GetFullPath(policy.Audit.Directory);
        return new JsonLinesAuditSink(directory, policy.Audit.RetentionDays);
    });

    services.AddSingleton<ICapabilityRegistry, CapabilityRegistry>();
    services.AddSingleton<IProcessOperationCoordinator, ProcessOperationCoordinator>();
    services.AddSingleton<IToolAuthorization, ToolAuthorization>();
    services.AddSingleton<IWpfAutomationService, WpfAutomationService>();
    services.AddSingleton<IWpfProbeClient, WpfProbeClient>();
    services.AddSingleton<IWpfUiInspectionService, WpfUiInspectionService>();
    services.AddSingleton<IUiAuditService, UiAuditService>();
    services.AddSingleton<IDotNetDiagnosticsService, DotNetDiagnosticsService>();
    services.AddSingleton<IClrMdService, ClrMdService>();
    services.AddSingleton<ISourceIntelligenceService, SourceIntelligenceService>();
    services.AddSingleton<IBackendProbeClient, BackendProbeClient>();
    services.AddSingleton<IDiagnosisService, DiagnosisService>();
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
