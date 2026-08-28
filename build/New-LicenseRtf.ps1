[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$LicenseFile,
    [Parameter(Mandatory)]
    [string]$OutputFile
)

$ErrorActionPreference = 'Stop'
$licensePath = [IO.Path]::GetFullPath($LicenseFile)
$outputPath = [IO.Path]::GetFullPath($OutputFile)
if (-not [IO.File]::Exists($licensePath)) { throw "License file does not exist: $licensePath" }
if ([string]::Equals($licensePath, $outputPath, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The RTF output must not overwrite the source license.'
}

$text = [IO.File]::ReadAllText($licensePath)
$escaped = $text.Replace('\', '\\').Replace('{', '\{').Replace('}', '\}')
$escaped = $escaped.Replace("`r`n", '\par ' + [Environment]::NewLine).Replace("`n", '\par ' + [Environment]::NewLine)
$rtf = '{\rtf1\ansi\deff0{\fonttbl{\f0 Segoe UI;}}\viewkind4\uc1\fs18 ' + $escaped + '}'
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($outputPath)) | Out-Null
[IO.File]::WriteAllText($outputPath, $rtf, [Text.ASCIIEncoding]::new())

Write-Host "Generated installer license agreement: $outputPath"
