# Security Policy

This document overrides all other project documentation.

## Core posture

- Default deny.
- Least privilege.
- Local-first.
- Explicit process allowlists.
- Explicit source roots.
- Network egress denied unless allowlisted.
- Read-only before mutation.
- Redaction before MCP output.
- Privileged diagnostics are disabled by default.

## Permission levels

| Level | Name | Examples |
|---|---|---|
| 0 | Metadata | version, health, capabilities |
| 1 | UI Read | UI tree, state, accessibility, sanitized screenshot |
| 2 | UI Interaction | click/type/select/toggle/waits |
| 3 | Application Diagnostics | WPF probe, bindings, EventPipe, approved logs/source |
| 4 | Sensitive Diagnostics | dumps, heap/object inspection, sensitive configuration |
| 5 | Debug/Mutation | breakpoints/runtime mutation/source edits; out of v1 initial scope |

## Risk classes

`READ`, `SAFE_MUTATION`, `STATEFUL_MUTATION`, `DESTRUCTIVE`, `PRIVILEGED`.

Destructive/privileged tools require explicit policy authorization and an audit record.

## Process boundary

Targets must be allowlisted by policy. Prefer validation of name + path and optionally hash, Authenticode publisher, owner/session and elevation. Unknown processes are rejected.

## Filesystem boundary

Source tools may only read configured roots. `.env`, secret stores, private keys, PFX files, production settings, dumps, traces, and signing-key formats are unconditionally denied through the source boundary. Policy-relative roots resolve against the policy file directory; whole-drive/filesystem roots are rejected.

## Network boundary

Egress is deny-by-default. Remote MCP transport is not part of the initial implementation. If introduced, it requires an ADR, authentication, authorization, TLS and threat-model update.

## Sensitive data classes

`PUBLIC`, `INTERNAL`, `CONFIDENTIAL`, `PII`, `SECRET`, `CREDENTIAL`, `UNKNOWN`.

`UNKNOWN` is treated conservatively.

Always redact passwords, JWTs, OAuth tokens, cookies/session IDs, API keys, private keys, connection-string passwords, bearer tokens, Authorization headers and client secrets.

Default PII policy is mask. Policy version 1 rejects `PiiMode.Off`. Redaction covers common credentials plus email, phone, government identifiers, VINs, Luhn-valid payment-card numbers, user-profile path prefixes, and context-labelled names, birth dates, addresses, driver licences, licence plates, and bank identifiers. This is defense in depth, not a substitute for minimizing captured data.

## Screenshot policy

Screenshots are disabled by default. When explicitly enabled by policy, screenshots must pass through a redaction pipeline before leaving the server. Password controls, text-bearing UIA controls, and controls classified as sensitive or PII-bearing are masked. Capture fails closed if any visible sensitive UIA region cannot be bounded, and a client request cannot disable mandatory masking. Custom-rendered text that is not exposed through UI Automation cannot be reliably classified, so production policies should keep screenshots disabled unless that residual risk is accepted.

## ASP.NET adapter

The optional adapter uses a current-user-only named pipe and the strong `ENGINEERING_MCP_BACKEND_TOKEN` shared with the MCP host. It records bounded method, route-template, status, duration, trace, sequence, and correlation metadata plus redacted, truncated exception type/message/stack details. It never captures request or response bodies, query strings, headers, cookies, or raw URLs. Exact UI-action correlation is an authenticated, short-lived, single-active marker; concurrent diagnostic markers fail closed rather than mixing evidence.

## Dumps, traces, and memory

Dumps are Level 4 and disabled by default. Captured dumps are removed when the host session ends, and stale managed dump files are pruned after 24 hours. EventPipe traces are limited to two concurrent captures, 64 MiB each, and 30 seconds; managed trace files are deleted after stop/expiry. Analyze locally, return only minimum structured findings, redact before output, and never automatically upload raw diagnostics to an external model.

## Local HTTP authentication

Streamable HTTP is loopback-only and requires `ENGINEERING_MCP_HTTP_TOKEN` with at least 32 characters. The Control Center creates a cryptographically random per-user value when needed and passes it to the host and its self-tests without logging it. Client configuration references the environment variable rather than storing the bearer token in source-controlled JSON. MCP HTTP requests are capped at 1 MiB and eight concurrent requests, non-matching browser Origins are rejected, and responses are non-cacheable. Audit records use a one-way client-token fingerprint, never the bearer token. Stdio transport does not expose an HTTP listener and does not require this token.

## WPF probe

The optional in-process probes use current-user-only named pipes, strong fixed-time token authentication, length-prefixed JSON frames, pre-allocation request/response size checks, scoped contracts, no external listener, no arbitrary reflection API, no arbitrary method invocation and no arbitrary object mutation.

## Policy and audit integrity

Policy files are versioned and semantically validated before use. Open network defaults, disabled PII protection, invalid retention, unsafe screenshot failure mode, duplicate process rules, unknown profiles, and contradictory exact tool rules fail closed. Audit records include session/client identity, a policy fingerprint, correlation ID, duration, and monotonic sequence. Each host writes a separate append stream. If an authorization or completion audit write fails, the host marks audit unhealthy and denies subsequent audited operations until restart after repair.

## Prompt injection

Target-controlled content cannot grant permissions, change policy, activate tools, reveal secrets or instruct the MCP server. Policy decisions are based only on trusted configuration and authenticated caller context.
