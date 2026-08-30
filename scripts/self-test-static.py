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
maincs = "\n".join(path.read_text(encoding="utf-8") for path in
                   sorted((ROOT / "src/EngineeringMcp.ControlCenter").glob("MainWindow*.cs")))
xaml = (ROOT / "src/EngineeringMcp.ControlCenter/MainWindow.xaml").read_text(encoding="utf-8")
runtime = (ROOT / "src/EngineeringMcp.Contracts/McpRuntimeDefaults.cs").read_text(encoding="utf-8")
artifact_layout = (ROOT / "src/EngineeringMcp.ControlCenter/SelfTestArtifactLayout.cs").read_text(encoding="utf-8")
integration_test_support = "\n".join(path.read_text(encoding="utf-8") for path in
                                     (ROOT / "tests/EngineeringMcp.IntegrationTests").glob("Test*Locator*.cs"))
tool_sources = "\n".join(path.read_text(encoding="utf-8") for path in (ROOT / "src/EngineeringMcp.Host").glob("*Tools.cs"))

central_package_versions = set(re.findall(r'<PackageVersion\s+Include="([^"]+)"', packages))
unversioned_package_references: set[str] = set()
for project_path in [*ROOT.rglob('*.csproj'), *ROOT.rglob('*.wixproj')]:
    if 'bin' in project_path.parts or 'obj' in project_path.parts:
        continue
    project_root = ET.parse(project_path).getroot()
    for reference in project_root.findall('.//PackageReference'):
        package_id = reference.get('Include')
        has_inline_version = reference.get('Version') is not None or reference.find('Version') is not None
        if package_id and not has_inline_version:
            unversioned_package_references.add(package_id)
missing_central_versions = sorted(unversioned_package_references - central_package_versions)
check(not missing_central_versions,
      "Every unversioned NuGet reference has a central version",
      ', '.join(missing_central_versions))

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
check('["type"] = "http"' in maincs and '["url"] = McpRuntimeDefaults.VsCodeMcpEndpoint' in maincs,
      "VS Code integration writes HTTP user-profile entry")
check('_liveMcpClient' not in maincs and 'OpenSessionAsync' not in maincs,
      "Old Control Center-owned stdio session removed")

# Workspace config should point to HTTP, never workspace-relative host process.
workspace_cfg = json.loads((ROOT / '.vscode/mcp.json').read_text(encoding='utf-8'))
server = workspace_cfg.get('servers', {}).get('dotnetWpfEngineering', {})
check(server.get('type') == 'http' and server.get('url') == 'http://127.0.0.1:8765/mcp?vscode',
      "Workspace MCP example uses shared HTTP endpoint")
check(server.get('headers', {}).get('X-Engineering-Mcp-Client') == 'vscode',
      "Workspace MCP example identifies live VS Code traffic")
check('${workspaceFolder}' not in json.dumps(workspace_cfg),
      "Workspace MCP example has no repository-path coupling")

# Dashboard pages and event handlers.
tabs = re.findall(r'<TabItem(?:\s+x:Name="[^"]+")?\s+Header="([^"]+)"', xaml)
check(tabs == ['Home', 'Validation', 'Integration', 'Tools', 'Logs', 'Security'],
      "Control Center has the expected six dashboard pages", str(tabs))
for label in ['Run MCP Server', 'Test MCP Server', 'Repair MCP Server', 'Connect to VS Code', 'MCP Server Logs']:
    check(f'Content="{label}"' in xaml, f'GUI action present: {label}')
check('Text="Authorize WPF workspace"' in xaml and 'AuthorizeWpfWorkspace_Click' in maincs,
      'GUI action present: Authorize WPF workspace')
check('x:Name="VersionText"' in xaml and
      'InitializeBuildIdentity();' in maincs and
      'AssemblyInformationalVersionAttribute' in maincs,
      'Control Center displays assembly-derived product version and build revision')
check('x:Name="PolicyDiagnosticsText"' in xaml and 'POLICY READINESS' in xaml,
      'Control Center exposes local policy readiness guidance')

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
check(tool_count == 76 and 'system_policy_diagnostics' in tool_sources and
      'system_tool_preflight' in tool_sources and
      'wpf_grid_summary' in tool_sources and 'wpf_validation_summary' in tool_sources,
      "76-tool surface includes authoritative preflight and safe advanced WPF diagnostics", str(tool_count))
