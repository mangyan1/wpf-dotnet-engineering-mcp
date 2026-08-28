'use strict';

const vscode = require('vscode');

const PROVIDER_ID = 'dotnetEngineeringMcp.provider';
const ENDPOINT = 'http://127.0.0.1:8765/mcp?vscode';
const TOKEN_ENVIRONMENT_VARIABLE = 'ENGINEERING_MCP_HTTP_TOKEN';

/** @param {vscode.ExtensionContext} context */
async function activate(context) {
  const output = vscode.window.createOutputChannel('.NET/WPF Engineering MCP');
  context.subscriptions.push(output);
  output.appendLine(`Activating .NET/WPF Engineering MCP on VS Code ${vscode.version} (${process.platform}).`);

  if (process.platform !== 'win32') {
    output.appendLine('WPF runtime automation requires Windows. MCP registration is disabled on this platform.');
    return;
  }

  if (!vscode.lm || typeof vscode.lm.registerMcpServerDefinitionProvider !== 'function' || typeof vscode.McpHttpServerDefinition !== 'function') {
    const message = 'This VS Code build does not expose the stable HTTP MCP extension API. Update VS Code and reload the window.';
    output.appendLine(message);
    vscode.window.showErrorMessage(message);
    return;
  }

  const didChange = new vscode.EventEmitter();
  context.subscriptions.push(didChange);

  const provider = vscode.lm.registerMcpServerDefinitionProvider(PROVIDER_ID, {
    onDidChangeMcpServerDefinitions: didChange.event,
    provideMcpServerDefinitions: async () => {
      const token = process.env[TOKEN_ENVIRONMENT_VARIABLE];
      if (!token || token.length < 32) {
        const message = `${TOKEN_ENVIRONMENT_VARIABLE} is missing. Start Developer Control Center once, then fully restart VS Code.`;
        output.appendLine(message);
        vscode.window.showErrorMessage(message);
        return [];
      }

      output.appendLine(`Published shared HTTP MCP definition: ${ENDPOINT}`);
      return [new vscode.McpHttpServerDefinition(
        '.NET/WPF Engineering MCP',
        vscode.Uri.parse(ENDPOINT),
        { Authorization: `Bearer ${token}` },
        '0.3.2'
      )];
    },
    resolveMcpServerDefinition: async server => server
  });
  context.subscriptions.push(provider);

  context.subscriptions.push(vscode.commands.registerCommand('dotnetEngineeringMcp.showSetup', async () => {
    output.show(true);
    output.appendLine('--- .NET/WPF Engineering MCP integration diagnostics ---');
    output.appendLine(`VS Code: ${vscode.version}`);
    output.appendLine(`Platform: ${process.platform}`);
    output.appendLine(`MCP provider API: ${Boolean(vscode.lm && typeof vscode.lm.registerMcpServerDefinitionProvider === 'function')}`);
    output.appendLine(`MCP HTTP definition API: ${typeof vscode.McpHttpServerDefinition === 'function'}`);
    output.appendLine(`Endpoint: ${ENDPOINT}`);
    output.appendLine(`Bearer token available: ${Boolean(process.env[TOKEN_ENVIRONMENT_VARIABLE])}`);
    output.appendLine('Start the shared MCP Server from Developer Control Center before using tools.');
  }));
}

function deactivate() {}
module.exports = { activate, deactivate };
