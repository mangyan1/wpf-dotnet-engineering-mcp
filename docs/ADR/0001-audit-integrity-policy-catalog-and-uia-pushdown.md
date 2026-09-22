# ADR-0001: Audit integrity chain, policy catalogue consolidation, deny-first diagnostics gates, and UIA selector pushdown

Status: ACCEPTED

## Context

The hardening pass between commits d586d19 and b7fae5b changed six structures that each had a plausible earlier design. They are recorded together because they share one theme: every one of them removes a place where an untrusted input (an application under automation, a policy file, a dump, or an audit file) could influence a cached, mediated, or silently-downgraded decision.

Before this pass:

- Audit records were appended to a JSONL file with no integrity linkage; a process writing to a shared same-day path could interleave with a restarted host, and deletion or truncation of a middle range was undetectable.
- `ToolPolicies` sat between the tools and `ToolPolicyCatalog`, forwarding calls while adding no policy of its own.
- ClrMD dump analysis checked privileges at some entry points, leaving later entry points free to assume an earlier check.
- A proposed cache would have memoized accessibility-analysis verdicts keyed by a hash of the snapshot content.
- Element resolution walked every descendant of every window in-process and matched selectors with an ordinal predicate.
- Control Center shared one static `HttpClient` with a fixed `Authorization` header for health checks across multiple configured servers.

## Decision

1. **Audit hash chain and rollover.** Every `AuditEvent` record carries a `RecordHash`: SHA-256 over the canonical hash-less serialization prefixed with the previous record's hash, chained from a fixed genesis value per file. Files roll on UTC day and are named `audit-yyyyMMdd-HHmmss-<pid>.jsonl`, so a restarted host (or a reused process id) never appends into a file whose chain started in another process. Retention pruning re-runs on every rollover so a long-lived host cannot keep writing into a file it no longer prunes.

2. **`ToolPolicies` deleted; tools use `ToolPolicyCatalog` directly.** The middleman added a hop without adding policy. One catalogue now answers capability, interaction, profile, and list filtering for every caller.

3. **Deny-first privileged diagnostics.** Every dump analysis entry in `ClrMdService` runs one shared gate (`EnsurePrivilegedDiagnostics`) before any dump reference or file access. It denies unless the policy sets `AllowPrivilegedDiagnostics` **and** the permission ceiling is at least `SensitiveDiagnostics`, returning `PRIVILEGED_DIAGNOSTICS_DISABLED` with actionable remediation. Checking first and acting after means no entry point can reach a dump on an assumption from a sibling path.

4. **No hash-keyed verdict cache.** The accessibility-analysis verdict cache was rejected. A cache key derived from target-supplied content is attacker-replayable: a hostile application can serve byte-identical UI content while its live semantics differ (handlers, visibility, or enabled state are not part of any fingerprint), and the cached verdict cannot attest which policy version, process, or moment produced it. Verdicts are therefore recomputed per call.

5. **Selector matching pushed into UIA, ordinal post-filter retained.** `ResolveElement` builds a FlaUI condition (`ByAutomationId`/`ByName`/`ByClassName`/`ByControlType`, AND-composed) so the COM/UIA provider filters the descendant walk. UIA property conditions compare strings non-ordinally, so the ordinal `Matches` predicate remains the post-filter; resolution semantics across Find/Query/Click/Type are unchanged, only the walk cost moves server-side. Attached sessions prune dead targets on the next call and FIFO-evict element references at a 10,000-reference ceiling.

6. **`McpHealthClient` extracted per instance.** Control Center health checks moved into `McpHealthClient`, constructed per configured server so the bearer token is attached per request instead of living on a shared static `Authorization` header that one server's configuration could leak to another's probe.

## Security impact

- Positive: audit tampering within a file is detectable by re-walking `RecordHash` values; cross-process audit interleaving and same-day reuse corruption are prevented structurally. Privileged dump analysis is deny-first with a single gate. A replayable verdict cache is absent. Bearer tokens cannot cross-configurate via a shared client.
- Residual: the hash chain proves order and content of records as written by the current process; it does not defend against an attacker rewriting every record in a file and recomputing the chain, which only signing (not in scope) would address. Retention pruning is best-effort and never blocks authorization.

