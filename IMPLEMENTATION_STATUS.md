# Implementation Status

Last updated: 2026-08-26

## Verification state

Verified on Windows on 2026-08-26 with .NET SDK 10.0.400:

- `dotnet build DotNetEngineeringMcp.sln --no-restore`: passed with 0 warnings and 0 errors.
- `dotnet test DotNetEngineeringMcp.sln --configuration Release --no-build`: 15 tests passed across security, adversarial, and integration suites, including an approved-root MSBuild/Roslyn semantic-reference resolution test.
- Live authenticated Streamable HTTP initialization and `tools/list`: HTTP 200, protocol `2025-06-18`, 68 tools.
- Live contract gate: every tool has an output schema, title, annotations, and descriptions for every input property; a deterministic domain failure returned MCP `isError=true`.
- WPF runtime smoke: allowlisted attach succeeded, a 50-element semantic snapshot succeeded, framed WPF probe status succeeded, and screenshot output contained one native MCP image block plus metadata with no structured base64 duplicate.
- The rebuilt Debug Control Center owns the rebuilt Debug host at `127.0.0.1:8765`; post-restart live discovery confirmed 68 tools, 204/204 described inputs, and complete schema/title/annotation coverage.
- Live tool-name contract: 0 invalid names, 0 dotted names, `wpf_attach` present, legacy `wpf.attach` absent.
- Authentication negative checks: missing and invalid bearer tokens both returned HTTP 401.

| Area | Status | Notes |
|---|---|---|
| Governance/security docs | IMPLEMENTED | Charter, security, threat model, capabilities, tool contracts, ADR rules |
| .NET 10 solution | IMPLEMENTED, VERIFIED | Fresh Windows build green with zero warnings |
| Official C# MCP SDK server | IMPLEMENTED | `ModelContextProtocol` 2.2.0 |
| System MCP tools | IMPLEMENTED | version/health/capabilities/permissions |
| Policy/process/filesystem guardrails | IMPLEMENTED | default-deny security control plane |
| Redaction/audit | IMPLEMENTED | secret/PII redaction and structured audit path |
| WPF UIA/FlaUI | IMPLEMENTED | semantic read and interaction tool surface |
| Screenshot redaction | IMPLEMENTED, DEFAULT OFF | PII-aware UIA masking; policy opt-in due to custom-rendering/OCR residual risk |
| WPF in-process probe | IMPLEMENTED | explicit named-pipe probe, no injection/arbitrary reflection API |
| WPF-UI adapter | IMPLEMENTED | resource/property/theme evidence and audits |
| GUI/A11y | IMPLEMENTED | deterministic audit surfaces |
| EventPipe diagnostics | IMPLEMENTED | two concurrent traces maximum; 64 MiB/30-second bounds; managed cleanup |
| Source intelligence | IMPLEMENTED | Roslyn/XAML/source mapping layer |
| Failure correlation | IMPLEMENTED | read-only observe/failure/workflow plus risk-gated click diagnosis |
| ASP.NET adapter | IMPLEMENTED | optional backend probe/observability layer |
| ClrMD/dump analysis | IMPLEMENTED, PRIVILEGED | policy-gated sensitive diagnostic path |
| UX heuristics | IMPLEMENTED | explicitly heuristic output |
| VS Code integration | IMPLEMENTED | authenticated HTTP definition and environment-backed bearer token |
| Codex integration | IMPLEMENTED, VERIFIED CONFIG | global `dotnetWpfEngineering` entry uses bearer-token environment variable |
| Developer Control Center | IMPLEMENTED, BUILD VERIFIED | authenticated MCP self-test + WPF end-to-end button-driven lab + policy selection |
| Protocol hardening | IMPLEMENTED, VERIFIED | structured output/error signaling, schemas, annotations, native images, progress, pagination |
| Production packaging | IMPLEMENTED, VERIFIED | locked dependencies, CI, SPDX SBOM, checksums, optional/required Authenticode gate |

## Developer Control Center verification target

After extracting this revision on Windows, double-click `Start-ControlCenter.cmd`. Then use **Run all dev tests**. A successful run is the preferred acceptance gate because it performs build/tests plus a real MCP client/server and WPF runtime path rather than only checking whether the host process stays alive.

## Control Center UX

- Simplified developer GUI: Home / Tests / Integrations / Logs.
- Home emphasizes one-click full validation, current subsystem status, quick tests, and latest evidence.
- Light, Dark, and System theme modes are available at runtime. System is the default.
- Advanced VS Code/security details are moved out of the primary workflow.

## Shared MCP transport update

Implemented in source:
- one `EngineeringMcp.Host` binary with `--transport http` and `--transport stdio`;
- Streamable HTTP endpoint `http://127.0.0.1:8765/mcp`;
- loopback peer + Host-header enforcement and no CORS;
- Control Center-owned background HTTP process;
- Control Center MCP self-test over the shared HTTP endpoint;
- full validation includes stdio compatibility;
- VS Code user-profile integration now points to the shared HTTP endpoint instead of launching a second stdio process.

The WPF fixture's full desktop interaction path remains a separate operator-run gate through **Run all dev tests** in the Control Center. The protocol and live tool-discovery path were verified independently against the rebuilt host.
