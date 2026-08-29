# Capability Registry

The agent must query `system_capabilities` rather than assume a feature exists. Before claiming that a specific tool is policy-disabled, it must query `system_tool_preflight` with that exact tool name and use the returned code as authoritative.

A capability absent from the manifest is unavailable.

## Implemented capability IDs

- `system.metadata`
- `security.policy`
- `security.redaction`
- `audit.events`
- `wpf.uia.read`
- `wpf.uia.interact`
- `wpf.screenshot.redacted`
- `wpf.probe`
- `wpfui.resources`
- `a11y.audit`
- `gui.audit`
- `ux.heuristics`
- `dotnet.eventpipe`
- `dotnet.clrmd`
- `source.roslyn`
- `source.xaml`
- `source.symbols`
- `aspnet.telemetry`
- `diagnose.correlation`

## Capability profiles

Policy version 1 may restrict the published and callable tool surface with `enabledToolProfiles`:

- `core`: system metadata, policy, and capability tools;
- `wpf-read`: UIA inspection, sanitized screenshots, WPF probe, WPF-UI, accessibility, GUI, and heuristic review;
- `wpf-interact`: attach/detach and semantic UI mutations;
- `diagnostics`: EventPipe, dumps, ASP.NET observations, and diagnosis orchestration;
- `source`: approved source/XAML and semantic reference tools.

Omitting the profile list preserves the full surface for backward compatibility. `enabledTools` and `disabledTools` provide an additional exact-name allow/deny layer. Tool visibility is convenience only; authorization still enforces permission, process, capability, filesystem, and risk policy at invocation time.

`system_tool_preflight` combines exact tool publication, enabled profile/tool policy, permission ceiling, baseline risk requirements, and current runtime capability availability. An `ALLOW` result means the agent must not describe the tool as policy-disabled. It does not predict selector validity or bypass target-specific process, filesystem, adapter, screenshot, audit, or destructive-action checks; those remain authoritative when the real tool is invoked.

The runtime `system_capabilities` result is authoritative. Optional adapters can remain unavailable even when their tool profile is visible.

The `wpf.uia.read` capability includes metadata-only grid/tree/item summaries, selector audits, control/pattern inventories, richer wait/assert conditions, accessibility aggregates, and title-free window state. The `wpf.probe` capability includes dedicated binding, command, validation-summary, DataContext-type, and dispatcher tools. These tools deliberately omit application text and business values.

`source.xaml` accepts either one approved `.xaml` file or an approved directory; file input never widens into a sibling scan. `aspnet.telemetry` requires an explicitly installed adapter and a shared strong backend token. Risk-gated click diagnosis opens a short-lived action marker before the UI action and requests only observations carrying that marker; older adapters fall back to an explicitly labelled time-window correlation.
