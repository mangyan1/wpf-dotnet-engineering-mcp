#!/usr/bin/env bash
set -euo pipefail

# Scan source for bearer tokens and private keys without ever printing matched
# source text. Two exact synthetic JWT-shaped test fixtures are allowlisted so
# the redaction tests can verify hostile-input handling.
python3 - <<'PY'
from __future__ import annotations

import re
import sys
from pathlib import Path

root = Path(".").resolve()
excluded_directories = {".git", "artifacts", "bin", "obj"}
excluded_files = {Path("scripts/verify-no-secrets.sh")}
rules = {
    "BEARER_TOKEN": re.compile(r"Authorization:\s*Bearer\s+[A-Za-z0-9._-]+"),
    "PRIVATE_KEY": re.compile(r"-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----"),
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
                if (relative_path, rule_name, match.group(0)) not in allowed_matches:
                    findings.append((rule_name, relative_path, line_number))

if findings:
    for rule_name, relative_path, line_number in findings:
        print(f"{rule_name}: {relative_path.as_posix()}:{line_number}")
    print(f"Potential secret material detected at {len(findings)} location(s).", file=sys.stderr)
    sys.exit(1)

print("No unexpected bearer tokens or private keys detected; matched values were not printed.")
PY
