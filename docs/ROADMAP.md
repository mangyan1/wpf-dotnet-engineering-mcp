# Authoritative Roadmap

Phases are sequential security gates. Later phases may be prototyped on branches, but must not be advertised as available until prior gates pass.

## Phase 0 — Governance

Deliver charter, security policy, threat model, data classification, capabilities, tool contracts, ADR process and roadmap.

**Gate:** governance exists and no implementation contradicts it.

## Phase 1 — Secure MCP Core

Implement .NET 10 host, official C# MCP SDK integration, capability registry, configuration, policy primitives, audit contracts, redaction core, structured errors, cancellation/timeouts.

Initial tools: `system_version`, `system_health`, `system_capabilities`, `system_permissions`.

**Gate:** unauthorized capability requests are rejectable by policy; no unavailable capability is advertised.

## Phase 2 — WPF Read-Only UIA

Add FlaUI/UIA3 adapter: allowlisted attach, windows, snapshot/query/find, semantic selectors.

**Gate:** fixture UI can be inspected repeatedly without coordinate dependence and no unrelated process can be attached.

## Phase 3 — Sensitive-Data Protection

Harden classification, secrets/PII scanning, UI text sanitization, screenshot masking, audit redaction.

**Gate:** seeded credentials never reach the MCP client through text, logs, exceptions or screenshots.

## Phase 4 — WPF Interaction

Click/type/select/toggle/expand/scroll/wait/assert with risk classification and destructive-action protection.

**Gate:** deterministic semantic workflows pass repeated fixture runs; destructive controls are blocked absent policy.

## Phase 5 — WPF Probe

Explicit in-process package with authenticated local IPC for DataContext, bindings/errors, commands/CanExecute, validation, logical/visual tree, dependency properties/resources and dispatcher diagnostics.

**Gate:** no arbitrary reflection/method execution, no external listener, no secret leakage.

## Phase 6 — WPF-UI Adapter

Theme/resource origin/effective style and design-token audits.

**Gate:** seeded hardcoded/incorrect WPF-UI resource violations are correctly detected.

## Phase 7 — GUI & Accessibility

Clipping/overlap/focus/automation names/patterns/keyboard navigation/contrast where measurable.

**Gate:** seeded fixture defects are detected with reproducible evidence.

## Phase 8 — .NET Runtime Diagnostics

EventPipe/runtime client integration: runtime info, exceptions, counters, traces, GC/thread summaries.

**Gate:** exception triggered from a WPF action can be correlated to that action without exposing unrelated sensitive payloads.

## Phase 9 — Source Intelligence

Roslyn/MSBuild/XAML/symbol mapping for definitions, references, bindings, commands, stack traces and UI-to-source mapping inside allowlisted roots.

**Gate:** known fixture stacks/UI elements map to correct source with file/line evidence where symbols permit.

## Phase 10 — Failure Correlation

Orchestrators `diagnose_click` and `diagnose` produce an evidence graph across UI → command → runtime → network/backend → source. Only `diagnose_click` performs an action.

**Gate:** seeded failures produce OBSERVED/CORRELATED/INFERRED/UNKNOWN output with no unsupported root-cause claims.

## Phase 11 — ASP.NET Adapter

Optional, explicitly configured health/request/exception/log/metric/OpenTelemetry correlation.

**Gate:** absent adapter returns capability unavailable; it never fabricates backend state.

## Phase 12 — ClrMD / Dumps

Privileged local dump/stack/heap summary and tightly limited object inspection.

**Gate:** Level-4 policy required, dumps remain local/restricted, raw sensitive memory never crosses MCP boundary.

## Phase 13 — UX Heuristics

Workflow/recovery/navigation/feedback review. Every result labelled `HEURISTIC` and never a sole security/build gate.

## Phase 14 — Production Hardening

Code signing, SBOM, dependency scanning, provenance, fuzzing, resource-exhaustion tests, audit retention, policy/config versioning and threat-model review.

**Implemented 2026-08-26:** locked NuGet dependency graphs, Windows local build/test/static/vulnerability gates, SPDX SBOM/dependency inventory/checksums, optional Authenticode signing with a mandatory-release switch, versioned/validated policies, sticky audit-health denial, bounded HTTP/IPC, and oversized-frame tests. Official release signing and externally attested provenance still require operator certificate/identity configuration or a future trusted build service; the repository cannot manufacture those credentials.

## Do not build first

Full debugger, arbitrary shell, source edits, autonomous fixing, database integration, unrestricted backend queries, arbitrary process inspection, broad filesystem access.
