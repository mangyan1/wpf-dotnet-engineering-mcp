[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$OutputDirectory = 'artifacts/release',
    [string]$CertificateThumbprint = $env:ENGINEERING_MCP_SIGNING_THUMBPRINT,
    [string]$SourceRevision = $env:ENGINEERING_MCP_SOURCE_REVISION,
    [string]$ExpectedVersion,
    [switch]$SelfSign,
    [switch]$RequireSigning,
    [switch]$RequireCleanSource
)

$ErrorActionPreference = 'Stop'
$RuntimeIdentifier = 'win-x64'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$resolvedOutput = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputDirectory))
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts'))
if (-not $resolvedOutput.StartsWith($artifactsRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'OutputDirectory must resolve beneath the repository artifacts directory.'
}

function Find-SignTool {
    $command = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($null -ne $command) { return $command.Source }

    $windowsKitsBin = 'C:\Program Files (x86)\Windows Kits\10\bin'
    if (-not [IO.Directory]::Exists($windowsKitsBin)) {
        throw 'SignTool was not found. Install the Windows SDK signing tools.'
    }

    $candidate = Get-ChildItem -LiteralPath $windowsKitsBin -Filter signtool.exe -Recurse -File |
        Where-Object { $_.Directory.Name -eq 'x64' } |
        Sort-Object { [version]$_.Directory.Parent.Name } -Descending |
        Select-Object -First 1
    if ($null -eq $candidate) { throw 'The Windows SDK does not contain an x64 SignTool.' }
    return $candidate.FullName
}

$buildProperties = [xml][IO.File]::ReadAllText((Join-Path $repositoryRoot 'Directory.Build.props'))
$version = [string]($buildProperties.Project.PropertyGroup.Version | Select-Object -First 1)
if ([string]::IsNullOrWhiteSpace($version)) { throw 'Directory.Build.props does not define Version.' }
if (-not [string]::IsNullOrWhiteSpace($ExpectedVersion) -and $version -ne $ExpectedVersion) {
    throw "Release version '$version' does not match expected version '$ExpectedVersion'."
}
$versionMatch = [regex]::Match($version, '^(?<core>\d+\.\d+\.\d+)(?:-[0-9A-Za-z.-]+)?$')
if (-not $versionMatch.Success) {
    throw "Directory.Build.props Version '$version' is not a supported semantic version."
}
$installerVersion = $versionMatch.Groups['core'].Value
$releaseChannel = if ($version.IndexOf('-', [StringComparison]::Ordinal) -ge 0) { 'preview' } else { 'stable' }

$headRevision = (& git -C $repositoryRoot rev-parse HEAD 2>$null)
$hasGitRevision = $LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($headRevision)
if ($hasGitRevision) { $headRevision = $headRevision.Trim().ToLowerInvariant() }

if ([string]::IsNullOrWhiteSpace($SourceRevision)) {
    if (-not $hasGitRevision) { throw 'Unable to resolve the source revision. Provide SourceRevision explicitly.' }
    $SourceRevision = $headRevision
}
if ($SourceRevision -notmatch '^[A-Fa-f0-9]{40}$') {
    throw 'SourceRevision must be the full 40-character Git commit hash.'
}
$SourceRevision = $SourceRevision.ToLowerInvariant()
if ($RequireCleanSource) {
    if (-not $hasGitRevision) { throw 'RequireCleanSource requires a Git checkout.' }
    if ($SourceRevision -ne $headRevision) { throw 'SourceRevision does not match the checked-out Git commit.' }
    $trackedChanges = @(& git -C $repositoryRoot status --porcelain --untracked-files=all)
    if ($LASTEXITCODE -ne 0) { throw 'Unable to verify source cleanliness.' }
    if ($trackedChanges.Count -ne 0) { throw 'Release source contains uncommitted tracked changes.' }
}

if ([IO.Directory]::Exists($resolvedOutput)) {
    [IO.Directory]::Delete($resolvedOutput, $true)
}
[IO.Directory]::CreateDirectory($resolvedOutput) | Out-Null

$packageName = "EngineeringMcp-$version-$RuntimeIdentifier"
$packageOutput = Join-Path $resolvedOutput $packageName
$hostOutput = Join-Path $packageOutput 'host'
$configOutput = Join-Path $packageOutput 'config'
$docsOutput = Join-Path $packageOutput 'docs'
$licensePath = Join-Path $repositoryRoot 'LICENSE'
$noticePath = Join-Path $repositoryRoot 'NOTICE'
[IO.Directory]::CreateDirectory($packageOutput) | Out-Null
[IO.Directory]::CreateDirectory($hostOutput) | Out-Null
[IO.Directory]::CreateDirectory($configOutput) | Out-Null
[IO.Directory]::CreateDirectory($docsOutput) | Out-Null

$hostProject = Join-Path $repositoryRoot 'src/EngineeringMcp.Host/EngineeringMcp.Host.csproj'
$controlCenterProject = Join-Path $repositoryRoot 'src/EngineeringMcp.ControlCenter/EngineeringMcp.ControlCenter.csproj'

dotnet publish $hostProject -c $Configuration -r $RuntimeIdentifier --self-contained true -p:DebugType=None -p:DebugSymbols=false -o $hostOutput
if ($LASTEXITCODE -ne 0) { throw 'Host publish failed.' }
dotnet publish $controlCenterProject -c $Configuration -r $RuntimeIdentifier --self-contained true -p:DebugType=None -p:DebugSymbols=false -o $packageOutput
if ($LASTEXITCODE -ne 0) { throw 'Control Center publish failed.' }

Copy-Item -LiteralPath (Join-Path $repositoryRoot 'config/policy.packaged.json') -Destination $configOutput
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'config/policy.schema.json') -Destination $configOutput
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'docs/SECURITY.md') -Destination $docsOutput
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'docs/CODE-SIGNING-POLICY.md') -Destination $docsOutput
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'docs/VSCODE.md') -Destination $docsOutput
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'docs/WPF-WORKSPACES.md') -Destination $docsOutput
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'README.md') -Destination (Join-Path $docsOutput 'README.md')
Copy-Item -LiteralPath $licensePath -Destination (Join-Path $packageOutput 'LICENSE.txt')
Copy-Item -LiteralPath $noticePath -Destination (Join-Path $packageOutput 'NOTICE.txt')

