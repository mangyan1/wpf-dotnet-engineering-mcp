# .NET/WPF Engineering MCP

## Runtime model

Normal development now uses **one shared local MCP service**. Start it from the Developer Control Center with **Run MCP Server**. The host listens only on `http://127.0.0.1:8765/mcp`; both the Control Center self-tests and editor clients connect to that same process using a per-user bearer token. The host still supports `--transport stdio` for compatibility clients.

**Connect to VS Code** installs a user-profile HTTP MCP entry, so ApexDrive and other workspaces see the same server without opening this repository. On first launch, the Control Center creates `ENGINEERING_MCP_HTTP_TOKEN` in the current Windows user's environment without displaying it. Fully restart VS Code after that first launch so it inherits the token. The Control Center owns the local service lifetime and stops it when the Control Center closes.

## License

Copyright (c) 2026 White-Lotus. All rights reserved. This project is source-available, not open source. The repository license permits personal and non-commercial use by individuals. It also permits internal development and DevOps use, including revenue-generating work, by qualifying small developers with no more than five workers and no more than USD 100,000 in annual gross revenue. Other organizational or commercial use requires prior written permission from White-Lotus. Redistribution, hosted services, product integration, and commercial AI/ML training remain prohibited. See `LICENSE` for the complete controlling terms. Third-party components remain under their respective licenses.

## Developer Control Center (recommended)

For normal development, do not type routine build/test/MCP commands. Double-click `Start-ControlCenter.cmd` in the repository root.

The Dev Lab can run the complete local validation path with buttons: solution build, automated tests, a real MCP stdio client/server self-test, WPF fixture launch, FlaUI/UIA attach and snapshot, semantic interaction/assertion, WPF probe checks, and sanitized screenshot verification. In-process build, test, readiness, and end-to-end actions compile into a unique temporary artifacts directory. Runtime validation executes every transport and fixture from that same fresh build, then removes the artifacts and restores the previous local MCP runtime state. This prevents Windows file locks from the running Control Center or host from invalidating validation. VS Code is tested after the MCP itself is known-good.

Optional: double-click `Install-ControlCenter-Shortcut.cmd` once to create a Desktop shortcut. See `docs/DEV-CONTROL-CENTER.md`.

## Standalone Windows app

Create a self-contained Windows package with:

```powershell
powershell -ExecutionPolicy Bypass -File build/release-hardening.ps1
```

The resulting `artifacts/release/EngineeringMcp-<version>-win-x64.zip` includes the branded Control Center, its private MCP host, a locked-down default policy, security/VS Code documentation, an SPDX SBOM, and the .NET runtime. Extract the complete folder and run `EngineeringMcp.ControlCenter.exe`; installing .NET or opening the source repository is not required.

The same release command also creates `EngineeringMcp-<version>-win-x64-Setup.msi`. The per-user installer requires no elevation, installs under `%LOCALAPPDATA%\Programs\Engineering MCP`, adds Start Menu and Desktop shortcuts, and supports standard Windows Installer repair, upgrade, and uninstall operations. Application files and shortcuts are removed on uninstall; user-level MCP configuration and security tokens are preserved intentionally.

Standalone mode keeps live MCP server control, protocol testing, policy selection, and the global **Connect to VS Code** action. Source builds, fixtures, and repository validation remain available only when the Control Center is launched from this checkout. The package manifest contains a reserved stable-channel update field, but automatic updating is intentionally inactive until a trusted release feed is configured.

For a durable ApexDrive integration, use **Configure ApexDrive** in the Control Center and select the
repository root. This explicit action generates a least-privilege per-user policy outside the install
directory, activates it, and restarts the MCP host. The policy and environment selection survive an
uninstall/reinstall; the installer itself continues to default to metadata-only access.

## Control Center (recommended)

For normal local operation, use the WPF Control Center instead of typing maintenance commands. On Windows, double-click `Start-ControlCenter.cmd` or run the `EngineeringMcp.ControlCenter` project from Visual Studio.