check('AddListToolsFilter' in (ROOT / 'src/EngineeringMcp.Host/McpContractFilters.cs').read_text(encoding='utf-8') and
      'AddCallToolFilter' in (ROOT / 'src/EngineeringMcp.Host/McpContractFilters.cs').read_text(encoding='utf-8'),
      "Central list/call contract filters are installed")

pipe_sources = '\n'.join((ROOT / path).read_text(encoding='utf-8') for path in [
    'src/EngineeringMcp.Probe.Wpf/WpfProbeServer.cs',
    'src/EngineeringMcp.Wpf/WpfProbeClient.cs',
    'src/EngineeringMcp.Diagnostics/AspNetCoreAdapter.cs',
    'src/EngineeringMcp.Diagnostics/BackendProbeClient.cs'])
check('BoundedJsonPipeProtocol' in pipe_sources and 'ReadLineAsync' not in pipe_sources,
      "Diagnostic IPC uses bounded framed JSON")

for policy_name in ['policy.example.json', 'policy.vscode-test.json']:
    policy = json.loads((ROOT / 'config' / policy_name).read_text(encoding='utf-8'))
    check(policy.get('$schema') == './policy.schema.json' and policy.get('policyVersion') == 1,
          f"Versioned policy schema declared: {policy_name}")

check((ROOT / 'build/release-hardening.ps1').exists(),
      "Local release hardening automation exists")
license_text = (ROOT / 'LICENSE').read_text(encoding='utf-8')
notice_text = (ROOT / 'NOTICE').read_text(encoding='utf-8')
build_props = (ROOT / 'Directory.Build.props').read_text(encoding='utf-8')
release_script = (ROOT / 'build/release-hardening.ps1').read_text(encoding='utf-8')
installer_source = (ROOT / 'installer/Package.wxs').read_text(encoding='utf-8')
license_metadata = '\n'.join([notice_text, build_props, release_script, installer_source,
                               (ROOT / 'README.md').read_text(encoding='utf-8')])
vscode_manifest = json.loads((ROOT / 'vscode-extension/package.json').read_text(encoding='utf-8'))
check('Apache License' in license_text and 'Version 2.0, January 2004' in license_text and
      'END OF TERMS AND CONDITIONS' in license_text,
      "Apache-2.0 license text is installed")
check('Copyright 2026 mangyan1' in notice_text and
      '<Authors>mangyan1</Authors>' in build_props and
      '<PackageLicenseExpression>Apache-2.0</PackageLicenseExpression>' in build_props and
      vscode_manifest.get('license') == 'Apache-2.0',
      "mangyan1 attribution and SPDX project metadata are installed")
check("licenseDeclared = 'Apache-2.0'" in release_script and
      "Copy-Item -LiteralPath $noticePath" in release_script,
      "Release SBOM and payload declare Apache-2.0")
signing_policy = (ROOT / 'docs/CODE-SIGNING-POLICY.md').read_text(encoding='utf-8')
check('Code signing policy' in signing_policy and
      'Free code signing provided by SignPath.io, certificate by SignPath Foundation' in signing_policy and
      'docs/CODE-SIGNING-POLICY.md' in release_script,
      "Code-signing policy and packaged trust-path disclosure are installed")
check(not any(marker in license_metadata.lower() for marker in [
          'source-available', 'not open source', 'non-commercial',
          'licenseref-']),
      "Legacy restrictive license metadata is absent")
check((ROOT / 'scripts/test-installed-vscode.ps1').exists(),
      "Installed-package VS Code acceptance automation exists")
ci_workflow = (ROOT / '.github/workflows/ci.yml').read_text(encoding='utf-8')
ci_actions = re.findall(r'uses:\s+actions/(?:checkout|setup-dotnet)@([0-9a-f]{40})', ci_workflow)
check('permissions:\n  contents: read' in ci_workflow and
      'pull_request_target' not in ci_workflow and
      'persist-credentials: false' in ci_workflow and
      'dotnet restore DotNetEngineeringMcp.sln --runtime win-x64 --locked-mode' in ci_workflow and
      'dotnet restore installer/EngineeringMcp.Installer.wixproj --locked-mode' in ci_workflow and
      len(ci_actions) == 2,
      "Public CI is read-only, lock-file-driven, and pins official actions by commit")
