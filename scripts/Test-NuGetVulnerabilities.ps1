[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$nugetSource = 'https://api.nuget.org/v3/index.json'
$targets = @(
    'DotNetEngineeringMcp.sln',
    'installer/EngineeringMcp.Installer.wixproj'
)
$findings = [Collections.Generic.List[object]]::new()

Push-Location -LiteralPath $repositoryRoot
try {
    foreach ($target in $targets) {
        $output = & dotnet package list `
            --project $target `
            --vulnerable `
            --include-transitive `
            --source $nugetSource `
            --format json `
            --no-restore

        if ($LASTEXITCODE -ne 0) {
            throw "NuGet vulnerability query failed for $target."
        }

        $report = ($output -join [Environment]::NewLine) | ConvertFrom-Json
        foreach ($project in @($report.projects)) {
            $frameworksProperty = $project.PSObject.Properties['frameworks']
            if ($null -eq $frameworksProperty) {
                continue
            }

            foreach ($framework in @($frameworksProperty.Value)) {
                foreach ($collectionName in @('topLevelPackages', 'transitivePackages')) {
                    $collectionProperty = $framework.PSObject.Properties[$collectionName]
                    if ($null -eq $collectionProperty) {
                        continue
                    }

                    foreach ($package in @($collectionProperty.Value)) {
                        if ($null -eq $package -or [string]::IsNullOrWhiteSpace($package.id)) {
                            continue
                        }

                        $vulnerabilitiesProperty = $package.PSObject.Properties['vulnerabilities']
                        if ($null -eq $vulnerabilitiesProperty) {
                            continue
                        }

                        foreach ($vulnerability in @($vulnerabilitiesProperty.Value)) {
                            if ($null -eq $vulnerability) {
                                continue
                            }

                            $findings.Add([pscustomobject]@{
                                Package = [string]$package.id
                                Version = [string]$package.resolvedVersion
                                Severity = [string]$vulnerability.severity
                            })
                        }
                    }
                }
            }
        }
    }
}
finally {
    Pop-Location
}

if ($findings.Count -gt 0) {
    foreach ($finding in $findings | Sort-Object Package, Version, Severity -Unique) {
        Write-Error ("Known NuGet vulnerability: package={0}, version={1}, severity={2}" -f `
            $finding.Package, $finding.Version, $finding.Severity)
    }

    throw "NuGet vulnerability scan failed with $($findings.Count) finding(s)."
}

Write-Host 'NuGet vulnerability scan: PASS (solution and installer, direct and transitive packages).'
