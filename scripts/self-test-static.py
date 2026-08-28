#!/usr/bin/env python3
from __future__ import annotations

import json
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
failures: list[str] = []
passes: list[str] = []

def check(condition: bool, name: str, detail: str = "") -> None:
    if condition:
        passes.append(name)
        print(f"PASS  {name}" + (f" — {detail}" if detail else ""))
    else:
        failures.append(name + (f": {detail}" if detail else ""))
        print(f"FAIL  {name}" + (f" — {detail}" if detail else ""))

# JSON syntax
json_files = list(ROOT.rglob("*.json"))
json_ok = True
for path in json_files:
    try:
        json.loads(path.read_text(encoding="utf-8"))
    except Exception as exc:
        json_ok = False
        print(f"      JSON error {path.relative_to(ROOT)}: {exc}")
check(json_ok, "JSON files parse", f"{len(json_files)} files")

# XML/XAML/project syntax
xml_files = [*ROOT.rglob("*.csproj"), *ROOT.rglob("*.props"), *ROOT.rglob("*.xaml")]
xml_ok = True
for path in xml_files:
    try:
        ET.parse(path)
    except Exception as exc:
        xml_ok = False
        print(f"      XML error {path.relative_to(ROOT)}: {exc}")
check(xml_ok, "XAML/project XML parses", f"{len(xml_files)} files")

program = (ROOT / "src/EngineeringMcp.Host/Program.cs").read_text(encoding="utf-8")
packages = (ROOT / "Directory.Packages.props").read_text(encoding="utf-8")
host_csproj = (ROOT / "src/EngineeringMcp.Host/EngineeringMcp.Host.csproj").read_text(encoding="utf-8")
selftest = (ROOT / "src/EngineeringMcp.ControlCenter/McpSelfTestService.cs").read_text(encoding="utf-8")
maincs = (ROOT / "src/EngineeringMcp.ControlCenter/MainWindow.xaml.cs").read_text(encoding="utf-8")
xaml = (ROOT / "src/EngineeringMcp.ControlCenter/MainWindow.xaml").read_text(encoding="utf-8")
runtime = (ROOT / "src/EngineeringMcp.Contracts/McpRuntimeDefaults.cs").read_text(encoding="utf-8")
artifact_layout = (ROOT / "src/EngineeringMcp.ControlCenter/SelfTestArtifactLayout.cs").read_text(encoding="utf-8")
integration_test_support = "\n".join(path.read_text(encoding="utf-8") for path in
                                     (ROOT / "tests/EngineeringMcp.IntegrationTests").glob("Test*Locator*.cs"))
tool_sources = "\n".join(path.read_text(encoding="utf-8") for path in (ROOT / "src/EngineeringMcp.Host").glob("*Tools.cs"))

check('ModelContextProtocol.AspNetCore' in packages and 'ModelContextProtocol.AspNetCore' in host_csproj,
      "HTTP MCP package wired")
check('WithHttpTransport' in program and 'MapMcp(McpRuntimeDefaults.McpPath)' in program,
      "Streamable HTTP server mapped")
check('WithStdioServerTransport' in program,
      "stdio compatibility retained")
check('UseUrls(launch.ListenUrl)' in program and 'IPAddress.IsLoopback' in program and 'IsAllowedLoopbackHost' in program,
      "HTTP service is loopback guarded")
check('UseCors' not in program and 'AddCors' not in program,
      "HTTP service does not enable CORS")
check('MaxRequestBodySize = 1_048_576' in program and 'SemaphoreSlim(8, 8)' in program,
      "HTTP request size and concurrency are bounded")
check('http://127.0.0.1:8765' in runtime and 'McpPath = "/mcp"' in runtime,
      "Runtime endpoint centralized")
check('HttpClientTransport' in selftest and 'HttpTransportMode.StreamableHttp' in selftest,
      "Control Center self-test uses shared HTTP MCP")
check('RunStdioCompatibilitySmokeAsync' in selftest,
      "Full self-test retains stdio compatibility check")
check('Command = layout.HostExecutable' in selftest and '"dotnet"' not in selftest[selftest.find('CreateStdioClientAsync'):selftest.find('CreateMinimalEnvironment')],
      "stdio self-test launches the selected built host directly")
check('"--artifacts-path", artifacts.Root' in maincs and 'SelfTestArtifactLayout.Create()' in maincs,
      "Control Center validation uses isolated build artifacts")
check('Directory.Delete(Root, recursive: true)' in artifact_layout and 'EnsureContained(Root)' in artifact_layout,
      "Isolated artifact cleanup is containment guarded")
