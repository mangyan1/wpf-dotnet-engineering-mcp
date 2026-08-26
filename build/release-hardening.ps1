[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$OutputDirectory = 'artifacts/release',
    [string]$CertificateThumbprint = $env:ENGINEERING_MCP_SIGNING_THUMBPRINT,
    [switch]$RequireSigning
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$resolvedOutput = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputDirectory))
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts'))
if (-not $resolvedOutput.StartsWith($artifactsRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'OutputDirectory must resolve beneath the repository artifacts directory.'
}

New-Item -ItemType Directory -Force -Path $resolvedOutput | Out-Null
$hostOutput = Join-Path $resolvedOutput 'host-win-x64'
$controlCenterOutput = Join-Path $resolvedOutput 'control-center-win-x64'

dotnet publish (Join-Path $repositoryRoot 'src/EngineeringMcp.Host/EngineeringMcp.Host.csproj') -c $Configuration -r win-x64 --self-contained false -o $hostOutput
if ($LASTEXITCODE -ne 0) { throw 'Host publish failed.' }
dotnet publish (Join-Path $repositoryRoot 'src/EngineeringMcp.ControlCenter/EngineeringMcp.ControlCenter.csproj') -c $Configuration -r win-x64 --self-contained false -o $controlCenterOutput
if ($LASTEXITCODE -ne 0) { throw 'Control Center publish failed.' }

$dependencyInventoryPath = Join-Path $resolvedOutput 'dependencies.json'
$dependencyJson = dotnet list (Join-Path $repositoryRoot 'DotNetEngineeringMcp.sln') package --include-transitive --format json
if ($LASTEXITCODE -ne 0) { throw 'Dependency inventory generation failed.' }
[IO.File]::WriteAllText($dependencyInventoryPath, ($dependencyJson -join [Environment]::NewLine), [Text.UTF8Encoding]::new($false))

$inventory = Get-Content -Raw -LiteralPath $dependencyInventoryPath | ConvertFrom-Json
$packages = foreach ($project in $inventory.projects) {
    foreach ($framework in $project.frameworks) {
        @($framework.topLevelPackages) + @($framework.transitivePackages) | ForEach-Object {
            if ($null -ne $_.id -and $null -ne $_.resolvedVersion) {
                [pscustomobject]@{ Name = [string]$_.id; Version = [string]$_.resolvedVersion }
            }
        }
    }
}
$packages = $packages | Sort-Object Name, Version -Unique
$spdxPackages = @(
    [ordered]@{
        SPDXID = 'SPDXRef-EngineeringMcp'
        name = 'DotNetEngineeringMcp'
        versionInfo = 'local-build'
        downloadLocation = 'NOASSERTION'
        filesAnalyzed = $false
        licenseConcluded = 'NOASSERTION'
        licenseDeclared = 'NOASSERTION'
        copyrightText = 'NOASSERTION'
    }
)
$relationships = @()
foreach ($package in $packages) {
    $safeId = ($package.Name + '-' + $package.Version) -replace '[^A-Za-z0-9.-]', '-'
    $spdxId = 'SPDXRef-Package-' + $safeId
    $spdxPackages += [ordered]@{
        SPDXID = $spdxId
        name = $package.Name
        versionInfo = $package.Version
        downloadLocation = 'NOASSERTION'
        filesAnalyzed = $false
        licenseConcluded = 'NOASSERTION'
        licenseDeclared = 'NOASSERTION'
        copyrightText = 'NOASSERTION'
    }
    $relationships += [ordered]@{ spdxElementId = 'SPDXRef-EngineeringMcp'; relationshipType = 'DEPENDS_ON'; relatedSpdxElement = $spdxId }
}

$sbom = [ordered]@{
    spdxVersion = 'SPDX-2.3'
    dataLicense = 'CC0-1.0'
    SPDXID = 'SPDXRef-DOCUMENT'
    name = 'DotNetEngineeringMcp-release'
    documentNamespace = 'https://example.invalid/spdx/DotNetEngineeringMcp/' + [Guid]::NewGuid().ToString('N')
    creationInfo = [ordered]@{ created = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ'); creators = @('Tool: build/release-hardening.ps1') }
    packages = $spdxPackages
    relationships = $relationships
}
[IO.File]::WriteAllText((Join-Path $resolvedOutput 'sbom.spdx.json'), ($sbom | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))

if (-not [string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
    if ($CertificateThumbprint -notmatch '^[A-Fa-f0-9]{40,64}$') { throw 'Signing certificate thumbprint is invalid.' }
    $signTool = Get-Command signtool.exe -ErrorAction Stop
    Get-ChildItem -LiteralPath $resolvedOutput -Recurse -File | Where-Object Extension -in '.exe', '.dll' | ForEach-Object {
        & $signTool.Source sign /sha1 $CertificateThumbprint /fd SHA256 /tr 'https://timestamp.digicert.com' /td SHA256 $_.FullName
        if ($LASTEXITCODE -ne 0) { throw "Authenticode signing failed for $($_.Name)." }
    }
} elseif ($RequireSigning) {
    throw 'Release signing is required but ENGINEERING_MCP_SIGNING_THUMBPRINT was not provided.'
}

$outputUri = [Uri]($resolvedOutput.TrimEnd('\') + '\')
$checksums = Get-ChildItem -LiteralPath $resolvedOutput -Recurse -File |
    Where-Object Name -ne 'SHA256SUMS.txt' |
    Sort-Object FullName |
    ForEach-Object {
        $relative = [Uri]::UnescapeDataString($outputUri.MakeRelativeUri([Uri]$_.FullName).ToString())
        '{0}  {1}' -f (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash.ToLowerInvariant(), $relative
    }
$checksums | Set-Content -LiteralPath (Join-Path $resolvedOutput 'SHA256SUMS.txt') -Encoding ascii

Write-Host "Release artifacts, SPDX SBOM, dependency inventory, and SHA-256 checksums created at $resolvedOutput"