$signingKind = 'unsigned'
if ($SelfSign) {
    $selfSignSubject = 'CN=Engineering MCP Development'
    $certificate = Get-ChildItem -Path Cert:\CurrentUser\My -CodeSigningCert |
        Where-Object {
            $_.Subject -eq $selfSignSubject -and
            $_.HasPrivateKey -and
            $_.NotAfter -gt [DateTime]::Now.AddDays(30)
        } |
        Sort-Object NotAfter -Descending |
        Select-Object -First 1

    if ($null -eq $certificate) {
        $certificate = New-SelfSignedCertificate `
            -Type CodeSigningCert `
            -Subject $selfSignSubject `
            -CertStoreLocation 'Cert:\CurrentUser\My' `
            -HashAlgorithm SHA256 `
            -KeyAlgorithm RSA `
            -KeyLength 3072 `
            -KeyExportPolicy NonExportable `
            -NotAfter ([DateTime]::Now.AddYears(2))
    }

    $CertificateThumbprint = $certificate.Thumbprint
    $publicCertificatePath = Join-Path $docsOutput 'EngineeringMcp-Development-CodeSigning.cer'
    Export-Certificate -Cert $certificate -FilePath $publicCertificatePath -Force | Out-Null
    $signingKind = 'development-self-signed'
}

$manifest = [ordered]@{
    schemaVersion = 1
    product = 'Engineering MCP'
    version = $version
    sourceRevision = $SourceRevision
    runtimeIdentifier = $RuntimeIdentifier
    selfContained = $true
    entryPoint = 'EngineeringMcp.ControlCenter.exe'
    host = 'host/EngineeringMcp.Host.exe'
    defaultPolicy = 'config/policy.packaged.json'
    createdUtc = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
    signing = [ordered]@{
        kind = $signingKind
        certificateThumbprint = if ([string]::IsNullOrWhiteSpace($CertificateThumbprint)) { $null } else { $CertificateThumbprint }
        publicCertificate = if ($SelfSign) { 'docs/EngineeringMcp-Development-CodeSigning.cer' } else { $null }
    }
    update = [ordered]@{
        channel = $releaseChannel
        manifestUrl = $null
    }
}
[IO.File]::WriteAllText(
    (Join-Path $packageOutput 'app-manifest.json'),
    ($manifest | ConvertTo-Json -Depth 5),
    [Text.UTF8Encoding]::new($false))

$dependencyInventoryPath = Join-Path $resolvedOutput 'dependencies.json'
$dependencyJson = dotnet list (Join-Path $repositoryRoot 'DotNetEngineeringMcp.sln') package --include-transitive --format json --no-restore
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
        versionInfo = $version
        downloadLocation = 'NOASSERTION'
        filesAnalyzed = $false
        licenseConcluded = 'Apache-2.0'
        licenseDeclared = 'Apache-2.0'
        copyrightText = 'Copyright 2026 mangyan1'
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

