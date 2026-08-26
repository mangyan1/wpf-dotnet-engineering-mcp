# Threat Model

## Protected assets

- application credentials and secrets;
- PII/customer data;
- source code;
- process memory/dumps;
- authentication material;
- workstation integrity;
- backend data;
- audit integrity.

## Primary adversaries

1. Malicious or compromised target application.
2. Prompt injection embedded in UI/logs/source/API responses.
3. Over-broad agent request.
4. Misconfigured allowlist.
5. Local untrusted process attempting to impersonate the probe or host.
6. Dependency compromise.
7. Accidental sensitive-data leakage via screenshots, traces, exceptions or dumps.

## Mandatory mitigations

- capability registry;
- explicit allowlists;
- caller permission levels;
- tool risk classification;
- output classification/redaction;
- screenshot masking;
- bounded output sizes/timeouts;
- audit trail;
- no arbitrary shell/reflection/filesystem/process access;
- probe authentication + local IPC;
- adversarial fixture tests;
- dependency scanning/SBOM before production release.

## 2026-08-26 control review

- Oversized/allocation attacks: HTTP bodies and framed local IPC are rejected before unbounded allocation; concurrency and observation windows are capped.
- Cross-process race/confusion: calls targeting the same process are serialized and probe messages are authenticated, framed, and current-user-only.
- Policy downgrade: policy version/schema and semantic validation reject open egress, disabled PII protection, whole-drive roots, unsafe screenshot behavior, and contradictory tool rules.
- Audit loss: completion-write failure marks the host audit path unhealthy and subsequent audited calls fail closed; each host has a separate append stream.
- Tool overexposure: profiles and exact tool rules limit discovery/calls while invocation authorization remains mandatory.
- Supply chain: packages are centrally pinned and locked; local release validation scans known vulnerabilities and emits dependency inventory, SPDX SBOM, and checksums.

Residual risks: UI Automation and WPF dispatcher COM calls cannot always be forcibly aborted in-process; custom-rendered screenshot text may evade UIA-based masking; regex/context PII classification cannot prove absence of all personal data; local administrators can tamper with binaries/audit files; official signing and externally attested provenance require operator credentials or a future trusted build service. Keep screenshots and privileged diagnostics disabled unless explicitly needed, use minimal read roots/profiles, and treat signing as a release-promotion gate.

## Stop conditions

Implementation must stop and require an ADR/security review before adding arbitrary shell execution, remote host control, database credentials, arbitrary process memory access, source mutation, runtime mutation, new remote transport, broad filesystem access or weakened redaction.
