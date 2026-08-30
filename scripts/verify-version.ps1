param([string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot))

$ErrorActionPreference = 'Stop'
$version = (Get-Content -LiteralPath (Join-Path $ProjectRoot 'VERSION') -Raw).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "VERSION 格式无效：$version" }
if ([version]$version -lt [version]'0.2.12') {
    throw "Windows 安装器版本不得回退到存在嵌入式升级挂起缺陷的版本：$version"
}

$checks = @(
    @{ Path = 'installer\BlueLink.Package\Package.wxs'; Pattern = 'Version="$(var.ProductVersion)"' },
    @{ Path = 'installer\BlueLink.Bundle\Bundle.wxs'; Pattern = 'Version="$(var.ProductVersion)"' },
    @{ Path = 'android\app\build.gradle.kts'; Pattern = 'versionName = productVersion' },
    @{ Path = 'Directory.Build.props'; Pattern = '<Version>$(BlueLinkVersion)</Version>' }
)
foreach ($check in $checks) {
    $content = Get-Content -LiteralPath (Join-Path $ProjectRoot $check.Path) -Raw
    if (-not $content.Contains($check.Pattern)) {
        throw "版本基线未接入：$($check.Path)"
    }
}

Write-Host "BlueLink version baseline verified: $version"
