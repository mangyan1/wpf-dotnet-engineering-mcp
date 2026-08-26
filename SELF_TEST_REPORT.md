# Self-Test Report — Shared Streamable HTTP Revision

## Static validation in build workspace

Result: **PASS**

- JSON parse checks: PASS
- XAML / project XML parse checks: PASS
- `ModelContextProtocol.AspNetCore` package wiring: PASS
- Streamable HTTP `WithHttpTransport` + `/mcp` mapping: PASS
- stdio compatibility transport retained: PASS
- loopback-only bind/peer/Host guard present: PASS
- no CORS enabled: PASS
- shared endpoint centralized in `McpRuntimeDefaults`: PASS
- Control Center self-test uses `HttpClientTransport`: PASS
- Full Self Test includes stdio compatibility: PASS
- Control Center launches `EngineeringMcp.Host.exe` as a background process: PASS
- VS Code integration writes an HTTP user-profile server entry: PASS
- old Control Center-owned stdio session removed: PASS
- workspace MCP example uses HTTP and has no `${workspaceFolder}` host coupling: PASS
- Control Center tabs are exactly Home / Self Test / Integration / Logs: PASS
- required plain-language GUI actions present: PASS
- XAML event handlers resolve to code-behind methods: PASS
- wildcard HTTP bind absent: PASS
- optional VS Code extension JavaScript syntax: PASS
- unexpected committed bearer/private-key scan: PASS

Static check total: **24 passed, 0 failed**.

## Windows runtime validation

Not executed in this container because it does not provide the .NET 10 SDK or an interactive Windows desktop.

On Windows, use **Developer Control Center → Run Full Self Test**. That acceptance path now performs:

1. `dotnet build`
2. `dotnet test --no-build`
3. restart one shared Streamable HTTP MCP service
4. health check `http://127.0.0.1:8765/healthz`
5. MCP HTTP protocol/tool discovery and `system.*` calls
6. stdio compatibility smoke test
7. launch WPF fixture
8. UIA attach / list windows / snapshot / find / type / assert
9. WPF probe status + binding diagnostics
10. screenshot/redaction check

A release should not be marked Windows-runtime verified until that GUI test is green.
