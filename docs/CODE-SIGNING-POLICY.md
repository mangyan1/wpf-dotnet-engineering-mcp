# Code signing policy

Engineering MCP publishes source, build scripts, release notes, checksums, and Windows artifacts from the same public repository. A release tag identifies the source used to produce its MSI and portable ZIP.

## Current preview signatures

Until a publicly trusted open-source signing service approves the project, preview artifacts use a non-exportable development certificate created in the release operator's Windows user certificate store. The build signs Engineering MCP executables, libraries, and MSI with SHA-256 and an RFC 3161 timestamp. The package includes only the public certificate. Development signatures protect post-signing integrity but do not establish a publicly trusted publisher identity.

## Planned trusted signing

Target provider: **Free code signing provided by SignPath.io, certificate by SignPath Foundation**.

Trusted signing will begin only after the project is accepted and its public, automated build and artifact configuration are approved. Every trusted signing request will require manual approval. Private keys must remain in the signing provider's hardware-backed service and must never be committed, exported, or exposed to the build runner.

## Team roles

- Committer and reviewer: [@mangyan1](https://github.com/mangyan1)
- Signing approver: [@mangyan1](https://github.com/mangyan1)

Contributions from other authors require review before merge. Release build-script, dependency, permission, and signing-workflow changes require explicit maintainer review.

## Privacy policy

This program will not transfer any information to other networked systems unless specifically requested by the user or the person installing or operating it. Runtime MCP HTTP traffic is loopback-only by default, policy-controlled network access defaults to deny, and audit/redaction controls must remain enabled for diagnostic workflows.

The project does not add telemetry, analytics, crash reporting, or remote logging. Release builds may contact explicitly configured package repositories and RFC 3161 timestamp services; these services receive package requests or signing digests, not user application data.

## Release approval

Before publication, the approver verifies:

1. The release tag points to the reviewed public source commit.
2. Build, automated tests, static security checks, and secret/PII-safe scans pass.
3. The MSI install/uninstall/reinstall lifecycle and installed MCP acceptance pass.
4. Artifact names, embedded versions, signing identity, timestamps, and SHA-256 checksums match the release notes.
5. The release is marked as a pre-release whenever it uses the development certificate.
