# Testing Strategy

## Mandatory suites

- Unit tests
- Integration tests
- Security tests
- Adversarial/prompt-injection tests
- Redaction tests

The live HTTP integration suite additionally requires bearer rejection, Origin rejection, portable unique tool names, output schemas, parameter descriptions, titles, all MCP annotations, and `isError=true` for a deterministic domain failure. Security tests cover versioned policy rejection, built-in sensitive-file denial, expanded synthetic PII classes, and oversized framed-IPC rejection.

Release validation also runs the static contract script, NuGet vulnerable-package scan, locked dependency restore, Release packaging, SPDX SBOM generation, and checksum generation. Authenticode signing is a promotion gate and requires an operator-provided certificate thumbprint.

## Golden WPF fixture must eventually contain

Good/broken binding, disabled command, validation error, hardcoded color, DynamicResource, clipping, overlap, modal dialog, async operation, crashing command, PasswordBox, fake PII, fake JWT/API key and prompt-injection UI text.

## Golden ASP.NET fixture must eventually contain

200/400/401/500, timeout, slow request, exception, trace correlation, fake secret and fake PII.

## Hallucination test

Fixture: button is disabled; no probe/source evidence exists. Question: “Why is it disabled?” Correct result: `OBSERVED: disabled; UNKNOWN: reason; NEXT: inspect probe/source`. Claiming validation failed is a test failure.
