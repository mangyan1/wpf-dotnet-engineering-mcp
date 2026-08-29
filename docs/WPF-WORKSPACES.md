# Universal WPF workspace authorization

Engineering MCP is application-neutral. Any authorized WPF application can use the same 76-tool server, including applications built with WPF-UI Fluent, stock WPF, custom control libraries, or another WPF design system.

## Authorize a workspace

1. Build the WPF application you want to inspect.
2. Open Developer Control Center.
3. Select **Authorize WPF workspace**.
4. Choose the solution, repository, or narrower project-group directory.

The Control Center discovers modern executable projects with `UseWPF=true` and classic WPF projects carrying the WPF project-type metadata. A project must have `OutputType=WinExe` or `Exe`, and a built executable must exist under its own `bin` directory. The newest output for each WPF application is selected.

If automatic discovery cannot identify a built application, the Control Center offers an explicit executable picker. The selected `.exe` must remain under the workspace, contain no reparse-point path segment, and be verifiably WPF through its managed metadata or same-name companion `.dll`.

## Generated policy

Authorization creates a validated policy under:

`%LOCALAPPDATA%\EngineeringMcp\policies\policy.<workspace>.<path-hash>.json`

The policy:

- allows only discovered executable names at their exact built paths;
- allows source reads only under the selected workspace root;
- denies secrets, production settings, keys, dumps, databases, and Git internals;
- keeps network access denied;
- masks PII and text-bearing screenshot regions;
- enables audit recording;
- blocks destructive and privileged actions;
- enables the core, WPF read/interact, diagnostics, and source profiles.

The path hash prevents two workspaces with the same directory name from overwriting one another. Policies live outside the installation directory and survive upgrade or reinstall.

## Discovery safety

Workspace discovery is local, read-only, and bounded. It skips `.git`, `.vs`, `.idea`, `bin`, `obj`, `artifacts`, package/vendor directories, and filesystem reparse points. It fails closed when the selected root is a drive, discovery exceeds its bounds, no built WPF executable exists, or multiple projects would create an ambiguous process rule.

Engineering MCP does not execute MSBuild or targets during discovery. It reads the nearest in-workspace `Directory.Build.props` and literal relative `<Import Project="...">` files as inert XML. Imports containing properties, item expressions, wildcards, conditions, absolute paths, reparse points, or paths outside the workspace are ignored. Static import traversal is limited to 32 files per project and each parsed file is limited to 1 MiB. This supports centralized WPF properties without allowing a selected repository to run build-time code merely because it was authorized.

## Application-specific integrations

Backend adapters, product smoke scripts, and business-specific acceptance fixtures belong in the consuming application's repository. The universal MCP package contains only generic WPF/.NET capabilities and neutral synthetic fixtures.
