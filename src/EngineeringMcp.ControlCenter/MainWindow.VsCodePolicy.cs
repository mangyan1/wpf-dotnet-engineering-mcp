using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using EngineeringMcp.Contracts;
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
            var configPath = WriteVsCodeMcpConfiguration(_mcpScope == "workspace"
                ? Path.Combine(_layout.Root, ".vscode", "mcp.json")
                : GetVsCodeUserMcpConfigPath());
            RefreshStatus();
            AppendLog($"VS Code MCP configuration ({_mcpScope} scope) installed: " + configPath);
            SetStatus(_mcpScope == "workspace"
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
    // remain available when the user switches to ApexDrive or another authorized project.
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
            ["url"] = McpRuntimeDefaults.McpEndpoint,
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
            return string.Equals(server?["type"]?.GetValue<string>(), "http", StringComparison.OrdinalIgnoreCase)
                && string.Equals(server?["url"]?.GetValue<string>(), McpRuntimeDefaults.McpEndpoint, StringComparison.OrdinalIgnoreCase);
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
            var headers = document?["servers"]?[McpRuntimeDefaults.ServerName]?["headers"] as JsonObject;
            return string.Equals(
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

    private void OpenPolicy_Click(object sender, RoutedEventArgs e) => OpenFile(_layout.Policy);
    private void OpenSecurity_Click(object sender, RoutedEventArgs e) => OpenFile(_layout.SecurityDoc);

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
