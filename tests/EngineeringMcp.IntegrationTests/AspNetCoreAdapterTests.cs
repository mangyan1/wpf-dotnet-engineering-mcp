using System.Diagnostics;
using System.Text.Json;
using EngineeringMcp.Contracts;
using EngineeringMcp.Diagnostics;
using EngineeringMcp.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EngineeringMcp.IntegrationTests;

[TestClass]
public sealed class AspNetCoreAdapterTests
{
    [TestMethod]
    [DoNotParallelize]
    public async Task Adapter_AuthenticatesAndReturnsLiveActionCorrelatedRequestMetadata()
    {
        const string token = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var previousToken = Environment.GetEnvironmentVariable("ENGINEERING_MCP_BACKEND_TOKEN");
        Environment.SetEnvironmentVariable("ENGINEERING_MCP_BACKEND_TOKEN", token);
        await using var app = BuildFixtureApplication(token);
        try
        {
            await app.StartAsync();
            using var process = Process.GetCurrentProcess();
            var path = process.MainModule?.FileName ?? throw new AssertFailedException("Current test process path was unavailable.");
            var policy = McpPolicy.LockedDownDefault with
            {
                Processes = new ProcessPolicy([new AllowedProcessRule(process.ProcessName, path)])
            };
            var provider = new FixedPolicyProvider(policy);
            var redactor = new RedactionService();
            var client = new BackendProbeClient(new ProcessGuard(provider), redactor, provider);
            const string correlationId = "abcdefabcdefabcdefabcdefabcdefab";

            var health = await client.RequestAsync(process.Id, "health");
            var begin = await client.RequestAsync(process.Id, "begin_correlation", correlationId: correlationId);
            var marker = ReadValue<BackendCorrelationObservation>(begin.Value?.Value);
            using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            using var response = await http.GetAsync("/adapter-test");
            var correlated = await client.RequestAsync(
                process.Id, "correlated", correlationId: correlationId, afterSequence: marker?.AfterSequence);
            var observations = ReadValue<List<BackendRequestObservation>>(correlated.Value?.Value);

            Assert.IsTrue(health.Success && health.Value?.Success == true, health.Error?.Message ?? health.Value?.ErrorMessage);
            Assert.IsNotNull(marker);
            Assert.AreEqual(StatusCodes.Status204NoContent, (int)response.StatusCode);
            Assert.IsNotNull(observations);
            Assert.IsTrue(observations.Any(item => item.Path == "/adapter-test" && item.CorrelationId == correlationId));
        }
        finally
        {
            await app.StopAsync();
            Environment.SetEnvironmentVariable("ENGINEERING_MCP_BACKEND_TOKEN", previousToken);
        }
    }

    [TestMethod]
    public async Task Middleware_CorrelatesOnlyRequestsInsideExplicitActionMarker()
    {
        var buffer = new BackendObservationBuffer(20);
        var correlation = new BackendActionCorrelation();
        var middleware = new EngineeringMcpBackendMiddleware(
            context =>
            {
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return Task.CompletedTask;
            },
            buffer,
            correlation,
            new RedactionService());

        Assert.IsTrue(correlation.TryBegin("0123456789abcdef0123456789abcdef"));
        var context = new DefaultHttpContext();
        context.SetEndpoint(new RouteEndpoint(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse("/api/repair-orders/{id}"),
            order: 0,
            EndpointMetadataCollection.Empty,
            displayName: "repair-order"));

        await middleware.InvokeAsync(context);

        var observed = buffer.Correlated("0123456789abcdef0123456789abcdef", 0, 10);
        Assert.HasCount(1, observed);
        Assert.AreEqual("/api/repair-orders/{id}", observed[0].Path);
        Assert.AreEqual(StatusCodes.Status204NoContent, observed[0].StatusCode);
        Assert.IsGreaterThan(0, observed[0].Sequence);
    }

    [TestMethod]
    public void ActionCorrelation_RejectsConcurrentMarkerAndExpiresByExplicitEnd()
    {
        var correlation = new BackendActionCorrelation();
        Assert.IsTrue(correlation.TryBegin("aaaaaaaaaaaaaaaa"));
        Assert.IsFalse(correlation.TryBegin("bbbbbbbbbbbbbbbb"));

        correlation.End("aaaaaaaaaaaaaaaa");

        Assert.IsTrue(correlation.TryBegin("bbbbbbbbbbbbbbbb"));
        Assert.AreEqual("bbbbbbbbbbbbbbbb", correlation.Current);
    }

    private static WebApplication BuildFixtureApplication(string token)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddEngineeringMcpBackendDiagnostics(new EngineeringMcpBackendOptions(Token: token));
        var app = builder.Build();
        app.UseEngineeringMcpBackendDiagnostics();
        app.MapGet("/adapter-test", () => Results.NoContent());
        return app;
    }

    private static T? ReadValue<T>(object? value)
    {
        if (value is JsonElement element)
            return element.Deserialize<T>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return value is T typed ? typed : default;
    }

    private sealed class FixedPolicyProvider(McpPolicy policy) : FilePolicyProvider
    {
        public override McpPolicy Current { get; } = policy;
        public override string Source => "test";
    }
}
