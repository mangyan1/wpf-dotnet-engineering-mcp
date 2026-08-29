using System.Text.Json;
using System.Text.RegularExpressions;
using EngineeringMcp.Contracts;
using EngineeringMcp.Security;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace EngineeringMcp.ControlCenter;

internal sealed record DevTestStep(string Area, string Test, string Status, string Detail);

internal sealed record McpSelfTestReport(
    bool Success,
    int ToolCount,
    string? ProtocolVersion,
    IReadOnlyList<DevTestStep> Steps);

internal sealed class McpSelfTestService
{
    private static readonly Regex ValidToolName = new("^[a-z0-9_-]+$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly string[] RequiredCoreTools =
    [
        "system_version",
        "system_health",
        "system_capabilities",
        "system_permissions",
        "system_policy_diagnostics",
        "system_tool_preflight",
        "wpf_list_processes",
        "wpf_attach",
        "wpf_snapshot",
        "wpf_find",
        "wpf_type",
        "wpf_assert",
        "wpf_screenshot",
        "wpf_probe"
    ];

    public Task<McpClient> OpenHttpSessionAsync(string httpToken, CancellationToken cancellationToken = default)
        => CreateHttpClientAsync(httpToken, cancellationToken);

    public async Task<McpSelfTestReport> RunProtocolSmokeAsync(
        string httpToken,
        Action<DevTestStep> onStep,
        Action<string> onLog,
        CancellationToken cancellationToken)
    {
        var steps = new List<DevTestStep>();
        void Record(DevTestStep step)
        {
            steps.Add(step);
            onStep(step);
        }

        await using var client = await CreateHttpClientAsync(httpToken, cancellationToken).ConfigureAwait(false);
        Record(new DevTestStep("Transport", "Connect Streamable HTTP", "PASS",
            $"Connected to {McpRuntimeDefaults.McpEndpoint}; negotiated protocol: {client.NegotiatedProtocolVersion ?? "legacy/unspecified"}."));

        var tools = await client.ListToolsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var invalidNames = tools.Select(tool => tool.Name).Where(name => !ValidToolName.IsMatch(name)).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        if (invalidNames.Length > 0)
        {
            Record(new DevTestStep("Protocol", "Tool name contract", "FAIL",
                "Invalid MCP tool names: " + string.Join(", ", invalidNames)));
            return new McpSelfTestReport(false, tools.Count, client.NegotiatedProtocolVersion, steps);
        }

        Record(new DevTestStep("Protocol", "Tool name contract", "PASS",
            $"All {tools.Count} tool names satisfy ^[a-z0-9_-]+$."));

        var toolNames = tools.Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);
        var missing = RequiredCoreTools.Where(name => !toolNames.Contains(name)).ToArray();
        if (missing.Length > 0)
        {
            Record(new DevTestStep("Protocol", "Tool discovery", "FAIL", "Missing: " + string.Join(", ", missing)));
            return new McpSelfTestReport(false, tools.Count, client.NegotiatedProtocolVersion, steps);
        }

        Record(new DevTestStep("Protocol", "Tool discovery", "PASS", $"{tools.Count} tools discovered; required core surface present."));

        foreach (var tool in new[] { "system_version", "system_health", "system_capabilities", "system_permissions", "system_policy_diagnostics" })
        {
            if (!await CallAndRecordAsync(client, "Core", tool, null, Record, onLog, cancellationToken).ConfigureAwait(false))
                return new McpSelfTestReport(false, tools.Count, client.NegotiatedProtocolVersion, steps);
        }

        if (!await CallAndRecordAsync(client, "Core", "system_tool_preflight",
                new Dictionary<string, object?> { ["toolName"] = "wpf_click" },
                Record, onLog, cancellationToken).ConfigureAwait(false))
            return new McpSelfTestReport(false, tools.Count, client.NegotiatedProtocolVersion, steps);

        return new McpSelfTestReport(true, tools.Count, client.NegotiatedProtocolVersion, steps);
    }

