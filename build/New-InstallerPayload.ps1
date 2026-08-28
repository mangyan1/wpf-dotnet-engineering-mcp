[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$PayloadDirectory,
    [Parameter(Mandatory)]
    [string]$OutputFile
)

$ErrorActionPreference = 'Stop'
$payloadRoot = [IO.Path]::GetFullPath($PayloadDirectory).TrimEnd('\')
$outputPath = [IO.Path]::GetFullPath($OutputFile)
if (-not [IO.Directory]::Exists($payloadRoot)) {
    throw "Installer payload directory does not exist: $payloadRoot"
}
if ($outputPath.StartsWith($payloadRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Generated WiX source must be outside the payload directory.'
}

function Get-StableIdentity([string]$relativePath) {
    $normalized = $relativePath.Replace('\', '/').ToLowerInvariant()
    $bytes = [Text.Encoding]::UTF8.GetBytes('EngineeringMcp.Installer/v1/' + $normalized)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { $hash = $algorithm.ComputeHash($bytes) }
    finally { $algorithm.Dispose() }
    $guidBytes = [byte[]]::new(16)
    [Array]::Copy($hash, $guidBytes, 16)
    $guidBytes[7] = ($guidBytes[7] -band 0x0f) -bor 0x40
    $guidBytes[8] = ($guidBytes[8] -band 0x3f) -bor 0x80
    $hex = [BitConverter]::ToString($hash).Replace('-', '').ToLowerInvariant()
    return [pscustomobject]@{
        ComponentId = 'cmp_' + $hex.Substring(0, 24)
        FileId = 'fil_' + $hex.Substring(0, 24)
        Guid = [Guid]::new($guidBytes).ToString('B').ToUpperInvariant()
    }
}

$settings = [Xml.XmlWriterSettings]::new()
$settings.Encoding = [Text.UTF8Encoding]::new($false)
$settings.Indent = $true
$settings.NewLineChars = [Environment]::NewLine
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($outputPath)) | Out-Null

$writer = [Xml.XmlWriter]::Create($outputPath, $settings)
try {
    $writer.WriteStartDocument()
    $writer.WriteStartElement('Wix', 'http://wixtoolset.org/schemas/v4/wxs')
    $writer.WriteStartElement('Fragment')
    $writer.WriteStartElement('ComponentGroup')
    $writer.WriteAttributeString('Id', 'PayloadComponents')
    $writer.WriteAttributeString('Directory', 'INSTALLFOLDER')

    $payloadUri = [Uri]($payloadRoot + '\')
    $files = Get-ChildItem -LiteralPath $payloadRoot -Recurse -File | Sort-Object FullName
    foreach ($file in $files) {
        $relativePath = [Uri]::UnescapeDataString($payloadUri.MakeRelativeUri([Uri]$file.FullName).ToString()).Replace('/', '\')
        $relativeDirectory = [IO.Path]::GetDirectoryName($relativePath)
        $identity = Get-StableIdentity $relativePath

        $writer.WriteStartElement('Component')
        $writer.WriteAttributeString('Id', $identity.ComponentId)
        $writer.WriteAttributeString('Guid', $identity.Guid)
        if (-not [string]::IsNullOrWhiteSpace($relativeDirectory)) {
            $writer.WriteAttributeString('Subdirectory', $relativeDirectory)
        }

        $writer.WriteStartElement('File')
        $writer.WriteAttributeString('Id', $identity.FileId)
        $writer.WriteAttributeString('Source', $file.FullName)
        if ($relativePath -ne 'EngineeringMcp.ControlCenter.exe') {
            $writer.WriteAttributeString('KeyPath', 'yes')
        }

        if ($relativePath -eq 'EngineeringMcp.ControlCenter.exe') {
            foreach ($shortcut in @(
                @{ Id = 'StartMenuShortcut'; Directory = 'ApplicationProgramsFolder' },
                @{ Id = 'DesktopShortcut'; Directory = 'DesktopFolder' }
            )) {
                $writer.WriteStartElement('Shortcut')
                $writer.WriteAttributeString('Id', $shortcut.Id)
                $writer.WriteAttributeString('Directory', $shortcut.Directory)
                $writer.WriteAttributeString('Name', 'Engineering MCP')
                $writer.WriteAttributeString('Description', 'Engineering MCP Control Center')
                $writer.WriteAttributeString('WorkingDirectory', 'INSTALLFOLDER')
                $writer.WriteAttributeString('Icon', 'EngineeringMcpIcon')
                $writer.WriteAttributeString('Advertise', 'no')
                $writer.WriteEndElement()
            }
        }

        $writer.WriteEndElement()
        if ($relativePath -eq 'EngineeringMcp.ControlCenter.exe') {
            $writer.WriteStartElement('RegistryValue')
            $writer.WriteAttributeString('Root', 'HKCU')
            $writer.WriteAttributeString('Key', 'Software\EngineeringMcp')
            $writer.WriteAttributeString('Name', 'InstalledVersion')
            $writer.WriteAttributeString('Type', 'string')
            $writer.WriteAttributeString('Value', '[ProductVersion]')
            $writer.WriteAttributeString('KeyPath', 'yes')
            $writer.WriteEndElement()

            $writer.WriteStartElement('RemoveFolder')
            $writer.WriteAttributeString('Id', 'RemoveApplicationProgramsFolder')
            $writer.WriteAttributeString('Directory', 'ApplicationProgramsFolder')
            $writer.WriteAttributeString('On', 'uninstall')
            $writer.WriteEndElement()
        }
        $writer.WriteEndElement()
    }

    $writer.WriteEndElement()
    $writer.WriteEndElement()
    $writer.WriteEndElement()
    $writer.WriteEndDocument()
}
finally {
    $writer.Dispose()
}

Write-Host "Generated installer payload authoring for $($files.Count) files: $outputPath"
