# .NET/WPF Engineering MCP — VS Code development extension

This optional extension registers the shared local Streamable HTTP MCP endpoint:

`http://127.0.0.1:8765/mcp?vscode`

Start the actual server from **Developer Control Center → Run MCP Server**. The extension does not spawn a second host process and does not depend on the currently open workspace.

For normal local use, the Control Center's **Connect to VS Code** button is simpler and does not require installing this extension.
