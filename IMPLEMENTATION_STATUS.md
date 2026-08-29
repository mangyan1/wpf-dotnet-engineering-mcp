# Implementation Status

Last updated: 2026-08-29

Current source version: 0.3.7-preview.3. This preview carries universal bounded WPF workspace discovery and authorization, inert centralized-property import discovery, verified manual executable fallback, target-only DPI-aware screenshot capture, conservative text masking, provider-chrome audit corrections, Apache-2.0 licensing, and the associated regression coverage.

## Verification state

Verified on Windows on 2026-08-28 with .NET SDK 10.0.400:

- `dotnet build DotNetEngineeringMcp.sln --no-restore`: passed with 0 warnings and 0 errors.
- `dotnet test DotNetEngineeringMcp.sln --no-restore --configuration Release`: 29 normal-suite tests passed and the opt-in installed-package acceptance test skipped by design; the updated installed acceptance passed separately after exercising install, uninstall, and reinstall.
- Release hardening produced the self-contained 0.3.5 ZIP and MSI with zero installer warnings/errors and required development self-signed, timestamped Authenticode signatures.
- Live authenticated Streamable HTTP initialization and `tools/list`: HTTP 200, protocol `2025-06-18`, 76 tools.
- Live contract gate: every tool has an output schema, title, annotations, and descriptions for every input property; a deterministic domain failure returned MCP `isError=true`.
- WPF runtime smoke: allowlisted attach succeeded, a 50-element semantic snapshot succeeded, framed WPF probe status succeeded, and screenshot output contained one native MCP image block plus metadata with no structured base64 duplicate.
- The rebuilt installed 0.3.5 candidate passed VS Code-style initialization, 76-tool discovery, exact-tool preflight, durable-policy persistence, runtime diagnostics, and fail-closed privileged denial checks.
- The real MSI install/uninstall/reinstall lifecycle preserved the durable policy and VS Code configuration byte-for-byte.
- Live tool-name contract: 0 invalid names, 0 dotted names, `wpf_attach` present, legacy `wpf.attach` absent.
- Authentication negative checks: missing and invalid bearer tokens both returned HTTP 401.

Additional source verification on 2026-08-29:

- `dotnet test DotNetEngineeringMcp.sln --no-restore --configuration Release`: 46 normal tests passed and the opt-in installed-package acceptance test skipped by design.
- Ten universal workspace-policy tests passed for multiple modern WPF apps, classic WPF metadata, safe centralized imports, external-import rejection, verified manual executable fallback, non-WPF exclusion, unbuilt-project failure, invalid-root rejection, and stable path-specific policy files.
- A real WPF fixture produced a valid masked PNG with UIA text/sensitive-region redactions, and capture remained fail-closed.
- The authenticated WPF probe completed a request, disposed, restarted, and completed another request without a stale singleton or pipe timeout.
- A live ASP.NET fixture recorded an HTTP request and returned it through the authenticated pipe with an exact diagnostic-action correlation marker.
- One-file XAML audits were limited to the selected file, and redaction preserved ISO timestamps, dotted versions, and target-framework path fragments while still masking realistic phone numbers.
- Release hardening produced the timestamped development-self-signed `EngineeringMcp-0.3.7-preview.3-win-x64.zip` and `EngineeringMcp-0.3.7-preview.3-win-x64-Setup.msi`; the manifest reports version `0.3.7-preview.3` on the `preview` channel and packages the Apache-2.0 license, White-Lotus notice, and universal WPF workspace guide.
- The final MSI passed install, uninstall, reinstall, durable policy/VS Code preservation, and installed 76-tool acceptance. The installed host reports `0.3.7-preview.3`.
- All 51 static contract/security checks passed, including product-neutrality and inert centralized-property/manual-executable authorization gates.
- A real application integration fixture returned backend adapter status `ready` with one bounded request observation and a selector audit of 43/43 stable actionable selectors with zero missing or duplicate IDs; the fixture remains external to the universal MCP product.