## Data accessed

No new data classes. Audit records already contain decision metadata; the chain adds a `RecordHash` field over the same payload. UIA conditions and verdicts operate on snapshots already authorized by the process guard.

## Permission/risk changes

- None raised. `AllowPrivilegedDiagnostics` stays `false` by default and the gate is now uniformly deny-first, which narrows the privileged surface.
- Control Center's per-instance client removes the risk that a health probe authenticates to the wrong local endpoint.

## Redaction/audit impact

- Audit events gain an integrity chain (above); records are unchanged otherwise.
- Selector pushdown does not touch redaction: element text was never returned, and `SafeUiAnalysis.ActionableTypes` is now shared by one definition instead of a divergent copy.
- The dropped verdict cache removes a path that could have served stale verdicts without re-running PII/secret classification on new content.

## Alternatives

- **HMAC the audit chain with a per-host key** — rejected for this pass: it adds key custody (a file the host can read is a file an attacker with the host's privileges can read) without defeating a same-privilege rewriter; the chain already defeats selective splicing. Signing infrastructure is a future decision if an external verifier is required.
- **Keep `ToolPolicies` and widen it** — rejected: a forwarding middleman invites two answers to "what does policy say?" The catalogue plus the `ToolListRules` gate is one place.
- **Cache verdicts with a TTL instead of content-hash keys** — rejected: a hostile application can trivially outlast any TTL, and a time-freshness bound is not a correctness argument for state the target can mutate between calls. Cost of recomputation is bounded by the existing snapshot ceilings.
- **Cache verdicts keyed by snapshot plus policy fingerprint and process id** — closer to sound, but the fingerprint input remains attacker-chosen content; identical content replays the verdict. Still rejected.
- **Trust UIA conditions alone and drop `Matches`** — rejected: UIA string comparisons are non-ordinal, which would silently change which elements resolve; `Matches` stays as the single ordinal authority.
- **One shared health client with per-request headers** — workable, but per-instance construction is simpler to reason about and deletes the static header entirely.

## Acceptance tests

- `tests/EngineeringMcp.SecurityTests/BoundedJsonPipeProtocol_RoundTripsAndRejectsOversizedFrame` and `tests/EngineeringMcp.AdversarialTests/BoundedJsonPipeProtocol_*` pin the bounded IPC surface the gates sit behind.
- `ToolAuthorization_FailingAuditSink_DeniesThenLatchesUntilRestart`, `ToolAuthorization_CompletionAuditFailure_TripsTheGateForLaterCalls`, and `ToolAuthorization_AuditDisabledPolicy_DoesNotDenyOnBrokenSink` (AdversarialTests) prove audit writes fail closed and latch.
- `ToolPolicyCatalog_UnknownToolNameIsNotPublished` and `PolicyEngine_ToolLists_DenyUnknownAndDisabledTools` (AdversarialTests) pin the single-catalogue contract.
- `PolicyEngine_RejectsPrivilegedWithoutExplicitFlag` (SecurityTests) and the `PRIVILEGED_DIAGNOSTICS_DISABLED` mapping in `ClrMdService` cover the deny-first gate.
- `SafeUiAnalysis_*` and `SelectorAudit_*` (IntegrationTests) pin the safe-analysis surface; `McpHttpIntegrationTests.LiveHttpHost_RequiresBearerAndPublishesPortableToolNames` pins the live host.

## Rollback

Each decision is independently revertable at its commit (69a7788 for 1–4's audit/policy half, e77fe0a for the catalogue deletion, b7fae5b for 5–6). Reverting the hash chain only changes `RecordHash` back to `null` — old readers accept the file; reverting the rollover naming reopens the cross-process interleaving risk documented above, so it should only be rolled back together with a written decision to accept it.