check('RepositoryRootEnvironmentVariable' in runtime and 'ArtifactsPathEnvironmentVariable' in runtime and
      'TestRepositoryLocator' in integration_test_support and 'TestArtifactLocator' in integration_test_support,
      "Isolated tests receive explicit repository and artifact locations")
check('EngineeringMcp.Host.exe' in (ROOT / 'src/EngineeringMcp.ControlCenter/ProjectLayout.cs').read_text(encoding='utf-8'),
      "Control Center launches built host executable")
check('startInfo.ArgumentList.Add("http")' in maincs and 'StartMcpServerAsync' in maincs,
      "Run MCP Server starts background HTTP host")
check('["type"] = "http"' in maincs and '["url"] = McpRuntimeDefaults.McpEndpoint' in maincs,
      "VS Code integration writes HTTP user-profile entry")
check('_liveMcpClient' not in maincs and 'OpenSessionAsync' not in maincs,
      "Old Control Center-owned stdio session removed")

# Workspace config should point to HTTP, never workspace-relative host process.
workspace_cfg = json.loads((ROOT / '.vscode/mcp.json').read_text(encoding='utf-8'))
server = workspace_cfg.get('servers', {}).get('dotnetWpfEngineering', {})
check(server.get('type') == 'http' and server.get('url') == 'http://127.0.0.1:8765/mcp',
      "Workspace MCP example uses shared HTTP endpoint")
check(server.get('headers', {}).get('X-Engineering-Mcp-Client') == 'vscode',
      "Workspace MCP example identifies live VS Code traffic")
check('${workspaceFolder}' not in json.dumps(workspace_cfg),
      "Workspace MCP example has no repository-path coupling")

# Four-tab UX and event handlers.
tabs = re.findall(r'<TabItem(?:\s+x:Name="[^"]+")?\s+Header="([^"]+)"', xaml)
check(tabs == ['Home', 'Self Test', 'Integration', 'Logs'], "Control Center has exactly four simple tabs", str(tabs))
for label in ['Run MCP Server', 'Test MCP Server', 'Repair MCP Server', 'Connect to VS Code', 'MCP Server Logs']:
    check(f'Content="{label}"' in xaml, f'GUI action present: {label}')

handlers = set(re.findall(r'(?:Click|SelectionChanged)="([A-Za-z_][A-Za-z0-9_]*)"', xaml))
missing_handlers = [h for h in sorted(handlers) if re.search(rf'\b{re.escape(h)}\s*\(', maincs) is None]
check(not missing_handlers, "All XAML event handlers exist", ', '.join(missing_handlers))

# Guard against accidentally reintroducing a LAN bind.
check('0.0.0.0' not in program and 'http://+:' not in program and 'https://+:' not in program,
      "No wildcard HTTP bind")

tool_count = len(re.findall(r'\[McpServerTool\(Name\s*=\s*"', tool_sources))
structured_count = len(re.findall(r'\[McpServerTool\(Name\s*=\s*"[^\"]+"\s*,\s*UseStructuredContent\s*=\s*true', tool_sources))
check(tool_count > 0 and structured_count == tool_count,
      "Every MCP tool opts into structured content", f"{structured_count}/{tool_count}")
check('AddListToolsFilter' in (ROOT / 'src/EngineeringMcp.Host/McpContractFilters.cs').read_text(encoding='utf-8') and
      'AddCallToolFilter' in (ROOT / 'src/EngineeringMcp.Host/McpContractFilters.cs').read_text(encoding='utf-8'),
      "Central list/call contract filters are installed")

pipe_sources = '\n'.join((ROOT / path).read_text(encoding='utf-8') for path in [
    'src/EngineeringMcp.Probe.Wpf/WpfProbeServer.cs',
    'src/EngineeringMcp.Wpf/WpfProbeClient.cs',
    'tests/EngineeringMcp.AspNetCore.TestApp/BackendDiagnostics.cs',
    'src/EngineeringMcp.Diagnostics/BackendProbeClient.cs'])
check('BoundedJsonPipeProtocol' in pipe_sources and 'ReadLineAsync' not in pipe_sources,
      "Diagnostic IPC uses bounded framed JSON")

for policy_name in ['policy.example.json', 'policy.vscode-test.json']:
    policy = json.loads((ROOT / 'config' / policy_name).read_text(encoding='utf-8'))
    check(policy.get('$schema') == './policy.schema.json' and policy.get('policyVersion') == 1,
          f"Versioned policy schema declared: {policy_name}")

check((ROOT / 'build/release-hardening.ps1').exists(),
      "Local release hardening automation exists")

print(f"\nStatic self-test: {len(passes)} passed, {len(failures)} failed")
if failures:
    for failure in failures:
        print(' - ' + failure)
    sys.exit(1)
