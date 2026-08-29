# VS Code integration

The Engineering MCP now uses a **shared local Streamable HTTP service** for normal development.

## How it works

1. Open the Developer Control Center.
2. On first launch, the Control Center creates a private per-user bearer token. Fully restart VS Code once so it inherits `ENGINEERING_MCP_HTTP_TOKEN`.
3. Click **Run MCP Server**. This starts one authenticated local server at `http://127.0.0.1:8765/mcp`.
4. Open **Integration** and click **Connect to VS Code** once.
5. The Control Center adds `dotnetWpfEngineering` to the VS Code user-profile `mcp.json` as an HTTP server.
6. Open any WPF solution or other workspace that uses the same VS Code profile.
7. VS Code connects to the already-running local service. It does not launch a second MCP host.

To authorize a WPF application, build it, open **Integrations**, select **Authorize WPF workspace**,
and choose its solution or repository root. The Control Center discovers executable projects with
`UseWPF=true`, creates exact-path process rules and a bounded source root under the current user's
local application-data directory, records the policy in `ENGINEERING_MCP_POLICY`, and restarts the
MCP host. Each workspace receives a stable path-specific policy file that survives application
uninstall/reinstall. The default packaged policy remains metadata-only until this explicit action.

The user-profile registration is intentionally cross-workspace. The security policy separately decides which processes and source roots the MCP may inspect.

The Integration page includes **Policy Readiness**. It reports only safe summary counts and remediation guidance; it does not expose process paths, source roots, tokens, or secrets. VS Code can request the same safe report through `system_policy_diagnostics`. Before claiming that one tool is policy-disabled, an agent must call `system_tool_preflight` with that exact tool name. Individual policy and guard failures include a machine-readable `error.remediation` field.

## Installed VS Code entry

```json
{
  "servers": {
    "dotnetWpfEngineering": {
      "type": "http",
      "url": "http://127.0.0.1:8765/mcp?vscode",
      "headers": {
        "Authorization": "Bearer ${env:ENGINEERING_MCP_HTTP_TOKEN}",
        "X-Engineering-Mcp-Client": "vscode"
      }
    }
  }
}
```

VS Code supports Streamable HTTP servers in user-profile MCP configuration. It may require a one-time trust approval when the server configuration is first used or changes.

The non-secret `vscode` query flag lets the Control Center distinguish real VS Code MCP traffic from its own health checks and self-tests. The header is retained as a compatibility signal for clients that preserve custom headers.

## Transport model

The same `EngineeringMcp.Host.exe` supports both:

- `--transport http` — normal shared service used by Control Center and VS Code.
- `--transport stdio` — compatibility mode for clients that prefer to spawn the server themselves.

The HTTP service binds only to the loopback interface. The host rejects non-loopback peers and non-loopback Host headers, requires a bearer token on `/mcp` and `/healthz`, compares that token in constant time, and does not enable CORS. The Control Center supplies the same token when probing health so it can identify and manage the host process.

## Troubleshooting

If VS Code lists the server but tools are unavailable:

1. Keep Developer Control Center open.
2. On Home, confirm **MCP SERVER — Running · HTTP**.
3. Click **Test MCP Server**.
4. If that fails, use **Repair MCP Server** and then **Run MCP Server**.
5. Use the Logs tab for the server output.
6. If the log shows HTTP 401, fully exit and restart VS Code so it inherits `ENGINEERING_MCP_HTTP_TOKEN`.
7. If diagnostic or WPF tools return a policy error, build the target application, use **Authorize WPF workspace**, and select its workspace root.
8. Call `system_policy_diagnostics` for broad readiness and `system_tool_preflight` for the disputed tool, or read **Integration > Policy Readiness** for the exact safe corrective action.

Engineering MCP child processes use a minimal environment. Relative and UNC/network entries are removed from inherited `PATH` values before the host, tests, or maintenance commands start. This prevents stale network tool paths from leaking into WSL or diagnostic subprocesses while retaining local absolute tool paths.

The 76-tool surface includes authoritative per-tool preflight, metadata-only WPF grid/tree/item summaries, selector diagnostics, richer waits/assertions, and probe-backed binding/command/validation diagnostics. Advanced tools never return grid cell text, item labels, ViewModel values, validation messages, raw AutomationIds, window titles, clipboard content, or unredacted artifacts.

Do not copy workspace-relative stdio configuration between projects. The shared HTTP endpoint is deliberately independent of the active workspace; authorization remains exact-path and policy-controlled.
