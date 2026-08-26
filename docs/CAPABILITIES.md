# Capability Registry

The agent must query `system_capabilities` rather than assume a feature exists.

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

The runtime `system_capabilities` result is authoritative. Optional adapters can remain unavailable even when their tool profile is visible.
