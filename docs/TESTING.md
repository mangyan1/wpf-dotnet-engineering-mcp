# Testing Strategy

## Mandatory suites

- Unit tests
- Integration tests
- Security tests
- Adversarial/prompt-injection tests
- Redaction tests

The live HTTP integration suite additionally requires bearer rejection, Origin rejection, portable unique tool names, output schemas, parameter descriptions, titles, all MCP annotations, `isError=true` for a deterministic domain failure, and safe remediation text for policy/guard denials. Security tests cover versioned policy rejection, built-in sensitive-file denial, expanded synthetic PII classes, timestamp/version/path false-positive regressions, oversized framed-IPC rejection, policy readiness diagnostics, and child-process PATH sanitization.

The integration suite launches real local fixtures to verify masked PNG output, authenticated WPF probe restart, a live ASP.NET request correlated through the named-pipe adapter, and one-file XAML audit isolation. No production account or personal data is required.

## Installed-package and VS Code acceptance

Run the installed runtime test without changing the installation:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/test-installed-vscode.ps1
```

Before promoting an MSI, exercise the complete install/uninstall/reinstall lifecycle:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/test-installed-vscode.ps1 `
  -MsiPath artifacts/release/EngineeringMcp-0.3.7-preview.3-win-x64-Setup.msi `
  -ExerciseReinstall
```

The lifecycle gate stops only Engineering MCP processes whose executable path is beneath the selected installation root. It verifies that the per-user policy and VS Code `mcp.json` hashes remain unchanged through install, uninstall, and reinstall. It extracts the candidate MSI to a verified temporary directory, compares the installed host hash with that payload, and requires exactly one installer registration so stale same-version development builds cannot pass. It then starts the installed host on an isolated loopback port and performs a VS Code-style initialize, 76-tool discovery, policy-diagnostic call, exact-tool preflight, successful runtime diagnostic, and intentionally denied privileged call with actionable remediation. The test never prints bearer tokens or policy contents.

The installer intentionally allows equal-version upgrades for development and pre-release rebuilds. WiX ICE61 conflicts with that inclusive upgrade range, so only ICE61 is suppressed for this documented case; the remaining installer validation stays enabled.

Release validation also runs the static contract script, NuGet vulnerable-package scan, locked dependency restore, Release packaging, SPDX SBOM generation, checksum generation, and installed-package acceptance. Authenticode signing is a promotion gate and requires an operator-provided certificate thumbprint or the explicitly labelled development self-signing mode.

## Golden WPF fixture must eventually contain

Good/broken binding, disabled command, validation error, hardcoded color, DynamicResource, clipping, overlap, modal dialog, async operation, crashing command, PasswordBox, fake PII, fake JWT/API key and prompt-injection UI text.

## Golden ASP.NET fixture

The current live fixture verifies a successful request, health, bounded observation, authentication, and an exact action-correlation marker. Future fault coverage remains: 400/401/500, timeout, slow request, and exception observations containing synthetic secret/PII-shaped values.

## Hallucination test

Fixture: button is disabled; no probe/source evidence exists. Question: “Why is it disabled?” Correct result: `OBSERVED: disabled; UNKNOWN: reason; NEXT: inspect probe/source`. Claiming validation failed is a test failure.
