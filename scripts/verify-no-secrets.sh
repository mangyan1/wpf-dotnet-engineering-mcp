#!/usr/bin/env bash
set -euo pipefail

# Scan source for bearer tokens, private keys, AWS access keys, GitHub/Slack
# tokens, and credential-style assignments without ever printing matched source
# text. Exact synthetic fixtures used by the redaction tests are allowlisted so
# hostile-input handling can be verified without tripping the gate.
python3 - <<'PY'
from __future__ import annotations

import re
import sys
from pathlib import Path

root = Path(".").resolve()
excluded_directories = {".git", "artifacts", "bin", "obj", "node_modules"}
excluded_files = {Path("scripts/verify-no-secrets.sh")}
rules = {
    "BEARER_TOKEN": re.compile(r"Authorization:\s*Bearer\s+[A-Za-z0-9._-]+"),
    "PRIVATE_KEY": re.compile(r"-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----"),
    "AWS_ACCESS_KEY_ID": re.compile(r"\bAKIA[0-9A-Z]{16}\b"),
    "GITHUB_TOKEN": re.compile(r"\b(?:gh[posur]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{22,})\b"),
    "SLACK_TOKEN": re.compile(r"\bxox[baprs]-[A-Za-z0-9-]{10,}\b"),
    "API_KEY_ASSIGNMENT": re.compile(
        r"(?i)\b(?:api[_-]?key|client[_-]?secret|secret|access[_-]?token|refresh[_-]?token|password|passwd|pwd)"
        r"\s*[:=]\s*[\"']?[^\s,;\"'\r\n]{6,}[\"']?"),
}
allowed_matches = {
    (
        Path("tests/EngineeringMcp.AdversarialTests/AdversarialTests.cs"),
        "BEARER_TOKEN",
        "Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.payload.signature",
    ),
    (
        Path("tests/EngineeringMcp.SecurityTests/SecurityTests.cs"),
        "BEARER_TOKEN",
        "Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.signature",
    ),
    (
        Path("tests/EngineeringMcp.SecurityTests/SecurityTests.cs"),
        "API_KEY_ASSIGNMENT",
        "password=SuperSecret123",
    ),
    (
        Path("tests/EngineeringMcp.AdversarialTests/AdversarialTests.cs"),
        "API_KEY_ASSIGNMENT",
        "api_key=sk_test_abcdefghijklmnopqrstuvwxyz123456",
    ),
    (
        Path("tests/EngineeringMcp.AdversarialTests/AdversarialTests.cs"),
        "API_KEY_ASSIGNMENT",
        "password: Hunter2!",
    ),
    (
        Path("tests/EngineeringMcp.AdversarialTests/AdversarialTests.cs"),
        "API_KEY_ASSIGNMENT",
        "client_secret=abc12345678901234567890",
    ),
    (
        Path("tests/EngineeringMcp.AdversarialTests/HostileInputTests.cs"),
        "AWS_ACCESS_KEY_ID",
        "AKIAIOSFODNN7EXAMPLE",
    ),
}

findings: list[tuple[str, Path, int]] = []
for path in root.rglob("*"):
    if not path.is_file():
        continue
    relative_path = path.relative_to(root)
    if relative_path in excluded_files or any(part in excluded_directories for part in relative_path.parts):
        continue
    try:
        lines = path.read_text(encoding="utf-8").splitlines()
    except (OSError, UnicodeDecodeError):
        continue
    for line_number, line in enumerate(lines, start=1):
        for rule_name, pattern in rules.items():
            for match in pattern.finditer(line):
                # The match starts at the keyword, so an opening string-literal
                # quote before it is never part of the match; only a trailing
                # quote (or one directly after the separator) can be included.
                # Normalize those away so allowlist entries stay canonical.
                if (relative_path, rule_name, match.group(0).strip("\"'")) not in allowed_matches:
                    findings.append((rule_name, relative_path, line_number))

if findings:
    for rule_name, relative_path, line_number in findings:
        print(f"{rule_name}: {relative_path.as_posix()}:{line_number}")
    print(f"Potential secret material detected at {len(findings)} location(s).", file=sys.stderr)
    sys.exit(1)

print("No unexpected secret material detected; matched values were not printed.")
PY