| Area | Status | Notes |
|---|---|---|
| Governance/security docs | IMPLEMENTED | Charter, security, threat model, capabilities, tool contracts, ADR rules |
| .NET 10 solution | IMPLEMENTED, VERIFIED | Fresh Windows build green with zero warnings |
| Official C# MCP SDK server | IMPLEMENTED | `ModelContextProtocol` 2.2.0 |
| System MCP tools | IMPLEMENTED | version/health/capabilities/permissions/policy diagnostics/exact-tool preflight |
| Policy/process/filesystem guardrails | IMPLEMENTED | default-deny security control plane |
| Redaction/audit | IMPLEMENTED | secret/PII redaction and structured audit path |
| WPF UIA/FlaUI | IMPLEMENTED | semantic read and interaction tool surface |
| Advanced WPF metadata tools | IMPLEMENTED, VERIFIED | 21 read-only tools; synthetic leak checks prove no UI text, business values, raw identifiers, titles, validation messages, clipboard, or raw screenshot output |
| Screenshot redaction | IMPLEMENTED, RUNTIME VERIFIED, DEFAULT OFF | Password, text-bearing, and policy-sensitive UIA regions masked; policy opt-in due to custom-rendering/OCR residual risk |
| WPF in-process probe | IMPLEMENTED, RESTART VERIFIED | Explicit authenticated named-pipe probe; bounded retry and actionable not-installed result; no injection/arbitrary reflection API |
| WPF-UI adapter | IMPLEMENTED | resource/property/theme evidence and audits |
| GUI/A11y | IMPLEMENTED | deterministic audit surfaces |
| EventPipe diagnostics | IMPLEMENTED | two concurrent traces maximum; 64 MiB/30-second bounds; managed cleanup |
| Source intelligence | IMPLEMENTED, VERIFIED | Roslyn/XAML/source mapping layer; XAML operations accept one approved file or directory |
| Failure correlation | IMPLEMENTED, VERIFIED | Read-only observe/failure/workflow plus risk-gated click diagnosis with exact backend action markers and labelled time-window fallback |
| ASP.NET adapter | IMPLEMENTED, PIPE/ACTION-CORRELATION VERIFIED | Reusable opt-in middleware and authenticated local probe; bounded route metadata only, no bodies, headers, cookies, or query strings |
| ClrMD/dump analysis | IMPLEMENTED, PRIVILEGED | policy-gated sensitive diagnostic path |
| UX heuristics | IMPLEMENTED | explicitly heuristic output |
| VS Code integration | IMPLEMENTED | authenticated HTTP definition and environment-backed bearer token |
| Universal WPF workspace authorization | IMPLEMENTED | bounded project discovery identifies built `UseWPF=true` executable projects, writes distinct exact-path per-workspace policies outside the install directory, and restarts MCP without weakening packaged default-deny behavior |
| Actionable policy denials | IMPLEMENTED, VERIFIED | structured remediation field, safe system policy diagnostic report, and Control Center Policy Readiness card |
| Child environment sanitization | IMPLEMENTED, VERIFIED | local absolute PATH entries only; relative, duplicate, and UNC/network entries are removed before child launch |
| Codex integration | IMPLEMENTED, VERIFIED CONFIG | global `dotnetWpfEngineering` entry uses bearer-token environment variable |
| Developer Control Center | IMPLEMENTED, BUILD VERIFIED | authenticated MCP self-test + WPF end-to-end button-driven lab + policy selection |
| Protocol hardening | IMPLEMENTED, VERIFIED | structured output/error signaling, schemas, annotations, native images, progress, pagination |
| Production packaging | IMPLEMENTED, VERIFIED | locked dependencies, local release hardening, SPDX SBOM, checksums, optional/required Authenticode gate, development self-sign mode, and installed lifecycle acceptance |

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
