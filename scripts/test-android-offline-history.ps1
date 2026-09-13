param(
    [Parameter(Mandatory)][string]$AdbPath,
    [Parameter(Mandatory)][string]$Serial,
    [Parameter(Mandatory)][ValidateSet('Denied', 'Off')][string]$ExpectedAccess,
    [string]$EvidenceDirectory
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $EvidenceDirectory) { $EvidenceDirectory = Join-Path $root ".acceptance\android-offline-history\$ExpectedAccess" }
New-Item -ItemType Directory -Path $EvidenceDirectory -Force | Out-Null
$EvidenceDirectory = (Resolve-Path -LiteralPath $EvidenceDirectory).Path
$package = 'com.bluelink.android'
$checks = [Collections.Generic.List[string]]::new()

function Invoke-TestAdb {
    $output = & $AdbPath -s $Serial @args
    if ($LASTEXITCODE -ne 0) { throw "ADB failed: $($args -join ' ')" }
    $output
}

function Read-TestUi([string]$Name, [switch]$Screenshot) {
    Invoke-TestAdb shell uiautomator dump /sdcard/bluelink-offline-test.xml | Out-Null
    $xmlPath = Join-Path $EvidenceDirectory ($Name + '.xml')
    Invoke-TestAdb pull /sdcard/bluelink-offline-test.xml $xmlPath | Out-Null
    if ($Screenshot) {
        Invoke-TestAdb shell screencap -p /sdcard/bluelink-offline-test.png | Out-Null
        Invoke-TestAdb pull /sdcard/bluelink-offline-test.png (Join-Path $EvidenceDirectory ($Name + '.png')) | Out-Null
    }
    [xml](Get-Content -LiteralPath $xmlPath -Raw)
}

function Has-TestText([xml]$Tree, [string]$Text) {
    [bool]($Tree.SelectNodes('//node') | Where-Object { $_.text -ceq $Text })
}

function Assert-Test([bool]$Condition, [string]$Description) {
    if (-not $Condition) { throw $Description }
    $checks.Add($Description)
    Write-Host "PASS: $Description"
}

function Tap-TestNode([xml]$Tree, [string]$Label) {
    $node = $Tree.SelectNodes('//node') | Where-Object { $_.text -ceq $Label -or $_.'content-desc' -ceq $Label } | Select-Object -First 1
    if (-not $node) { throw "Missing UI node: $Label" }
    $bounds = @([regex]::Matches($node.bounds, '\d+') | ForEach-Object { [int]$_.Value })
    if ($bounds.Count -ne 4 -or $bounds[2] -le $bounds[0] -or $bounds[3] -le $bounds[1]) { throw "Invalid node bounds: $Label" }
    Invoke-TestAdb shell input tap ([int](($bounds[0]+$bounds[2])/2)) ([int](($bounds[1]+$bounds[3])/2)) | Out-Null
}

