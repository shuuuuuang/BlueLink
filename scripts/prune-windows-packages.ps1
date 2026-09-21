[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ArtifactRoot,
    [Parameter(Mandatory)][string]$SuccessfulDirectory,
    [ValidateRange(1,20)][int]$Keep = 3,
    [switch]$Apply
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $ArtifactRoot).Path.TrimEnd('\')
$latest = (Resolve-Path -LiteralPath $SuccessfulDirectory).Path.TrimEnd('\')
if ($root -notmatch '[\\/]artifacts(?:[\\/].*)?$' -or
    [IO.Path]::GetDirectoryName($latest) -ne $root) { throw 'Retention requires a direct completed package directory inside artifacts.' }
function Assert-SafePath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    if (-not $full.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) { throw "Path outside artifact root: $full" }
    $relative = $full.Substring($root.Length + 1)
    if ($relative -match '(^|[\\/])(Data|Download|\.git)([\\/]|$)') { throw "Protected data path: $full" }
    for ($node = Get-Item -LiteralPath $full -Force; $null -ne $node; $node = $node.Parent) {
        if ($node.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Reparse path is not eligible: $full" }
        if ($node.FullName -eq $root) { break }
        if ($node -is [IO.FileInfo]) { $node = $node.Directory; if ($node.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse parent.' }; if ($node.FullName -eq $root) { break } }
    }
}
function Test-PackageName([string]$Name) {
    return $Name -match '^BlueLink.*\.msi$' -or
        $Name -match '^BlueLink.*(-Setup|Setup-|Review).*\.exe$' -or
        $Name -match '^BlueLink.*(win-|windows).*\.zip$'
}
# A successful manifest and matching bytes are mandatory even for an explicit cleanup call.
$manifestPath = Join-Path $latest 'build-manifest.json'
Assert-SafePath $manifestPath
$manifest = @(Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json)
if ($manifest.Count -eq 0) { throw 'Empty successful build manifest.' }
foreach ($entry in $manifest) {
    if ([IO.Path]::GetFileName($entry.File) -ne $entry.File -or -not (Test-PackageName $entry.File)) { throw 'Invalid artifact manifest filename.' }
    $file = Join-Path $latest $entry.File
    Assert-SafePath $file
    if ((Get-Item -LiteralPath $file).Length -ne $entry.Size -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $entry.Sha256) { throw "Successful package integrity mismatch: $file" }
}
$packages = @(Get-ChildItem -LiteralPath $root -Recurse -File -Force | Where-Object {
    (Test-PackageName $_.Name) -and $_.FullName.Substring($root.Length + 1) -notmatch '(^|[\\/])(Data|Download|\.git)([\\/]|$)'
})
$groups = @($packages | Group-Object { $_.FullName.Substring($root.Length + 1).Split('\')[0] })
$completed = @($groups | ForEach-Object {
    $folder = Join-Path $root $_.Name
    $top = @($_.Group | Where-Object DirectoryName -eq $folder)
    if ($top.Count -gt 0) { [pscustomobject]@{ Name=$_.Name; Date=($top | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1).LastWriteTimeUtc } }
} | Sort-Object Date -Descending)
$retained = @($completed | Select-Object -First $Keep -ExpandProperty Name)
if ([IO.Path]::GetFileName($latest) -notin $retained) { throw 'Successful build is not among the newest sets; refusing cleanup.' }
$removed = @($groups | Where-Object Name -NotIn $retained | ForEach-Object Group)
# Validate every final absolute target before any mutation. Never recursively delete build/data directories.
foreach ($file in $removed) { Assert-SafePath $file.FullName }
$bytes = ($removed | Measure-Object Length -Sum).Sum
if ($Apply) {
    foreach ($file in $removed) { Remove-Item -LiteralPath $file.FullName -Force }
    foreach ($group in $groups | Where-Object Name -NotIn $retained) {
        $folder = Join-Path $root $group.Name
        foreach ($sidecar in @('SHA256SUMS.txt','build-manifest.json')) {
            $path = Join-Path $folder $sidecar
            if (-not (Test-Path -LiteralPath $path)) { continue }
            Assert-SafePath $path
            if ($sidecar -eq 'SHA256SUMS.txt') {
                $remaining = @(Get-Content -LiteralPath $path | Where-Object { $_ -notmatch '\s+(\*?BlueLink.*(?:\.exe|\.msi|\.zip))$' })
                if ($remaining.Count) { $remaining | Set-Content -LiteralPath $path -Encoding utf8 } else { Remove-Item -LiteralPath $path -Force }
            } else {
                $remaining = @(Get-Content -LiteralPath $path -Raw | ConvertFrom-Json | Where-Object { -not (Test-PackageName $_.File) })
                if ($remaining.Count) { ConvertTo-Json -InputObject $remaining -Depth 10 | Set-Content -LiteralPath $path -Encoding utf8 } else { Remove-Item -LiteralPath $path -Force }
            }
        }
        if ((Get-ChildItem -LiteralPath $folder -Force | Measure-Object).Count -eq 0) {
            Assert-SafePath $folder
            Remove-Item -LiteralPath $folder
        }
    }
}
[pscustomobject]@{ Applied=[bool]$Apply; Root=$root; Retained=$retained; RemovedFiles=$removed.Count; ReclaimedBytes=$bytes }
