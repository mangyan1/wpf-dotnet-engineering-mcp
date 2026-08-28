# Developer Control Center

The Control Center is the primary development interface. Normal development does not require terminal commands.

It has two explicit runtime modes:

- **Developer mode** discovers the source solution and enables build, test, readiness, fixture, and WPF end-to-end actions.
- **Standalone mode** discovers `app-manifest.json` beside the executable and uses the bundled host and policy. It keeps runtime MCP tests and global VS Code integration enabled while disabling source-only actions.

Build the standalone app with `build/release-hardening.ps1`, extract the generated ZIP, and launch `EngineeringMcp.ControlCenter.exe`. The package is self-contained and does not require a machine-wide .NET installation.

## Home

- **Run MCP Server** — starts one shared loopback Streamable HTTP service at `http://127.0.0.1:8765/mcp`.
- **Test MCP Server** — connects to that running service, discovers tools, and calls the core system tools.
- **Run Full Self Test** — builds the solution, runs code/security/adversarial tests, starts the shared HTTP service, tests HTTP MCP, checks stdio compatibility, launches the WPF fixture, then runs UIA/probe/screenshot checks.

The Control Center and VS Code use the same HTTP server process. Closing the Control Center stops the server intentionally so the in-memory WPF probe token is not left behind in an orphaned process.

## Self Test

- **Run MCP Server**
- **Stop MCP Server**
- **Test MCP Server**
- **MCP Server Logs**
- **Repair MCP Server** — use only if Run/Test fails. Developer mode restores and rebuilds the host; standalone mode verifies the packaged host/policy. Both modes refresh VS Code integration if it already exists.
- **Test WPF End-to-End** — starts the server if necessary and verifies the controlled fixture.

## Integration

- **Connect to VS Code** — one-time user-profile registration pointing VS Code at the shared HTTP endpoint.
- **Open User MCP Config** — inspect the actual VS Code user configuration.
- **Open Policy** / **Security Guide** — inspect authorization boundaries.

VS Code visibility is not authorization. The MCP can be visible globally while process/file guardrails continue to deny unapproved targets.

## Logs

Shows build output, shared MCP server stdout/stderr, protocol results, UI automation evidence, and probe errors. Normal failures are shown here instead of modal dialogs.