# This test writes only its own fixture rows, and only on the dedicated emulator.
$avdName = @(Invoke-TestAdb emu avd name)[0].Trim()
if ($avdName -notin @('BlueLink_API_34', 'BlueLink_API_34_Recovery')) { throw 'Use a dedicated BlueLink API 34 test emulator.' }
Invoke-TestAdb shell run-as $package id | Out-Null
$existing = @(Invoke-TestAdb shell run-as $package sqlite3 databases/bluelink.db '"SELECT count(*) FROM peer WHERE peerId IN (''qa-b1-offline-pc'',''qa-b1-offline-phone'');"')[0].Trim()
if ($existing -ne '0') { throw 'Fixture IDs already exist. Inspect them before rerunning.' }
$seed = @'
.bail on
PRAGMA foreign_keys=ON;
BEGIN IMMEDIATE;
INSERT INTO peer VALUES ('qa-b1-offline-pc',NULL,'QA-PC-离线测试','WINDOWS','UNKNOWN',1788612000000,1788612000000,1788612000000,'');
INSERT INTO peer VALUES ('qa-b1-offline-phone',NULL,'QA-Phone-离线测试','ANDROID','UNKNOWN',1788611000000,1788611000000,1788611000000,'');
INSERT INTO conversation VALUES ('peer:qa-b1-offline-pc','qa-b1-offline-pc',1788612000000,2,'');
INSERT INTO conversation VALUES ('peer:qa-b1-offline-phone','qa-b1-offline-phone',1788611000000,0,'');
INSERT INTO message VALUES ('0f4901fd-d663-4799-b9a4-e1e77911c930','peer:qa-b1-offline-pc','qa-b1-offline-pc','INCOMING','TEXT','QA：离线历史测试记录（测试数据）','RECEIVED',1788612000000,1788612000000);
COMMIT;
'@
$cleanup = @'
.bail on
PRAGMA foreign_keys=ON;
BEGIN IMMEDIATE;
DELETE FROM peer WHERE peerId IN ('qa-b1-offline-pc','qa-b1-offline-phone');
COMMIT;
'@
Set-Content -LiteralPath (Join-Path $EvidenceDirectory 'fixture.sql') -Value $seed -Encoding utf8
Set-Content -LiteralPath (Join-Path $EvidenceDirectory 'cleanup.sql') -Value $cleanup -Encoding utf8
Invoke-TestAdb push (Join-Path $EvidenceDirectory 'fixture.sql') /data/local/tmp/bluelink-offline-fixture.sql | Out-Null
Invoke-TestAdb push (Join-Path $EvidenceDirectory 'cleanup.sql') /data/local/tmp/bluelink-offline-cleanup.sql | Out-Null
$failure = $null
try {
    Invoke-TestAdb shell am force-stop $package | Out-Null
    Invoke-TestAdb shell 'run-as com.bluelink.android sqlite3 databases/bluelink.db < /data/local/tmp/bluelink-offline-fixture.sql' | Out-Null
    Invoke-TestAdb shell am start -W -n "$package/.MainActivity" | Out-Null
    for ($attempt = 0; $attempt -lt 4; $attempt++) {
        $tree = Read-TestUi 'cold-start' -Screenshot
        if (Has-TestText $tree 'QA-PC-离线测试') { break }
    }
    $stateText = if ($ExpectedAccess -eq 'Denied') { '权限已关闭' } else { '蓝牙未开启' }
    Assert-Test (Has-TestText $tree $stateText) "Expected $ExpectedAccess state"
    Assert-Test ((Has-TestText $tree 'QA-PC-离线测试') -and (Has-TestText $tree 'QA-Phone-离线测试')) 'Cold start loads stored history without Bluetooth'
    Tap-TestNode $tree '折叠离线'
    $tree = Read-TestUi 'collapsed' -Screenshot
    Assert-Test (-not (Has-TestText $tree 'QA-PC-离线测试')) 'Collapse hides stored cards'
    Tap-TestNode $tree '展开离线'
    $tree = Read-TestUi 'expanded'
    Tap-TestNode $tree '搜索历史会话'
    Invoke-TestAdb shell input text 'QA-PC' | Out-Null
    Invoke-TestAdb shell input keyevent KEYCODE_BACK | Out-Null
    $tree = Read-TestUi 'search-match' -Screenshot
    Assert-Test ((Has-TestText $tree 'QA-PC-离线测试') -and -not (Has-TestText $tree 'QA-Phone-离线测试')) 'Offline search filters stored names'
    Tap-TestNode $tree 'QA-PC-离线测试'
    $tree = Read-TestUi 'open-history' -Screenshot
    Assert-Test (Has-TestText $tree 'QA：离线历史测试记录（测试数据）') 'Stored messages remain readable without Bluetooth'
    Invoke-TestAdb shell input keyevent KEYCODE_BACK | Out-Null
    $tree = Read-TestUi 'returned-home'
    Assert-Test (Has-TestText $tree '设备与会话') 'Back returns to the device screen'
} catch {
    $failure = $_.Exception.Message
    throw
} finally {
    Invoke-TestAdb shell am force-stop $package | Out-Null
    Invoke-TestAdb shell 'run-as com.bluelink.android sqlite3 databases/bluelink.db < /data/local/tmp/bluelink-offline-cleanup.sql' | Out-Null
    Invoke-TestAdb shell am start -W -n "$package/.MainActivity" | Out-Null
    [ordered]@{Timestamp=(Get-Date).ToString('o');ExpectedAccess=$ExpectedAccess;Checks=@($checks);Failure=$failure;FixturesRemoved=$true} |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $EvidenceDirectory 'result.json') -Encoding utf8
}
