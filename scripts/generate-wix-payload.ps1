param(
    [Parameter(Mandatory = $true)][string]$AppPublishDir,
    [Parameter(Mandatory = $true)][string]$BootstrapDir,
    [Parameter(Mandatory = $true)][string]$ManifestPath,
    [Parameter(Mandatory = $true)][string]$OutputFile,
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$GuidNamespace = 'BlueLink.Package/'
)

$ErrorActionPreference = 'Stop'

function Get-StableToken([string]$Value) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { $bytes = $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($Value.ToLowerInvariant())) }
    finally { $sha.Dispose() }
    return ([BitConverter]::ToString($bytes).Replace('-', '')).Substring(0, 24)
}

function Get-RelativePath([string]$Root, [string]$Path) {
    $baseUri = [Uri]::new(([IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'))
    $pathUri = [Uri]::new([IO.Path]::GetFullPath($Path))
    return [Uri]::UnescapeDataString($baseUri.MakeRelativeUri($pathUri).ToString()).Replace('/', '\')
}

function Get-StableGuid([string]$Value) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { $bytes = $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($GuidNamespace + $Value.ToLowerInvariant())) }
    finally { $sha.Dispose() }
    $guidBytes = [byte[]]::new(16)
    [Array]::Copy($bytes, $guidBytes, 16)
    $guidBytes[7] = ($guidBytes[7] -band 0x0F) -bor 0x50
    $guidBytes[8] = ($guidBytes[8] -band 0x3F) -bor 0x80
    return ([Guid]::new($guidBytes)).ToString('B').ToUpperInvariant()
}

$appRoot = [IO.Path]::GetFullPath($AppPublishDir)
$bootstrapRoot = [IO.Path]::GetFullPath($BootstrapDir)
foreach ($path in @($appRoot, $bootstrapRoot)) {
    if (-not (Test-Path -LiteralPath $path -PathType Container)) { throw "Payload directory not found: $path" }
}

$owned = [Collections.Generic.List[string]]::new()
foreach ($path in @('BlueLink.exe', 'BlueLink.exe.config', 'Uninstall.exe', 'Uninstall.exe.config', '.bluelink-install.json')) { $owned.Add($path) }
foreach ($file in Get-ChildItem -LiteralPath $appRoot -Recurse -File | Where-Object Extension -ne '.pdb') {
    $owned.Add('app/' + (Get-RelativePath $appRoot $file.FullName).Replace('\', '/'))
}
foreach ($file in Get-ChildItem -LiteralPath $bootstrapRoot -Recurse -File | Where-Object Extension -ne '.pdb') {
    $owned.Add('bootstrap/' + (Get-RelativePath $bootstrapRoot $file.FullName).Replace('\', '/'))
}
$installRoot = [IO.Path]::GetFullPath((Split-Path -Parent $ManifestPath))
$payloadFiles = @($owned | Where-Object { $_ -ne '.bluelink-install.json' } | Sort-Object -Unique | ForEach-Object {
    $relativePath = [string]$_
    $sourcePath = Join-Path $installRoot $relativePath.Replace('/', '\')
    if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
        throw "Owned payload file was not staged: $relativePath"
    }
    $item = Get-Item -LiteralPath $sourcePath
    [ordered]@{
        Path = $relativePath
        Length = [long]$item.Length
        Sha256 = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash.ToUpperInvariant()
    }
})
$fingerprintMaterial = ($payloadFiles | ForEach-Object {
    "$($_.Path)|$($_.Length)|$($_.Sha256)"
}) -join "`n"
$fingerprintSha = [Security.Cryptography.SHA256]::Create()
try {
    $payloadFingerprint = ([BitConverter]::ToString(
        $fingerprintSha.ComputeHash([Text.Encoding]::UTF8.GetBytes($fingerprintMaterial))
    )).Replace('-', '')
}
finally { $fingerprintSha.Dispose() }
$manifest = [ordered]@{
    ProductId = 'BlueLink.Desktop'
    StructureVersion = 2
    ApplicationVersion = $Version
    BuildId = "$Version-$($payloadFingerprint.Substring(0, 16).ToLowerInvariant())"
    PayloadFingerprint = $payloadFingerprint
    PayloadFiles = $payloadFiles
    OwnedPaths = @($owned | Sort-Object -Unique)
}
$manifestDirectory = Split-Path -Parent $ManifestPath
New-Item -ItemType Directory -Path $manifestDirectory -Force | Out-Null
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $ManifestPath -Encoding utf8

$componentIds = [Collections.Generic.List[string]]::new()
$settings = [Xml.XmlWriterSettings]::new()
$settings.Indent = $true
$settings.Encoding = [Text.UTF8Encoding]::new($false)
$outputDirectory = Split-Path -Parent $OutputFile
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$writer = [Xml.XmlWriter]::Create($OutputFile, $settings)

function Write-DirectoryContents([Xml.XmlWriter]$Writer, [string]$Root, [string]$Relative, [string]$InstallPrefix) {
    $current = if ([String]::IsNullOrEmpty($Relative)) { $Root } else { Join-Path $Root $Relative }
    foreach ($file in Get-ChildItem -LiteralPath $current -File | Where-Object Extension -ne '.pdb' | Sort-Object Name) {
        $relativeFile = if ([String]::IsNullOrEmpty($Relative)) { $file.Name } else { Join-Path $Relative $file.Name }
        $logical = ($InstallPrefix + '/' + $relativeFile.Replace('\', '/')).TrimStart('/')
        $token = Get-StableToken $logical
        $componentId = 'PayloadComponent_' + $token
        $componentIds.Add($componentId)
        $Writer.WriteStartElement('Component')
        $Writer.WriteAttributeString('Id', $componentId)
        $Writer.WriteAttributeString('Guid', (Get-StableGuid $logical))
        $Writer.WriteStartElement('File')
        $Writer.WriteAttributeString('Id', 'PayloadFile_' + $token)
        $Writer.WriteAttributeString('Source', $file.FullName)
        $Writer.WriteAttributeString('Name', $file.Name)
        $Writer.WriteAttributeString('KeyPath', 'yes')
        $Writer.WriteEndElement()
        $Writer.WriteEndElement()
    }
    foreach ($directory in Get-ChildItem -LiteralPath $current -Directory | Sort-Object Name) {
        $childRelative = if ([String]::IsNullOrEmpty($Relative)) { $directory.Name } else { Join-Path $Relative $directory.Name }
        $Writer.WriteStartElement('Directory')
        $Writer.WriteAttributeString('Id', 'PayloadDirectory_' + (Get-StableToken ($InstallPrefix + '/' + $childRelative.Replace('\', '/'))))
        $Writer.WriteAttributeString('Name', $directory.Name)
        Write-DirectoryContents $Writer $Root $childRelative $InstallPrefix
        $Writer.WriteEndElement()
    }
}

try {
    $writer.WriteStartDocument()
    $writer.WriteStartElement('Wix', 'http://wixtoolset.org/schemas/v4/wxs')
    $writer.WriteStartElement('Fragment')
    $writer.WriteStartElement('DirectoryRef'); $writer.WriteAttributeString('Id', 'APPFOLDER')
    Write-DirectoryContents $writer $appRoot '' 'app'
    $writer.WriteEndElement()
    $writer.WriteStartElement('DirectoryRef'); $writer.WriteAttributeString('Id', 'BOOTSTRAPFOLDER')
    Write-DirectoryContents $writer $bootstrapRoot '' 'bootstrap'
    $writer.WriteEndElement()
    $writer.WriteEndElement()
    $writer.WriteStartElement('Fragment')
    $writer.WriteStartElement('ComponentGroup'); $writer.WriteAttributeString('Id', 'PublishedPayloadComponents')
    foreach ($id in $componentIds) { $writer.WriteStartElement('ComponentRef'); $writer.WriteAttributeString('Id', $id); $writer.WriteEndElement() }
    $writer.WriteEndElement(); $writer.WriteEndElement()
    $writer.WriteEndElement(); $writer.WriteEndDocument()
}
finally { $writer.Dispose() }

Write-Host "Generated manifest with $($manifest.OwnedPaths.Count) owned paths: $ManifestPath"
Write-Host "Payload fingerprint: $payloadFingerprint"
Write-Host "Generated $($componentIds.Count) WiX payload components: $OutputFile"
