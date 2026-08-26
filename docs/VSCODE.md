# VS Code integration

The Engineering MCP now uses a **shared local Streamable HTTP service** for normal development.

## How it works

1. Open the Developer Control Center.
2. On first launch, the Control Center creates a private per-user bearer token. Fully restart VS Code once so it inherits `ENGINEERING_MCP_HTTP_TOKEN`.
3. Click **Run MCP Server**. This starts one authenticated local server at `http://127.0.0.1:8765/mcp`.
4. Open **Integration** and click **Connect to VS Code** once.
5. The Control Center adds `dotnetWpfEngineering` to the VS Code user-profile `mcp.json` as an HTTP server.
6. Open ApexDrive or any other workspace that uses the same VS Code profile.
7. VS Code connects to the already-running local service. It does not launch a second MCP host.

The user-profile registration is intentionally cross-workspace. The security policy separately decides which processes and source roots the MCP may inspect.

## Installed VS Code entry

```json
{
  "servers": {
    "dotnetWpfEngineering": {
      "type": "http",
      "url": "http://127.0.0.1:8765/mcp",
      "headers": {
        "Authorization": "Bearer ${env:ENGINEERING_MCP_HTTP_TOKEN}"
      }
    }
  }
}
```

VS Code supports Streamable HTTP servers in user-profile MCP configuration. It may require a one-time trust approval when the server configuration is first used or changes.

## Transport model

The same `EngineeringMcp.Host.exe` supports both:

- `--transport http` — normal shared service used by Control Center and VS Code.
- `--transport stdio` — compatibility mode for clients that prefer to spawn the server themselves.

The HTTP service binds only to the loopback interface. The host rejects non-loopback peers and non-loopback Host headers, requires a bearer token on `/mcp`, compares that token in constant time, and does not enable CORS. Health metadata remains available locally without credentials so the Control Center can safely identify and manage the host process.

## Troubleshooting

If VS Code lists the server but tools are unavailable:

1. Keep Developer Control Center open.
2. On Home, confirm **MCP SERVER — Running · HTTP**.
3. Click **Test MCP Server**.
4. If that fails, use **Repair MCP Server** and then **Run MCP Server**.
5. Use the Logs tab for the server output.
6. If the log shows HTTP 401, fully exit and restart VS Code so it inherits `ENGINEERING_MCP_HTTP_TOKEN`.

Do not copy workspace-relative stdio configuration into ApexDrive. The shared HTTP endpoint is deliberately independent of the active workspace.