$sbomPath = Join-Path $resolvedOutput 'sbom.spdx.json'
$sbom = [ordered]@{
    spdxVersion = 'SPDX-2.3'
    dataLicense = 'CC0-1.0'
    SPDXID = 'SPDXRef-DOCUMENT'
    name = "EngineeringMcp-$version"
    documentNamespace = 'https://example.invalid/spdx/DotNetEngineeringMcp/' + [Guid]::NewGuid().ToString('N')
    creationInfo = [ordered]@{ created = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ'); creators = @('Tool: build/release-hardening.ps1') }
    packages = $spdxPackages
    relationships = $relationships
}
[IO.File]::WriteAllText($sbomPath, ($sbom | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
Copy-Item -LiteralPath $dependencyInventoryPath -Destination $docsOutput
Copy-Item -LiteralPath $sbomPath -Destination $docsOutput

if (-not [string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
    if ($CertificateThumbprint -notmatch '^[A-Fa-f0-9]{40,64}$') { throw 'Signing certificate thumbprint is invalid.' }
    $signTool = Find-SignTool
    $signedFiles = @(Get-ChildItem -LiteralPath $packageOutput -Recurse -File |
        Where-Object { $_.Name -like 'EngineeringMcp.*' -and $_.Extension -in '.exe', '.dll' })
    foreach ($file in $signedFiles) {
        & $signTool sign /sha1 $CertificateThumbprint /fd SHA256 /tr 'http://timestamp.digicert.com' /td SHA256 $file.FullName
        if ($LASTEXITCODE -ne 0) { throw "Authenticode signing failed for $($file.Name)." }
        $signature = Get-AuthenticodeSignature -LiteralPath $file.FullName
        if ($null -eq $signature.SignerCertificate -or
            $signature.SignerCertificate.Thumbprint -ne $CertificateThumbprint) {
            throw "Authenticode signature verification failed for $($file.Name)."
        }
    }
} elseif ($RequireSigning) {
    throw 'Release signing is required but ENGINEERING_MCP_SIGNING_THUMBPRINT was not provided.'
}

$installerPayloadSource = Join-Path $resolvedOutput 'installer-payload.wxs'
$installerLicenseRtf = Join-Path $resolvedOutput 'installer-license.rtf'
& (Join-Path $repositoryRoot 'build/New-InstallerPayload.ps1') `
    -PayloadDirectory $packageOutput `
    -OutputFile $installerPayloadSource
if ($LASTEXITCODE -ne 0) { throw 'Installer payload authoring failed.' }
& (Join-Path $repositoryRoot 'build/New-LicenseRtf.ps1') `
    -LicenseFile $licensePath `
    -OutputFile $installerLicenseRtf
if ($LASTEXITCODE -ne 0) { throw 'Installer license generation failed.' }

$installerName = $packageName + '-Setup'
$installerProject = Join-Path $repositoryRoot 'installer/EngineeringMcp.Installer.wixproj'
$installerOutputPath = $resolvedOutput + '\'
dotnet build $installerProject `
    -c $Configuration `
    -p:PayloadSourceFile=$installerPayloadSource `
    -p:ProductVersion=$installerVersion `
    -p:RepositoryRoot=$repositoryRoot `
    -p:LicenseRtf=$installerLicenseRtf `
    -p:OutputName=$installerName `
    -p:OutputPath=$installerOutputPath
if ($LASTEXITCODE -ne 0) { throw 'MSI installer build failed.' }

$installerPath = Join-Path $resolvedOutput ($installerName + '.msi')
if (-not [IO.File]::Exists($installerPath)) { throw 'MSI installer output is missing.' }
if (-not [string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
    if ([string]::IsNullOrWhiteSpace($signTool)) { $signTool = Find-SignTool }
    & $signTool sign /sha1 $CertificateThumbprint /fd SHA256 /tr 'http://timestamp.digicert.com' /td SHA256 $installerPath
    if ($LASTEXITCODE -ne 0) { throw 'Authenticode signing failed for the MSI installer.' }
    $installerSignature = Get-AuthenticodeSignature -LiteralPath $installerPath
    if ($null -eq $installerSignature.SignerCertificate -or
        $installerSignature.SignerCertificate.Thumbprint -ne $CertificateThumbprint) {
        throw 'Authenticode signature verification failed for the MSI installer.'
    }
}

[IO.File]::Delete($installerPayloadSource)
[IO.File]::Delete($installerLicenseRtf)
[IO.File]::Delete((Join-Path $resolvedOutput ($installerName + '.wixpdb')))

$archivePath = Join-Path $resolvedOutput ($packageName + '.zip')
Compress-Archive -LiteralPath $packageOutput -DestinationPath $archivePath -CompressionLevel Optimal

$outputUri = [Uri]($resolvedOutput.TrimEnd('\') + '\')
$checksums = Get-ChildItem -LiteralPath $resolvedOutput -Recurse -File |
    Where-Object Name -ne 'SHA256SUMS.txt' |
    Sort-Object FullName |
    ForEach-Object {
        $relative = [Uri]::UnescapeDataString($outputUri.MakeRelativeUri([Uri]$_.FullName).ToString())
        '{0}  {1}' -f (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash.ToLowerInvariant(), $relative
    }
$checksums | Set-Content -LiteralPath (Join-Path $resolvedOutput 'SHA256SUMS.txt') -Encoding ascii

Write-Host "Self-contained package: $archivePath"
Write-Host "Windows installer: $installerPath"
Write-Host "Release directory: $resolvedOutput"
if ([string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
    Write-Warning 'The package is unsigned. Provide ENGINEERING_MCP_SIGNING_THUMBPRINT and -RequireSigning for an official release.'
} elseif ($SelfSign) {
    Write-Warning 'The package uses a development self-signed certificate. Other machines will not trust its publisher until the included public certificate is explicitly trusted.'
}
