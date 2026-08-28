[CmdletBinding()]
param(
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'Programs\Engineering MCP'),
    [string]$PolicyPath = [Environment]::GetEnvironmentVariable('ENGINEERING_MCP_POLICY', [EnvironmentVariableTarget]::User),
    [string]$VsCodeConfigPath = (Join-Path $env:APPDATA 'Code\User\mcp.json'),
    [string]$MsiPath,
    [switch]$ExerciseReinstall,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$testProject = Join-Path $repositoryRoot 'tests\EngineeringMcp.IntegrationTests\EngineeringMcp.IntegrationTests.csproj'
$installRoot = [IO.Path]::GetFullPath($InstallRoot)

function Resolve-RequiredFile([string]$Path, [string]$Label) {
    if ([string]::IsNullOrWhiteSpace($Path)) { throw "$Label is not configured." }
    $resolved = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) { throw "$Label was not found." }
    return $resolved
}

function Test-UnderDirectory([string]$Candidate, [string]$Directory) {
    $root = [IO.Path]::GetFullPath($Directory).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $fullCandidate = [IO.Path]::GetFullPath($Candidate)
    return $fullCandidate.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)
}

function Assert-PersistentConfiguration([string]$PolicyHash, [string]$VsCodeHash) {
    $currentPolicy = (Get-FileHash -Algorithm SHA256 -LiteralPath $script:policyPath).Hash
    $currentVsCode = (Get-FileHash -Algorithm SHA256 -LiteralPath $script:vsCodeConfigPath).Hash
    if ($currentPolicy -ne $PolicyHash) { throw 'The durable MCP policy changed during install/uninstall/reinstall.' }
    if ($currentVsCode -ne $VsCodeHash) { throw 'The VS Code MCP configuration changed during install/uninstall/reinstall.' }
}

function Stop-VerifiedEngineeringMcpProcesses {
    foreach ($processName in @('EngineeringMcp.ControlCenter', 'EngineeringMcp.Host')) {
        foreach ($process in @(Get-Process -Name $processName -ErrorAction SilentlyContinue)) {
            try {
                $actualPath = $process.MainModule.FileName
                if (-not (Test-UnderDirectory $actualPath $script:installRoot)) {
                    throw "Refused to stop $processName because its executable is outside the selected install root."
                }
                Stop-Process -Id $process.Id -Force
                $process.WaitForExit(5000) | Out-Null
            }
            finally {
                $process.Dispose()
            }
        }
    }
}

function Invoke-Msi([string]$Mode) {
    $switch = if ($Mode -eq 'install') { '/i' } else { '/x' }
    $arguments = @($switch, ('"' + $script:msiPath + '"'), '/qn', '/norestart')
    $installer = Start-Process -FilePath 'msiexec.exe' -ArgumentList $arguments -Wait -PassThru -WindowStyle Hidden
    if ($installer.ExitCode -notin @(0, 3010)) {
        throw "Windows Installer $Mode failed with exit code $($installer.ExitCode)."
    }
}

$policyPath = Resolve-RequiredFile $PolicyPath 'Durable MCP policy'
$vsCodeConfigPath = Resolve-RequiredFile $VsCodeConfigPath 'VS Code MCP configuration'
if (Test-UnderDirectory $policyPath $installRoot) {
    throw 'The MCP policy is inside the install directory and cannot survive uninstall. Configure a durable policy first.'
}

$policyHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $policyPath).Hash
$vsCodeHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $vsCodeConfigPath).Hash

if ($ExerciseReinstall) {
    $msiPath = Resolve-RequiredFile $MsiPath 'MSI package'
    try {
        Write-Host 'Installing the candidate MSI...'
        Stop-VerifiedEngineeringMcpProcesses
        Invoke-Msi 'install'
        Assert-PersistentConfiguration $policyHash $vsCodeHash

        Write-Host 'Uninstalling the candidate MSI...'
        Stop-VerifiedEngineeringMcpProcesses
        Invoke-Msi 'uninstall'
        Assert-PersistentConfiguration $policyHash $vsCodeHash

        Write-Host 'Reinstalling the candidate MSI...'
        Invoke-Msi 'install'
        Assert-PersistentConfiguration $policyHash $vsCodeHash
    }
    finally {
        $hostExecutable = Join-Path $installRoot 'host\EngineeringMcp.Host.exe'
        if (-not (Test-Path -LiteralPath $hostExecutable -PathType Leaf) -and -not [string]::IsNullOrWhiteSpace($msiPath)) {
            Write-Warning 'The acceptance sequence did not leave Engineering MCP installed; attempting recovery reinstall.'
            Invoke-Msi 'install'
        }
    }
}

$installedHost = Join-Path $installRoot 'host\EngineeringMcp.Host.exe'
if (-not (Test-Path -LiteralPath $installedHost -PathType Leaf)) {
    throw 'The installed Engineering MCP host was not found.'
}

$env:ENGINEERING_MCP_ACCEPTANCE_INSTALL_ROOT = $installRoot
$env:ENGINEERING_MCP_ACCEPTANCE_POLICY = $policyPath
$env:ENGINEERING_MCP_ACCEPTANCE_VSCODE_CONFIG = $vsCodeConfigPath

Write-Host 'Running installed-package VS Code protocol acceptance...'
& dotnet test $testProject --no-restore --configuration $Configuration --filter 'TestCategory=InstalledAcceptance'
if ($LASTEXITCODE -ne 0) { throw "Installed-package VS Code acceptance failed with exit code $LASTEXITCODE." }

Assert-PersistentConfiguration $policyHash $vsCodeHash
Write-Host 'Installed-package VS Code acceptance: PASS'