    public async Task<bool> RunStdioCompatibilitySmokeAsync(
        ProjectLayout layout,
        string probeToken,
        Action<DevTestStep> onStep,
        Action<string> onLog,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var client = await CreateStdioClientAsync(layout, probeToken, onLog, cancellationToken).ConfigureAwait(false);
            var tools = await client.ListToolsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            var invalidNames = tools.Select(tool => tool.Name).Where(name => !ValidToolName.IsMatch(name)).OrderBy(name => name, StringComparer.Ordinal).ToArray();
            if (invalidNames.Length > 0)
            {
                var detail = "Invalid MCP tool names: " + string.Join(", ", invalidNames);
                onStep(new DevTestStep("Transport", "stdio tool name contract", "FAIL", detail));
                onLog("stdio tool name contract: FAIL - " + detail);
                return false;
            }

            onStep(new DevTestStep("Transport", "stdio tool name contract", "PASS",
                $"All {tools.Count} stdio tool names satisfy ^[a-z0-9_-]+$."));

            var health = await client.CallToolAsync("system_health", new Dictionary<string, object?>(), cancellationToken: cancellationToken).ConfigureAwait(false);
            var success = health.IsError is not true && !HasDomainFailure(health);
            onStep(new DevTestStep("Transport", "stdio compatibility", success ? "PASS" : "FAIL",
                success ? $"Compatibility transport works; {tools.Count} tools discovered." : Summarize("system_health", health)));
            return success;
        }
        catch (Exception ex)
        {
            var message = ex.GetType().Name + ": " + ex.Message;
            onStep(new DevTestStep("Transport", "stdio compatibility", "FAIL", message));
            onLog("stdio compatibility: FAIL - " + message);
            return false;
        }
    }

    public async Task<McpSelfTestReport> RunWpfEndToEndAsync(
        int fixtureProcessId,
        string httpToken,
        Action<DevTestStep> onStep,
        Action<string> onLog,
        CancellationToken cancellationToken)
    {
        var steps = new List<DevTestStep>();
        void Record(DevTestStep step)
        {
            steps.Add(step);
            onStep(step);
        }

        await using var client = await CreateHttpClientAsync(httpToken, cancellationToken).ConfigureAwait(false);
        var tools = await client.ListToolsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        Record(new DevTestStep("Transport", "HTTP MCP + discover", "PASS", $"Connected to shared service; {tools.Count} tools available."));

        var tests = new (string Area, string Tool, Dictionary<string, object?>? Args)[]
        {
            ("WPF UIA", "wpf_attach", new() { ["processId"] = fixtureProcessId }),
            ("WPF UIA", "wpf_list_windows", new() { ["processId"] = fixtureProcessId }),
            ("WPF UIA", "wpf_snapshot", new()
            {
                ["processId"] = fixtureProcessId,
                ["windowReference"] = null,
                ["maxElements"] = 250,
                ["maxDepth"] = 10
            }),
            ("WPF UIA", "wpf_find", new()
            {
                ["processId"] = fixtureProcessId,
                ["automationId"] = "MountPathBox",
                ["name"] = null,
                ["controlType"] = null,
                ["reference"] = null
            }),
            ("WPF interaction", "wpf_type", new()
            {
                ["processId"] = fixtureProcessId,
                ["text"] = @"C:\McpFixture\SmokeTest",
                ["automationId"] = "MountPathBox",
                ["name"] = null,
                ["controlType"] = null,
                ["reference"] = null
            }),
            ("WPF interaction", "wpf_assert", new()
            {
                ["processId"] = fixtureProcessId,
                ["automationId"] = "SaveButton",
                ["name"] = null,
                ["controlType"] = null,
                ["reference"] = null,
                ["enabled"] = true,
                ["offscreen"] = null,
                ["keyboardFocusable"] = null,
                ["expectedName"] = null
            }),
            ("WPF probe", "wpf_probe", new() { ["processId"] = fixtureProcessId, ["operation"] = "status" }),
            ("WPF probe", "wpf_probe", new()
            {
                ["processId"] = fixtureProcessId,
                ["operation"] = "binding_errors",
                ["automationId"] = null,
                ["name"] = null
            }),
            ("Security", "wpf_screenshot", new()
            {
                ["processId"] = fixtureProcessId,
                ["automationId"] = null,
                ["name"] = null,
                ["controlType"] = null,
                ["reference"] = null
            })
        };

        foreach (var test in tests)
        {
            if (!await CallAndRecordAsync(client, test.Area, test.Tool, test.Args, Record, onLog, cancellationToken).ConfigureAwait(false))
                return new McpSelfTestReport(false, tools.Count, client.NegotiatedProtocolVersion, steps);
        }

        return new McpSelfTestReport(true, tools.Count, client.NegotiatedProtocolVersion, steps);
    }

    public async Task<bool> RunAspNetEndToEndAsync(
        int backendProcessId,
        string httpToken,
        Action<DevTestStep> onStep,
        Action<string> onLog,
        CancellationToken cancellationToken)
    {
        var steps = new List<DevTestStep>();
        void Record(DevTestStep step)
        {
            steps.Add(step);
            onStep(step);
        }

        await using var client = await CreateHttpClientAsync(httpToken, cancellationToken).ConfigureAwait(false);
        if (!await CallAndRecordAsync(client, "ASP.NET telemetry", "aspnet_health",
                new Dictionary<string, object?> { ["processId"] = backendProcessId },
                Record, onLog, cancellationToken).ConfigureAwait(false))
            return false;

        return await CallAndRecordAsync(client, "ASP.NET telemetry", "aspnet_recent_requests",
            new Dictionary<string, object?> { ["processId"] = backendProcessId, ["limit"] = 10 },
            Record, onLog, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<McpClient> CreateHttpClientAsync(string httpToken, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(httpToken);

        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Name = ".NET/WPF Engineering MCP Dev Self-Test",
            Endpoint = new Uri(McpRuntimeDefaults.McpEndpoint),
            TransportMode = HttpTransportMode.StreamableHttp,
            ConnectionTimeout = TimeSpan.FromSeconds(5),
            EnableStandaloneGetStream = false,
            AdditionalHeaders = new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer " + httpToken
            }
        });

        return await McpClient.CreateAsync(transport, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static async Task<McpClient> CreateStdioClientAsync(
        ProjectLayout layout,
        string probeToken,
        Action<string> onLog,
        CancellationToken cancellationToken)
    {
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = ".NET/WPF Engineering MCP stdio Compatibility Test",
            Command = layout.HostExecutable,
            Arguments = ["--transport", "stdio"],
            WorkingDirectory = layout.Root,
            InheritEnvironmentVariables = false,
            EnvironmentVariables = CreateMinimalEnvironment(layout, probeToken),
            ShutdownTimeout = TimeSpan.FromSeconds(5),
            StandardErrorLines = line =>
            {
                if (!string.IsNullOrWhiteSpace(line))
                    onLog("MCP stdio: " + line);
            }
        });

        return await McpClient.CreateAsync(transport, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    internal static Dictionary<string, string?> CreateMinimalEnvironment(
        ProjectLayout layout,
        string probeToken,
        string? httpToken = null,
        string? backendToken = null)
    {
        var environment = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
        environment["ENGINEERING_MCP_POLICY"] = layout.Policy;
        environment["ENGINEERING_MCP_PROBE_TOKEN"] = probeToken;
        if (!string.IsNullOrWhiteSpace(backendToken))
            environment["ENGINEERING_MCP_BACKEND_TOKEN"] = backendToken;
        if (!string.IsNullOrWhiteSpace(httpToken))
            environment[McpRuntimeDefaults.HttpTokenEnvironmentVariable] = httpToken;

        foreach (var name in new[] { "DOTNET_ROOT", "NUGET_PACKAGES", "DOTNET_CLI_HOME" })
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (ProcessEnvironmentSanitizer.IsSafeLocalAbsolutePath(value))
                environment[name] = value;
        }

        ProcessEnvironmentSanitizer.SanitizePathInPlace(environment);
        environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        environment["DOTNET_NOLOGO"] = "1";

        return environment;
    }

    private static async Task<bool> CallAndRecordAsync(
        McpClient client,
        string area,
        string tool,
        Dictionary<string, object?>? arguments,
        Action<DevTestStep> record,
        Action<string> onLog,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await client.CallToolAsync(
                tool,
                arguments ?? new Dictionary<string, object?>(),
                cancellationToken: cancellationToken).ConfigureAwait(false);

            var summary = Summarize(tool, result);
            var failed = result.IsError is true || HasDomainFailure(result);
            var status = failed ? "FAIL" : "PASS";
            record(new DevTestStep(area, tool, status, summary));
            if (failed)
                onLog($"{tool}: {status} - {summary}");
            return !failed;
        }
        catch (OperationCanceledException)
        {
            record(new DevTestStep(area, tool, "CANCEL", "Operation cancelled."));
            throw;
        }
        catch (Exception ex)
        {
            var safe = ex.GetType().Name + ": " + ex.Message;
            record(new DevTestStep(area, tool, "FAIL", safe));
            onLog($"{tool}: FAIL - {safe}");
            return false;
        }
    }

    private static bool HasDomainFailure(CallToolResult result)
    {
        if (result.StructuredContent is { } structured && HasNegativeResultFlag(structured))
            return true;

        foreach (var text in result.Content.OfType<TextContentBlock>())
        {
            if (TryReadFailure(text.Text))
                return true;
        }

        return false;
    }

    private static bool TryReadFailure(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text[0] != '{')
            return false;

        try
        {
            using var document = JsonDocument.Parse(text);
            return HasNegativeResultFlag(document.RootElement);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool HasNegativeResultFlag(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return false;

        if (TryGetPropertyIgnoreCase(element, "success", out var success) && success.ValueKind == JsonValueKind.False)
            return true;
        if (TryGetPropertyIgnoreCase(element, "passed", out var passed) && passed.ValueKind == JsonValueKind.False)
            return true;

        if (TryGetPropertyIgnoreCase(element, "value", out var value) && value.ValueKind == JsonValueKind.Object)
        {
            if (TryGetPropertyIgnoreCase(value, "success", out var nestedSuccess) && nestedSuccess.ValueKind == JsonValueKind.False)
                return true;
            if (TryGetPropertyIgnoreCase(value, "passed", out var nestedPassed) && nestedPassed.ValueKind == JsonValueKind.False)
                return true;
        }

        return false;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string Summarize(string tool, CallToolResult result)
    {
        if (tool.Equals("wpf_screenshot", StringComparison.Ordinal))
            return result.IsError is true ? "Screenshot/redaction tool returned an MCP error." : "Sanitized screenshot captured; image payload intentionally omitted from dev log.";

        string value;
        if (result.StructuredContent is { } structured)
        {
            value = structured.GetRawText();
        }
        else
        {
            value = string.Join(" | ", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        }

        if (string.IsNullOrWhiteSpace(value))
            return result.IsError is true ? "Tool returned an error without text." : "Tool completed successfully.";

        value = value.Replace('\r', ' ').Replace('\n', ' ');
        const int max = 420;
        return value.Length <= max ? value : value[..max] + "…";
    }
}
