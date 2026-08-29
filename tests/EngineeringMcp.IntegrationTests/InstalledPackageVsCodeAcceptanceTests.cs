using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using EngineeringMcp.Contracts;
using EngineeringMcp.Security;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EngineeringMcp.IntegrationTests;

public sealed partial class McpHttpIntegrationTests
{
    [TestMethod]
    [TestCategory("InstalledAcceptance")]
    public async Task InstalledPackage_VsCodeAcceptance_UsesDurableConfigurationAndRepresentativeTools()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("The installed Engineering MCP package is Windows-only.");
            return;
        }

        var installRootValue = Environment.GetEnvironmentVariable("ENGINEERING_MCP_ACCEPTANCE_INSTALL_ROOT");
        var durablePolicyValue = Environment.GetEnvironmentVariable("ENGINEERING_MCP_ACCEPTANCE_POLICY");
        var vsCodeConfigValue = Environment.GetEnvironmentVariable("ENGINEERING_MCP_ACCEPTANCE_VSCODE_CONFIG");
        if (string.IsNullOrWhiteSpace(installRootValue) ||
            string.IsNullOrWhiteSpace(durablePolicyValue) ||
            string.IsNullOrWhiteSpace(vsCodeConfigValue))
        {
            Assert.Inconclusive("Run scripts/test-installed-vscode.ps1 to supply the installed acceptance paths.");
            return;
        }

        var installRoot = Path.GetFullPath(installRootValue);
        var durablePolicy = Path.GetFullPath(durablePolicyValue);
        var vsCodeConfig = Path.GetFullPath(vsCodeConfigValue);
        var hostExecutable = Path.Combine(installRoot, "host", "EngineeringMcp.Host.exe");

        Assert.IsTrue(File.Exists(hostExecutable), "The installed MCP host executable is missing.");
        Assert.IsTrue(File.Exists(durablePolicy), "The durable user policy is missing.");
        Assert.IsTrue(File.Exists(vsCodeConfig), "The VS Code MCP configuration is missing.");
        Assert.IsFalse(IsUnderDirectory(durablePolicy, installRoot),
            "The selected policy must live outside the install directory so uninstall/reinstall cannot remove it.");

        ValidateVsCodeConfiguration(vsCodeConfig);
        var durablePolicyHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(durablePolicy)));
        var vsCodeConfigHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(vsCodeConfig)));

        var tempRoot = Path.Combine(Path.GetTempPath(), "engineering-mcp-installed-acceptance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var acceptancePolicy = Path.Combine(tempRoot, "policy.acceptance.json");
        WriteAcceptancePolicy(acceptancePolicy, hostExecutable, installRoot, Path.Combine(tempRoot, "audit"));

        var port = ReserveLoopbackPort();
        var baseUrl = $"http://127.0.0.1:{port}";
        var endpoint = baseUrl + "/mcp";
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var startInfo = new ProcessStartInfo(hostExecutable)
        {
            WorkingDirectory = installRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--transport");
        startInfo.ArgumentList.Add("http");
        startInfo.ArgumentList.Add("--url");
        startInfo.ArgumentList.Add(baseUrl);
        ConfigureMinimalEnvironment(startInfo, acceptancePolicy, token, tempRoot);

        using var process = new Process { StartInfo = startInfo };
        Assert.IsTrue(process.Start(), "Windows did not start the installed MCP host process.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        try
        {
            using var healthClient = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            healthClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            await WaitForHealthAsync(healthClient, baseUrl + "/healthz", process);

            int hostProcessId;
            using (var healthResponse = await healthClient.GetAsync(baseUrl + "/healthz"))
            {
                healthResponse.EnsureSuccessStatusCode();
                using var health = JsonDocument.Parse(await healthResponse.Content.ReadAsStringAsync());
                hostProcessId = health.RootElement.GetProperty("processId").GetInt32();
            }

            using var client = CreateMcpClient(token);
            client.DefaultRequestHeaders.Add(McpRuntimeDefaults.ClientNameHeader, McpRuntimeDefaults.VsCodeClientName);
            using (var initialized = await client.PostAsync(McpRuntimeDefaults.WithVsCodeClientMarker(endpoint), JsonContent(new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "initialize",
                @params = new
                {
                    protocolVersion = "2025-06-18",
                    capabilities = new { },
                    clientInfo = new { name = "vscode", version = "installed-acceptance" }
                }
            })))
            {
                initialized.EnsureSuccessStatusCode();
            }

            using (var listed = await client.PostAsync(endpoint, JsonContent(ToolsListRequest())))
            {
                listed.EnsureSuccessStatusCode();
                using var payload = await ReadMcpJsonAsync(listed);
                var names = payload.RootElement.GetProperty("result").GetProperty("tools")
                    .EnumerateArray()
                    .Select(tool => tool.GetProperty("name").GetString())
                    .Where(name => name is not null)
                    .ToArray();
                Assert.HasCount(76, names);
                CollectionAssert.IsSubsetOf(
                    new[] { "system_health", "system_policy_diagnostics", "system_tool_preflight", "dotnet_runtime_info", "dotnet_capture_dump" },
                    names!);
            }

            using (var policyResponse = await CallToolAsync(client, endpoint, 3, "system_policy_diagnostics", new { }))
            {
                var result = policyResponse.RootElement.GetProperty("result");
                Assert.IsFalse(result.TryGetProperty("isError", out var isError) && isError.GetBoolean());
                Assert.AreEqual("configured-file",
                    result.GetProperty("structuredContent").GetProperty("policySource").GetString());
            }

            using (var preflightResponse = await CallToolAsync(client, endpoint, 4, "system_tool_preflight", new { toolName = "dotnet_runtime_info" }))
            {
                var preflight = preflightResponse.RootElement.GetProperty("result").GetProperty("structuredContent");
                Assert.IsTrue(preflight.GetProperty("published").GetBoolean());
                Assert.IsTrue(preflight.GetProperty("allowedByPolicy").GetBoolean());
                Assert.AreEqual("ALLOW", preflight.GetProperty("code").GetString());
            }

            using (var runtimeResponse = await CallToolAsync(client, endpoint, 5, "dotnet_runtime_info", new { processId = hostProcessId }))
            {
                var result = runtimeResponse.RootElement.GetProperty("result");
                Assert.IsFalse(result.TryGetProperty("isError", out var isError) && isError.GetBoolean());
                Assert.IsTrue(result.GetProperty("structuredContent").GetProperty("success").GetBoolean());
            }

            using (var deniedResponse = await CallToolAsync(client, endpoint, 6, "dotnet_capture_dump", new { processId = hostProcessId }))
            {
                var result = deniedResponse.RootElement.GetProperty("result");
                Assert.IsTrue(result.GetProperty("isError").GetBoolean());
                var error = result.GetProperty("structuredContent").GetProperty("error");
                Assert.AreEqual("PERMISSION_DENIED", error.GetProperty("code").GetString());
                StringAssert.Contains(error.GetProperty("remediation").GetString(), "Control Center");
            }

            Assert.AreEqual(durablePolicyHash,
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(durablePolicy))),
                "The installed host acceptance run changed the durable policy.");
            Assert.AreEqual(vsCodeConfigHash,
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(vsCodeConfig))),
                "The installed host acceptance run changed the VS Code configuration.");
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
            await DeleteTemporaryDirectoryAsync(tempRoot);
        }
    }

    private static void ValidateVsCodeConfiguration(string configPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(configPath));
        var server = document.RootElement.GetProperty("servers").GetProperty(McpRuntimeDefaults.ServerName);
        Assert.AreEqual("http", server.GetProperty("type").GetString());
        Assert.AreEqual(McpRuntimeDefaults.VsCodeMcpEndpoint, server.GetProperty("url").GetString());
        var headers = server.GetProperty("headers");
        Assert.AreEqual(McpRuntimeDefaults.VsCodeClientName,
            headers.GetProperty(McpRuntimeDefaults.ClientNameHeader).GetString());
        var authorization = headers.GetProperty("Authorization").GetString();
        Assert.IsNotNull(authorization);
        StringAssert.Contains(authorization, "${env:");
        Assert.IsFalse(authorization.Contains(Environment.GetEnvironmentVariable(McpRuntimeDefaults.HttpTokenEnvironmentVariable) ?? Guid.NewGuid().ToString(), StringComparison.Ordinal));
    }

    private static void WriteAcceptancePolicy(string path, string hostExecutable, string installRoot, string auditDirectory)
    {
        var policy = new
        {
            policyVersion = 1,
            enabledToolProfiles = new[] { "core", "wpf-read", "wpf-interact", "diagnostics", "source" },
            permissionCeiling = "ApplicationDiagnostics",
            processes = new { allow = new[] { new { name = Path.GetFileName(hostExecutable), path = hostExecutable, sha256 = (string?)null, publisher = (string?)null } } },
            filesystem = new { readRoots = new[] { installRoot }, denyGlobs = new[] { "**/.env", "**/secrets.json", "**/*.pfx", "**/*.key", "**/*.dmp", "**/*.dump", "**/.git/**" } },
            network = new { @default = "deny", allow = Array.Empty<string>() },
            pii = "Mask",
            audit = new { enabled = true, directory = auditDirectory, retentionDays = 1 },
            screenshots = new { enabled = false, maskPasswordControls = true, maskSensitiveNames = true, failClosedOnRedactionError = true },
            allowDestructiveActions = false,
            allowPrivilegedDiagnostics = false,
            uiActions = new { denyAutomationIds = Array.Empty<string>(), destructiveAutomationIds = Array.Empty<string>(), statefulAutomationIds = Array.Empty<string>() }
        };
        File.WriteAllText(path, JsonSerializer.Serialize(policy));
    }

    private static void ConfigureMinimalEnvironment(ProcessStartInfo startInfo, string policy, string token, string tempRoot)
    {
        var inheritedPath = Environment.GetEnvironmentVariable("PATH");
        startInfo.Environment.Clear();
        foreach (var name in new[] { "SystemRoot", "WINDIR", "COMSPEC" })
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value))
                startInfo.Environment[name] = value;
        }

        startInfo.Environment["PATH"] = ProcessEnvironmentSanitizer.SanitizePath(inheritedPath);
        startInfo.Environment["TEMP"] = tempRoot;
        startInfo.Environment["TMP"] = tempRoot;
        startInfo.Environment["ENGINEERING_MCP_POLICY"] = policy;
        startInfo.Environment[McpRuntimeDefaults.HttpTokenEnvironmentVariable] = token;
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["DOTNET_NOLOGO"] = "1";
    }

    private static async Task<JsonDocument> CallToolAsync(HttpClient client, string endpoint, int id, string name, object arguments)
    {
        using var response = await client.PostAsync(endpoint, JsonContent(new
        {
            jsonrpc = "2.0",
            id,
            method = "tools/call",
            @params = new { name, arguments }
        }));
        response.EnsureSuccessStatusCode();
        return await ReadMcpJsonAsync(response);
    }

    private static bool IsUnderDirectory(string candidate, string directory)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullCandidate = Path.GetFullPath(candidate);
        return fullCandidate.StartsWith(root, comparison);
    }

    private static async Task DeleteTemporaryDirectoryAsync(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!IsUnderDirectory(fullPath, Path.GetTempPath()))
            throw new InvalidOperationException("Refused to delete an acceptance directory outside the system temporary root.");

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                Directory.Delete(fullPath, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 5)
            {
                await Task.Delay(attempt * 100);
            }
        }
    }
}
