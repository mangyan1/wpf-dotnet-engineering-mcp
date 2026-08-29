# Tool Contracts

## Global requirements for every tool

Before a tool can be marked implemented, it must define:

- purpose and capability ID;
- permission level and risk class;
- target scope;
- input schema;
- output schema;
- timeouts/cancellation;
- result size limits;
- classification/redaction behavior;
- audit behavior;
- deterministic error codes;
- success/failure/adversarial/security tests.

Tools must never replace an error with a guessed result.

Every non-UNKNOWN application claim must state its provenance: `OBSERVED` (direct evidence), `CORRELATED` (timing co-occurrence, never causal fact), `INFERRED` (heuristic, explicitly labeled), or `UNKNOWN` (no evidence).

## MCP wire contract

- All tools use structured content and advertise an output schema.
- Every input property has a description, and every tool has a display title plus read-only/destructive/idempotent/open-world annotations.
- A domain `ToolResult` with `success=false` is emitted with MCP `isError=true`.
- Sanitized screenshots use native MCP image content; structured output contains metadata rather than a second base64 copy.
- Cancellation tokens are not exposed as schema inputs and are propagated into bounded waits, EventPipe, probes, semantic analysis, and diagnosis.
- Long semantic/diagnosis operations report MCP progress when the client supplies a progress token.
- Large source-reference results are available through `source_find_references_page` with `offset`, `pageSize`, and `nextOffset`.

## Initial system tools

### `system_version`

Returns server version/runtime metadata. Permission 0, READ.

### `system_health`

Returns local server readiness only. It must not imply target application health. Permission 0, READ.

### `system_capabilities`

Returns capability manifest. Permission 0, READ.

### `system_permissions`

Returns active permission ceiling and policy mode without revealing secrets. Permission 0, READ.

### `system_tool_preflight`

Accepts one exact public tool name and returns the authoritative publication and authorization state for the active policy and runtime capability registry. Permission 0, READ. Agents must call it before reporting that a tool is policy-disabled. `ALLOW` covers policy and runtime capability only; real invocations still enforce target, input, selector, adapter, screenshot, audit, and dynamic destructive-action checks.

## Public tool prefixes

Public MCP tool names must match `^[a-z0-9_-]+$` for VS Code compatibility. Use underscore prefixes: `wpf_`, `wpfui_`, `a11y_`, `gui_`, `ux_`, `dotnet_`, `source_`, `aspnet_`, `diagnose_`, and `system_`. Family tools retain an explicit `operation` discriminator (`wpf_probe`, `wpfui_inspect`) for advanced and backward-compatible access. Dedicated tools are added only for high-value workflows where a narrow schema materially improves discoverability or enforces a stricter metadata-only result contract.

## Metadata-only advanced WPF boundary

Advanced grid, tree, item, selector, accessibility, window, wait, and assertion tools must not return element names, AutomationIds, row labels, cell text, values, ViewModel property values, validation messages, clipboard content, or raw screenshots. They may return bounded counts, booleans, control/pattern types, geometry, opaque session references, and non-reversible identifier fingerprints. Probe-backed tools may expose code-level binding paths and CLR type names, but never evaluate commands or return bound runtime values.

Capability IDs are internal manifest identifiers and retain dotted names such as `wpf.uia.read` and `dotnet.eventpipe`.

## Selector order

1. AutomationId
2. stable application semantic ID
3. Name + ControlType
4. UIA relationship
5. structural selector
6. coordinates only as a last resort and explicitly labelled as fragile

## Diagnosis result vocabulary

Every diagnosis uses `OBSERVED`, `CORRELATED`, `INFERRED`, `UNKNOWN`. Each non-UNKNOWN application claim should carry an evidence reference/correlation ID when the underlying adapter supports it.

`diagnose` is the read-only current-state collector across UI, probe, backend, and approved source. `diagnose_click` is the explicit action-replay path and remains risk/policy gated.