The Control Center provides fixed buttons for build, tests, readiness checks, VS Code MCP configuration repair, opening the correct VS Code workspace, probing the MCP host, launching/stopping the WPF fixture, launching the WPF + ASP.NET fixture stack, and selecting/opening the active security policy. It intentionally does **not** expose an arbitrary command shell.

The separate `EngineeringMcp.Wpf.TestApp` is an automation fixture, not the MCP management UI; it intentionally contains controlled defects/fault injection for MCP tests.


Security-first MCP server for authorized WPF/.NET applications.

## Status

The implementation now includes the secure MCP host, WPF/FlaUI automation, bounded WPF and ASP.NET probes, WPF-UI inspection, .NET diagnostics, syntactic and MSBuild/Roslyn semantic source analysis, cross-layer failure observation, and test fixtures. Published MCP tool names use the portable `lowercase_with_underscores` contract required by Codex and other strict clients. Every published tool advertises structured output, parameter descriptions, a display title, and MCP behavior annotations.

## Frozen v1 scope

Provide controlled, auditable tools for:

- semantic WPF UI inspection and automation;
- optional WPF in-process diagnostics through an explicit probe;
- WPF-UI design-system inspection;
- .NET runtime diagnostics;
- source/XAML correlation;
- accessibility and GUI analysis;
- optional ASP.NET observability;
- secret/PII redaction and policy enforcement around every capability.

The server is **not** a general shell, arbitrary process inspector, credential extractor, remote administration agent, or unrestricted debugger.

Policy denials are actionable without becoming permissive: `system_policy_diagnostics` returns safe readiness findings, and structured failures include a remediation field naming the relevant policy setting or Control Center action. Child processes receive a sanitized local-only `PATH`; relative and UNC/network tool paths are removed before launch.

## Source-of-truth order

1. `docs/SECURITY.md`
2. `docs/CHARTER.md`
3. `docs/TOOL-CONTRACTS.md`
4. `docs/CAPABILITIES.md`
5. Accepted ADRs in `docs/ADR/`
6. Implementation
7. Tests
8. README/examples

If implementation conflicts with security policy, the implementation is defective.

## Current build status

The project files target .NET 10 and pin the official `ModelContextProtocol` package to 2.2.0. On 2026-08-28 the Windows Release build completed with zero warnings/errors, the security, adversarial, and integration suites passed, the authenticated 54-tool HTTP contract and MSBuild semantic-reference tests passed, and the static checks passed. Installed-package acceptance is available through `scripts/test-installed-vscode.ps1`, including an explicit install/uninstall/reinstall persistence mode. Full interactive WPF fixture coverage remains an operator-run Control Center gate.

## Release hardening

`build/release-hardening.ps1` publishes a self-contained `win-x64` Windows app folder, portable ZIP, and per-user MSI installer; emits an SPDX 2.3 SBOM; saves the transitive dependency inventory; and creates SHA-256 checksums. Run it locally after the build, test, static-check, and vulnerable-dependency gates. Set `ENGINEERING_MCP_SIGNING_THUMBPRINT` and pass `-RequireSigning` for an official signed release. Unsigned output must not be promoted as an official release.

For internal development builds, pass `-SelfSign -RequireSigning`. This creates or reuses a non-exportable `Engineering MCP Development` code-signing key in `Cert:\CurrentUser\My`, signs the Engineering MCP binaries with SHA-256 plus an RFC 3161 timestamp, and includes the public `.cer` in the package documentation. The certificate is not placed in Trusted Root automatically. Other machines must explicitly trust the included public certificate; this does not establish public publisher identity or Microsoft Defender SmartScreen reputation.

## VS Code

- The Control Center **Connect to VS Code** action installs the MCP at VS Code user-profile scope for cross-workspace use. `.vscode/mcp.json` remains a repository-local development example.
- `vscode-extension/` contains a development VS Code extension that registers the HTTP MCP server programmatically and requires `ENGINEERING_MCP_HTTP_TOKEN` (set by the Control Center in the user environment) to already be inherited by VS Code.
- See `docs/VSCODE.md` for the first runtime test.

See `docs/ROADMAP.md` for phase gates and `IMPLEMENTATION_STATUS.md` for exact completion state.
