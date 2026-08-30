# Contributing

Engineering MCP accepts focused changes that preserve its local-first, default-deny security model and universal WPF/.NET scope.

## Before opening a pull request

1. Create a focused branch from `main`.
2. Use only synthetic data. Never commit customer rows, application databases, logs, dumps, credentials, tokens, private keys, production configuration, or proprietary consuming-application logic.
3. Keep process, source, network, screenshot, diagnostic, and mutation capabilities policy-gated and fail-closed.
4. Update documentation and tests with every behavior or public-contract change.
5. Run:

```powershell
dotnet restore DotNetEngineeringMcp.sln --runtime win-x64 --locked-mode
dotnet restore installer/EngineeringMcp.Installer.wixproj --locked-mode
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Test-NuGetVulnerabilities.ps1
dotnet build DotNetEngineeringMcp.sln --configuration Release --no-restore
dotnet test DotNetEngineeringMcp.sln --configuration Release --no-build --no-restore
python scripts/self-test-static.py
bash scripts/verify-no-secrets.sh
```

The ordinary suite intentionally skips the installed-package acceptance test. Release candidates must additionally pass the MSI install, uninstall, reinstall, configuration-preservation, and installed 76-tool acceptance workflow documented in [`docs/TESTING.md`](docs/TESTING.md).

## Review expectations

- Pull requests must explain the security boundary affected and the evidence used to validate the change.
- Build scripts, dependencies, policy schemas, authentication, redaction, diagnostic capture, installer behavior, and signing workflows require explicit maintainer review.
- Dependabot opens weekly NuGet, VS Code extension, and GitHub Actions update pull requests. Minor and patch updates are grouped; major upgrades remain separate and must not be merged without compatibility testing.
- Do not weaken tests or suppress warnings to make a gate pass.
- Report vulnerabilities privately according to [`docs/SECURITY.md`](docs/SECURITY.md); do not include sensitive exploit data in a public issue.