dependabot = (ROOT / '.github/dependabot.yml').read_text(encoding='utf-8')
check('package-ecosystem: nuget' in dependabot and
      '- "/installer"' in dependabot and
      '- "/src/*"' in dependabot and
      '- "/tests/*"' in dependabot and
      'group-by: dependency-name' in dependabot and
      'package-ecosystem: npm' in dependabot and
      'directory: "/vscode-extension"' in dependabot and
      'package-ecosystem: github-actions' in dependabot and
      (ROOT / '.github/CODEOWNERS').exists() and
      (ROOT / 'CONTRIBUTING.md').exists(),
      "Dependabot covers NuGet, installer, VS Code, and GitHub Actions dependencies")
vulnerability_script = (ROOT / 'scripts/Test-NuGetVulnerabilities.ps1').read_text(encoding='utf-8')
check('Test-NuGetVulnerabilities.ps1' in ci_workflow and
      'DotNetEngineeringMcp.sln' in vulnerability_script and
      'EngineeringMcp.Installer.wixproj' in vulnerability_script and
      '--include-transitive' in vulnerability_script,
      "CI rejects known direct and transitive NuGet vulnerabilities")
dependency_review = (ROOT / '.github/workflows/dependency-review.yml').read_text(encoding='utf-8')
dependency_review_actions = re.findall(r'uses:\s+[^\s@]+@([0-9a-f]{40})', dependency_review)
check('pull_request_target' not in dependency_review and
      'permissions:\n  contents: read' in dependency_review and
      'actions/dependency-review-action@' in dependency_review and
      'fail-on-severity: moderate' in dependency_review and
      len(dependency_review_actions) == 2,
      "Pull requests receive pinned, read-only GitHub dependency review")
check('AllowSameVersionUpgrades="yes"' in (ROOT / 'installer/Package.wxs').read_text(encoding='utf-8'),
      "MSI replaces same-version development builds")
check('ICE61' in (ROOT / 'installer/EngineeringMcp.Installer.wixproj').read_text(encoding='utf-8'),
      "Same-version MSI validation exception is explicitly scoped")
check('ProcessEnvironmentSanitizer.SanitizePathInPlace' in selftest and
      'remediation' in (ROOT / 'src/EngineeringMcp.Contracts/SecurityModels.cs').read_text(encoding='utf-8').lower(),
      "Child environment sanitization and actionable failure contract are wired")

workspace_provisioner = (ROOT / 'src/EngineeringMcp.Security/WpfWorkspacePolicyProvisioner.cs').read_text(encoding='utf-8')
check('MaximumDirectories = 4096' in workspace_provisioner and
      'FileAttributes.ReparsePoint' in workspace_provisioner and
      'UseWPF' in workspace_provisioner and
      'ProjectTypeGuids' in workspace_provisioner and
      'GetDefaultPolicyPath' in workspace_provisioner,
      "Universal bounded WPF workspace authorization is installed")
check('MaximumImportedProjectFiles = 32' in workspace_provisioner and
      'Directory.Build.props' in workspace_provisioner and
      'DtdProcessing.Prohibit' in workspace_provisioner and
      'ProvisionExecutable' in workspace_provisioner and
      'PEReader' in workspace_provisioner,
      "Centralized WPF properties and manual executable fallback remain inert and verified")

neutrality_roots = [ROOT / 'src', ROOT / 'tests', ROOT / 'config', ROOT / 'docs', ROOT / 'scripts']
neutrality_files = [ROOT / 'README.md', ROOT / 'IMPLEMENTATION_STATUS.md']
for neutrality_root in neutrality_roots:
    neutrality_files.extend(path for path in neutrality_root.rglob('*')
                            if path.is_file() and path != ROOT / 'scripts/self-test-static.py' and
                            'bin' not in path.parts and 'obj' not in path.parts)
product_specific_paths = []
for path in neutrality_files:
    try:
        if 'apexdrive' in path.read_text(encoding='utf-8').lower():
            product_specific_paths.append(str(path.relative_to(ROOT)))
    except UnicodeDecodeError:
        continue
check(not product_specific_paths,
      "Universal product surface contains no ApexDrive coupling",
      ', '.join(sorted(set(product_specific_paths))))

print(f"\nStatic self-test: {len(passes)} passed, {len(failures)} failed")
if failures:
    for failure in failures:
        print(' - ' + failure)
    sys.exit(1)
