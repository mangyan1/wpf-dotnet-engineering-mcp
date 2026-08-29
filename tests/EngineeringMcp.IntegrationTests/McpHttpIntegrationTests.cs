using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using EngineeringMcp.Contracts;
using EngineeringMcp.Security;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EngineeringMcp.IntegrationTests;

[TestClass]
public sealed partial class McpHttpIntegrationTests
{
    [TestMethod]
    public async Task LiveHttpHost_RequiresBearerAndPublishesPortableToolNames()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("The WPF engineering host is Windows-only.");
            return;
        }

        var root = TestRepositoryLocator.FindRoot();
        var configuration =
#if DEBUG
            "Debug";
#else
            "Release";
#endif
        var executable = TestArtifactLocator.FindHostExecutable(
            root,
            configuration,
            "net10.0-windows10.0.19041.0");
        Assert.IsTrue(File.Exists(executable), "Build the solution before running the live host integration test.");

        var port = ReserveLoopbackPort();
        var baseUrl = $"http://127.0.0.1:{port}";
        var endpoint = baseUrl + "/mcp";
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var startInfo = new ProcessStartInfo(executable)
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--transport");
        startInfo.ArgumentList.Add("http");
        startInfo.ArgumentList.Add("--url");
        startInfo.ArgumentList.Add(baseUrl);
        startInfo.Environment["ENGINEERING_MCP_HTTP_TOKEN"] = token;
        startInfo.Environment["ENGINEERING_MCP_POLICY"] = Path.Combine(root, "config", "policy.vscode-test.json");

        using var process = new Process { StartInfo = startInfo };
        Assert.IsTrue(process.Start(), "Windows did not start the MCP host process.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        try
        {
            using var healthClient = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            healthClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            await WaitForHealthAsync(healthClient, baseUrl + "/healthz", process);
            using (var initialHealth = await healthClient.GetAsync(baseUrl + "/healthz"))
            {
                initialHealth.EnsureSuccessStatusCode();
                using var initialHealthPayload = JsonDocument.Parse(await initialHealth.Content.ReadAsStringAsync());
                Assert.IsFalse(initialHealthPayload.RootElement.GetProperty("vsCodeActive").GetBoolean());
            }

            using var anonymousHealthClient = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var unauthenticatedHealth = await anonymousHealthClient.GetAsync(baseUrl + "/healthz");
            Assert.AreEqual(HttpStatusCode.Unauthorized, unauthenticatedHealth.StatusCode);

            using var unauthenticated = await anonymousHealthClient.PostAsync(endpoint, JsonContent(ToolsListRequest()));
            Assert.AreEqual(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);

            using var badTokenClient = CreateMcpClient("definitely-invalid-token-value-0000");
            using var badToken = await badTokenClient.PostAsync(endpoint, JsonContent(ToolsListRequest()));
            Assert.AreEqual(HttpStatusCode.Unauthorized, badToken.StatusCode);

            using var crossOriginClient = CreateMcpClient(token);
            crossOriginClient.DefaultRequestHeaders.Add("Origin", "https://untrusted.example.invalid");
            using var crossOrigin = await crossOriginClient.PostAsync(endpoint, JsonContent(ToolsListRequest()));
            Assert.AreEqual(HttpStatusCode.Forbidden, crossOrigin.StatusCode);

            using var client = CreateMcpClient(token);
            using var initialized = await client.PostAsync(McpRuntimeDefaults.WithVsCodeClientMarker(endpoint), JsonContent(new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "initialize",
                @params = new
                {
                    protocolVersion = "2025-06-18",
                    capabilities = new { },
                    clientInfo = new { name = "integration-test", version = "1.0" }
                }
            }));
            initialized.EnsureSuccessStatusCode();

            using (var activeHealth = await healthClient.GetAsync(baseUrl + "/healthz"))
            {
                activeHealth.EnsureSuccessStatusCode();
                using var activeHealthPayload = JsonDocument.Parse(await activeHealth.Content.ReadAsStringAsync());
                Assert.IsTrue(activeHealthPayload.RootElement.GetProperty("vsCodeActive").GetBoolean());
                Assert.AreEqual(JsonValueKind.String,
                    activeHealthPayload.RootElement.GetProperty("lastVsCodeActivityUtc").ValueKind);
            }

            using var listed = await client.PostAsync(endpoint, JsonContent(ToolsListRequest()));
            listed.EnsureSuccessStatusCode();
            using var payload = await ReadMcpJsonAsync(listed);
            var tools = payload.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().ToArray();
            var names = tools
                .Select(tool => tool.GetProperty("name").GetString() ?? string.Empty)
                .ToArray();

            Assert.IsGreaterThan(0, names.Length);
            Assert.IsTrue(names.All(name => PortableToolName().IsMatch(name)),
                "Live tool discovery returned a name outside ^[a-z0-9_-]+$: " +
                string.Join(", ", names.Where(name => !PortableToolName().IsMatch(name))));
            Assert.AreEqual(1, names.Count(name => name == "wpf_attach"));
            Assert.IsFalse(names.Contains("wpf.attach", StringComparer.Ordinal));
            Assert.IsTrue(names.Contains("diagnose", StringComparer.Ordinal));
            Assert.IsTrue(names.Contains("source_find_references_semantic", StringComparer.Ordinal));
            Assert.IsTrue(names.Contains("system_policy_diagnostics", StringComparer.Ordinal));
            Assert.IsTrue(names.Contains("system_tool_preflight", StringComparer.Ordinal));
            Assert.HasCount(76, names);
            CollectionAssert.AreEquivalent(
                ToolPolicyCatalog.All.Select(definition => definition.ToolName).OrderBy(name => name, StringComparer.Ordinal).ToArray(),
                names.OrderBy(name => name, StringComparer.Ordinal).ToArray(),
                "Every published MCP tool must have one authoritative preflight policy definition.");
            foreach (var required in new[]
                     {
                         "wpf_grid_summary", "wpf_selector_audit", "wpf_binding_errors",
                         "wpf_validation_summary", "wpf_wait_absent", "wpf_assert_pattern"
                     })
                Assert.IsTrue(names.Contains(required, StringComparer.Ordinal), $"Missing advanced safe WPF tool: {required}");

            foreach (var tool in tools)
            {
                var toolName = tool.GetProperty("name").GetString();
                Assert.IsTrue(tool.TryGetProperty("title", out var title) && !string.IsNullOrWhiteSpace(title.GetString()), $"{toolName} has no title.");
                Assert.IsTrue(tool.TryGetProperty("outputSchema", out _), $"{toolName} has no output schema.");
                Assert.IsTrue(tool.TryGetProperty("annotations", out var annotations), $"{toolName} has no annotations.");
                foreach (var hint in new[] { "readOnlyHint", "destructiveHint", "idempotentHint", "openWorldHint" })
                    Assert.IsTrue(annotations.TryGetProperty(hint, out _), $"{toolName} is missing annotation {hint}.");

                if (tool.GetProperty("inputSchema").TryGetProperty("properties", out var properties))
                {
                    foreach (var property in properties.EnumerateObject())
                        Assert.IsTrue(property.Value.TryGetProperty("description", out var description) && !string.IsNullOrWhiteSpace(description.GetString()),
                            $"{toolName}.{property.Name} has no parameter description.");
                }
            }

            using (var preflightCall = await client.PostAsync(endpoint, JsonContent(new
            {
                jsonrpc = "2.0",
                id = 3,
                method = "tools/call",
                @params = new { name = "system_tool_preflight", arguments = new { toolName = "wpf_click" } }
            })))
            {
                preflightCall.EnsureSuccessStatusCode();
                using var preflightPayload = await ReadMcpJsonAsync(preflightCall);
                var preflight = preflightPayload.RootElement.GetProperty("result").GetProperty("structuredContent");
                Assert.IsTrue(preflight.GetProperty("known").GetBoolean());
                Assert.IsTrue(preflight.GetProperty("published").GetBoolean());
                Assert.IsTrue(preflight.GetProperty("allowedByPolicy").GetBoolean());
                Assert.AreEqual("ALLOW", preflight.GetProperty("code").GetString());
                StringAssert.Contains(preflight.GetProperty("agentDirective").GetString(), "Do not report it as policy-disabled");
            }

            using (var deniedPreflightCall = await client.PostAsync(endpoint, JsonContent(new
            {
                jsonrpc = "2.0",
                id = 4,
                method = "tools/call",
                @params = new { name = "system_tool_preflight", arguments = new { toolName = "dotnet_capture_dump" } }
            })))
            {
                deniedPreflightCall.EnsureSuccessStatusCode();
                using var deniedPreflightPayload = await ReadMcpJsonAsync(deniedPreflightCall);
                var preflight = deniedPreflightPayload.RootElement.GetProperty("result").GetProperty("structuredContent");
                Assert.IsTrue(preflight.GetProperty("published").GetBoolean());
                Assert.IsFalse(preflight.GetProperty("allowedByPolicy").GetBoolean());
                Assert.AreEqual("PERMISSION_DENIED", preflight.GetProperty("code").GetString());
            }

            using var failedCall = await client.PostAsync(endpoint, JsonContent(new
            {
                jsonrpc = "2.0",
                id = 5,
                method = "tools/call",
                @params = new { name = "wpf_attach", arguments = new { processId = -1 } }
            }));
            failedCall.EnsureSuccessStatusCode();
            using var failedPayload = await ReadMcpJsonAsync(failedCall);
            var failedResult = failedPayload.RootElement.GetProperty("result");
            Assert.IsTrue(failedResult.GetProperty("isError").GetBoolean(), "Domain failures must set MCP isError=true.");
            var failedStructured = failedResult.GetProperty("structuredContent");
            Assert.IsFalse(failedStructured.GetProperty("success").GetBoolean());
            Assert.AreEqual(JsonValueKind.String,
                failedStructured.GetProperty("error").GetProperty("remediation").ValueKind,
                "Policy and guard failures must provide safe remediation guidance.");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }

            _ = await stdout;
            _ = await stderr;
        }
    }

    private static HttpClient CreateMcpClient(string token)
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/event-stream");
        client.DefaultRequestHeaders.Add("MCP-Protocol-Version", "2025-06-18");
        return client;
    }

    private static StringContent JsonContent(object value)
        => new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    private static object ToolsListRequest() => new
    {
        jsonrpc = "2.0",
        id = 2,
        method = "tools/list",
        @params = new { }
    };

    private static async Task<JsonDocument> ReadMcpJsonAsync(HttpResponseMessage response)
    {
        var content = await response.Content.ReadAsStringAsync();
        if (content.TrimStart().StartsWith('{'))
            return JsonDocument.Parse(content);

        var data = content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(line => line.StartsWith("data:", StringComparison.Ordinal));
        Assert.IsNotNull(data, "MCP response contained neither JSON nor an SSE data event.");
        return JsonDocument.Parse(data[5..].Trim());
    }

    private static async Task WaitForHealthAsync(HttpClient client, string healthUrl, Process process)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited)
                Assert.Fail($"MCP host exited before health check with code {process.ExitCode}.");

            try
            {
                using var response = await client.GetAsync(healthUrl);
                if (response.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException) { }

            await Task.Delay(100);
        }

        Assert.Fail("MCP host did not become healthy within 10 seconds.");
    }

    private static int ReserveLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    [GeneratedRegex("^[a-z0-9_-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex PortableToolName();
}
