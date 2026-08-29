using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using EngineeringMcp.Contracts;
using EngineeringMcp.Security;
using Microsoft.Win32;
using Wpf.Ui.Controls;

namespace EngineeringMcp.ControlCenter;

/// <summary>VS Code integration and MCP policy file wiring.</summary>
public partial class MainWindow
{
    private void ConnectVsCode_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureReady()) return;
        try
        {
            var useWorkspaceScope = _layout.IsRepositoryMode && _mcpScope == "workspace";
            var configPath = WriteVsCodeMcpConfiguration(useWorkspaceScope
                ? Path.Combine(_layout.Root, ".vscode", "mcp.json")
                : GetVsCodeUserMcpConfigPath());
            RefreshStatus();
            var installedScope = useWorkspaceScope ? "workspace" : "global";
            AppendLog($"VS Code MCP configuration ({installedScope} scope) installed: " + configPath);
            SetStatus(useWorkspaceScope
                ? "VS Code integration installed for this workspace. Reload VS Code to enable live connection status."
                : "VS Code integration installed for this profile. Reload VS Code to enable live connection status.");
        }
        catch (Exception ex)
        {
            AppendLog("Connect to VS Code failed: " + ex.GetType().Name + ": " + ex.Message);
            SetStatus("Could not install the VS Code MCP configuration. See Logs.");
            MainTabs.SelectedItem = LogsTab;
        }
    }

    // User-profile scope is intentional default: a workspace-local .vscode/mcp.json only exists
    // while that repository is open. The engineering MCP is a developer tool that must
    // remain available when the user switches between authorized workspaces.
    private string WriteVsCodeMcpConfiguration(string configPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);

        JsonObject document;
        if (File.Exists(configPath))
        {
            var existing = File.ReadAllText(configPath);
            document = JsonNode.Parse(existing) as JsonObject
                ?? throw new InvalidDataException("VS Code user mcp.json is not a JSON object.");

            var backupPath = configPath + ".engineering-mcp.bak";
            File.Copy(configPath, backupPath, overwrite: true);
        }
        else
        {
            document = new JsonObject();
        }

        var servers = document["servers"] as JsonObject ?? new JsonObject();
        document["servers"] = servers;

        var server = new JsonObject
        {
            ["type"] = "http",
            ["url"] = McpRuntimeDefaults.VsCodeMcpEndpoint,
            ["headers"] = new JsonObject
            {
                ["Authorization"] = $"Bearer ${{env:{McpRuntimeDefaults.HttpTokenEnvironmentVariable}}}",
                [McpRuntimeDefaults.ClientNameHeader] = McpRuntimeDefaults.VsCodeClientName
            }
        };

        servers[McpRuntimeDefaults.ServerName] = server;

        File.WriteAllText(
            configPath,
            document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);

        return configPath;
    }

    private static string GetVsCodeUserMcpConfigPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(appData))
            throw new DirectoryNotFoundException("Windows application-data directory is unavailable.");

        var stableUser = Path.Combine(appData, "Code", "User");
        var insidersUser = Path.Combine(appData, "Code - Insiders", "User");

        if (Directory.Exists(stableUser))
            return Path.Combine(stableUser, "mcp.json");
        if (Directory.Exists(insidersUser))
            return Path.Combine(insidersUser, "mcp.json");

        // Default to stable's standard profile location. VS Code will create/use this path.
        return Path.Combine(stableUser, "mcp.json");
    }

    private static bool IsVsCodeUserMcpInstalled()
    {
        try
        {
            var configPath = GetVsCodeUserMcpConfigPath();
            if (!File.Exists(configPath)) return false;
            var document = JsonNode.Parse(File.ReadAllText(configPath)) as JsonObject;
            var server = document?["servers"]?[McpRuntimeDefaults.ServerName] as JsonObject;
            var url = server?["url"]?.GetValue<string>();
            return string.Equals(server?["type"]?.GetValue<string>(), "http", StringComparison.OrdinalIgnoreCase)
                && (string.Equals(url, McpRuntimeDefaults.McpEndpoint, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(url, McpRuntimeDefaults.VsCodeMcpEndpoint, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private static bool HasVsCodeClientMarkerInstalled()
    {
        try
        {
            var configPath = GetVsCodeUserMcpConfigPath();
            if (!File.Exists(configPath)) return false;
            var document = JsonNode.Parse(File.ReadAllText(configPath)) as JsonObject;
            var server = document?["servers"]?[McpRuntimeDefaults.ServerName] as JsonObject;
            var headers = server?["headers"] as JsonObject;
            return string.Equals(server?["url"]?.GetValue<string>(), McpRuntimeDefaults.VsCodeMcpEndpoint, StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                headers?[McpRuntimeDefaults.ClientNameHeader]?.GetValue<string>(),
                McpRuntimeDefaults.VsCodeClientName,
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private void OpenVsCode_Click(object sender, RoutedEventArgs e)
    {
        OpenVsCodeUserMcpConfig();
    }

    private void OpenVsCodeUserMcpConfig()
    {
        try
        {
            var path = GetVsCodeUserMcpConfigPath();
            if (!File.Exists(path))
            {
                SetStatus("VS Code integration is not installed yet. Click Connect to VS Code first.");
                return;
            }

            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            AppendLog("Opened the VS Code user-profile MCP configuration: " + path);
        }
        catch (Exception ex)
        {
            AppendLog("Could not open VS Code user MCP configuration: " + ex.Message);
            SetStatus("Could not open the VS Code MCP configuration. See Logs.");
        }
    }

    private void OpenMcpConfig_Click(object sender, RoutedEventArgs e) => OpenVsCodeUserMcpConfig();

    private void SelectPolicy_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select Engineering MCP policy",
            Filter = "JSON policy (*.json)|*.json|All files (*.*)|*.*",
            CheckFileExists = true,
            InitialDirectory = Path.GetDirectoryName(_layout.Policy)
        };

        if (dialog.ShowDialog(this) is not true)
            return;

        var selected = Path.GetFullPath(dialog.FileName);
        _layout = _layout with { Policy = selected };
        Environment.SetEnvironmentVariable("ENGINEERING_MCP_POLICY", selected, EnvironmentVariableTarget.User);
        Environment.SetEnvironmentVariable("ENGINEERING_MCP_POLICY", selected, EnvironmentVariableTarget.Process);
        PolicyPathText.Text = selected;
        RefreshStatus();
        AppendLog("Selected MCP policy: " + selected);
        SetStatus("MCP policy selected. Restart the MCP server to apply it.");
    }

    private async void AuthorizeWpfWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureReady() || _busy) return;

        var suggestedRoot = WpfWorkspacePolicyProvisioner.FindSuggestedWorkspaceRoot();
        var dialog = new OpenFolderDialog
        {
            Title = "Select a WPF solution or workspace",
            Multiselect = false,
            InitialDirectory = suggestedRoot
        };

        if (dialog.ShowDialog(this) is not true)
            return;

        try
        {
            WpfWorkspacePolicyProvisioningResult provisioned;
            try
            {
                provisioned = WpfWorkspacePolicyProvisioner.Provision(dialog.FolderName);
            }
            catch (WpfWorkspaceDiscoveryException discoveryException)
            {
                AppendLog("Automatic WPF discovery did not find an application: " + discoveryException.Message);
                var executableDialog = new OpenFileDialog
                {
                    Title = "Select a built WPF executable inside the workspace",
                    Filter = "Windows executable (*.exe)|*.exe",
                    CheckFileExists = true,
                    Multiselect = false,
                    InitialDirectory = dialog.FolderName
                };
                if (executableDialog.ShowDialog(this) is not true)
                {
                    SetStatus("WPF workspace authorization was cancelled.");
                    return;
                }

                provisioned = WpfWorkspacePolicyProvisioner.ProvisionExecutable(
                    dialog.FolderName,
                    executableDialog.FileName);
            }
            Environment.SetEnvironmentVariable(
                "ENGINEERING_MCP_POLICY",
                provisioned.PolicyPath,
                EnvironmentVariableTarget.User);
            Environment.SetEnvironmentVariable(
                "ENGINEERING_MCP_POLICY",
                provisioned.PolicyPath,
                EnvironmentVariableTarget.Process);

            _layout = _layout with { Policy = provisioned.PolicyPath };
            PolicyPathText.Text = provisioned.PolicyPath;
            RefreshStatus();
            AppendLog("Provisioned durable WPF workspace policy: " + provisioned.PolicyPath);
            AppendLog($"Authorized {provisioned.Applications.Count} built WPF application(s): " +
                      string.Join(", ", provisioned.Applications.Select(application => application.Name)));

            if (await StartMcpServerAsync(restart: true, CancellationToken.None))
                SetStatus($"WPF workspace authorized for {provisioned.Applications.Count} application(s). MCP restarted and ready for VS Code.");
            else
                SetStatus("WPF workspace policy installed, but MCP restart failed. See Logs.");
        }
        catch (Exception ex)
        {
            AppendLog("Authorize WPF workspace failed: " + ex.GetType().Name + ": " + ex.Message);
            SetStatus("Could not authorize the WPF workspace. Build or select a verifiable WPF application and see Logs for details.");
            MainTabs.SelectedItem = LogsTab;
        }
    }

    private void OpenPolicy_Click(object sender, RoutedEventArgs e) => OpenFile(_layout.Policy);
    private void OpenSecurity_Click(object sender, RoutedEventArgs e) => OpenFile(_layout.SecurityDoc);

    private void RefreshPolicyDiagnostics()
    {
        if (PolicyDiagnosticsText is null)
            return;

        if (!File.Exists(_layout.Policy))
        {
            PolicyDiagnosticsText.Text = "Policy file missing. Select or configure a policy before starting the MCP server.";
            SecurityStatusText.Text = "● Policy missing";
            return;
        }

        try
        {
            var provider = new FilePolicyProvider(_layout.Policy);
            var report = PolicyDiagnostics.Analyze(provider.Current, provider.Source);
            if (report.Findings.Count == 0)
            {
                PolicyDiagnosticsText.Text =
                    $"Policy ready: {report.PermissionCeiling}; {report.ProcessRuleCount} process rule(s); {report.SourceRootCount} source root(s).";
                SecurityStatusText.Text = "● Armed";
                SecurityStatusText.ToolTip = "Configured policy passed readiness checks.";
                return;
            }

            PolicyDiagnosticsText.Text = string.Join(Environment.NewLine,
                report.Findings.Take(3).Select(finding => $"{finding.Code}: {finding.Summary} {finding.Remediation}"));
            SecurityStatusText.Text = $"● {report.Findings.Count} policy warning(s)";
            SecurityStatusText.ToolTip = string.Join(Environment.NewLine,
                report.Findings.Select(finding => $"{finding.Code}: {finding.Remediation}"));
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or IOException or UnauthorizedAccessException)
        {
            PolicyDiagnosticsText.Text = "Policy validation failed. Select a valid policy and restart the MCP server.";
            SecurityStatusText.Text = "● Policy invalid";
            SecurityStatusText.ToolTip = ex.GetType().Name;
        }
    }

    private void OpenFile(string path)
    {
        if (!File.Exists(path))
        {
            AppendLog("File not found: " + path);
            return;
        }
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(ActivityLog.Text))
            Clipboard.SetText(ActivityLog.Text);
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        ActivityLog.Clear();
        _logEntries.Clear();
    }
}
