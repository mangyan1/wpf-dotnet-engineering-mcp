# .NET/WPF Engineering MCP

## Runtime model

Normal development now uses **one shared local MCP service**. Start it from the Developer Control Center with **Run MCP Server**. The host listens only on `http://127.0.0.1:8765/mcp`; both the Control Center self-tests and editor clients connect to that same process using a per-user bearer token. The host still supports `--transport stdio` for compatibility clients.

**Connect to VS Code** installs a user-profile HTTP MCP entry, so ApexDrive and other workspaces see the same server without opening this repository. On first launch, the Control Center creates `ENGINEERING_MCP_HTTP_TOKEN` in the current Windows user's environment without displaying it. Fully restart VS Code after that first launch so it inherits the token. The Control Center owns the local service lifetime and stops it when the Control Center closes.

## Developer Control Center (recommended)

For normal development, do not type routine build/test/MCP commands. Double-click `Start-ControlCenter.cmd` in the repository root.

The Dev Lab can run the complete local validation path with buttons: solution build, automated tests, a real MCP stdio client/server self-test, WPF fixture launch, FlaUI/UIA attach and snapshot, semantic interaction/assertion, WPF probe checks, and sanitized screenshot verification. In-process build, test, readiness, and end-to-end actions compile into a unique temporary artifacts directory. Runtime validation executes every transport and fixture from that same fresh build, then removes the artifacts and restores the previous local MCP runtime state. This prevents Windows file locks from the running Control Center or host from invalidating validation. VS Code is tested after the MCP itself is known-good.

Optional: double-click `Install-ControlCenter-Shortcut.cmd` once to create a Desktop shortcut. See `docs/DEV-CONTROL-CENTER.md`.

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

The project files target .NET 10 and pin the official `ModelContextProtocol` package to 2.2.0. On 2026-08-26 the Windows Release build completed with zero warnings/errors, all 15 automated tests passed, the authenticated live HTTP contract and MSBuild semantic-reference tests passed, the static checks passed, and NuGet reported no known vulnerable packages from the configured sources. Full interactive WPF fixture coverage remains an operator-run Control Center gate.

## Release hardening

`build/release-hardening.ps1` publishes the Windows host and Control Center, emits an SPDX 2.3 SBOM, saves the transitive dependency inventory, and creates SHA-256 checksums. Run it locally after the build, test, static-check, and vulnerable-dependency gates. Set `ENGINEERING_MCP_SIGNING_THUMBPRINT` and pass `-RequireSigning` for an official signed release. Unsigned output must not be promoted as an official release.

## VS Code

- The Control Center **Connect to VS Code** action installs the MCP at VS Code user-profile scope for cross-workspace use. `.vscode/mcp.json` remains a repository-local development example.
- `vscode-extension/` contains a development VS Code extension that registers the HTTP MCP server programmatically and requires `ENGINEERING_MCP_HTTP_TOKEN` (set by the Control Center in the user environment) to already be inherited by VS Code.
- See `docs/VSCODE.md` for the first runtime test.

See `docs/ROADMAP.md` for phase gates and `IMPLEMENTATION_STATUS.md` for exact completion state.
