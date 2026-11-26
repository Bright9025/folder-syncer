$ErrorActionPreference = 'Stop'

# 覆盖本轮新增功能：哈希清单、哈希不符保护、单文件大小上限
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent (Split-Path -Parent $here)
$exe = Join-Path $root 'release\upload\FolderSyncer.exe'
$mockExe = Join-Path $here 'MockFtpServer.exe'
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:exe /platform:anycpu /codepage:65001 /out:"$mockExe" /r:System.dll /r:System.Core.dll "$here\MockFtpServer.cs" | Out-Null

$t = Join-Path $root '_newfeat'
Remove-Item -Recurse -Force $t -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force "$t\up\sub", "$t\arc", "$t\remote" | Out-Null
$utf8 = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText("$t\up\a.txt", "AAA", $utf8)
[System.IO.File]::WriteAllText("$t\up\sub\b.txt", "BBBB", $utf8)
[System.IO.File]::WriteAllText("$t\up\big.bin", ("x" * 1048577), $utf8)   # 1 MB + 1 字节
$pass = 0; $fail = 0
function A($c, $m) { if ($c) { $script:pass++; "  [PASS] $m" } else { $script:fail++; "  [FAIL] $m" } }

function Cfg($port, $name, $maxMb) {
@"
[general]
log_dir=logs-$name
state_dir=state-$name

[server]
host = 127.0.0.1
port = $port
user = u
password = p
tls = off
allow_plaintext = true
manifest_dir = /_foldersyncer

[update]
enabled = false

[job:site]
enabled = true
local = $t\up
remote = /site
mode = upload
max_file_mb = $maxMb
verify_hash = true
"@ | Set-Content "$t\$name.ini" -Encoding UTF8
}

$mock = Start-Process -FilePath $mockExe -ArgumentList @("--root=$t\remote", '--port=2201') -PassThru -WindowStyle Hidden
Start-Sleep -Milliseconds 900
Cfg 2201 up 0

"== 1) 上传并生成哈希清单 =="
$p1 = Start-Process -FilePath $exe -ArgumentList @('--config', "$t\up.ini", '--sync') -Wait -PassThru -WindowStyle Hidden
A ($p1.ExitCode -eq 0) "sync exit 0"
$hashFile = "$t\remote\_foldersyncer\hash-site.tsv"
A (Test-Path $hashFile) "hash journal written"
if (Test-Path $hashFile) {
    $lines = @(Get-Content $hashFile -Encoding UTF8 | Where-Object { $_ -and $_ -notlike '#*' })
    A ($lines.Count -eq 3) "journal has 3 entries (actual $($lines.Count))"
    A (($lines | Where-Object { $_ -match '^a\.txt\t3\t[0-9A-F]{64}$' }).Count -eq 1) "a.txt entry has size + sha256"
}

"== 2) 归档（哈希一致）=="
@"
[general]
log_dir=logs-arc
state_dir=state-arc

[server]
host = 127.0.0.1
port = 2201
user = u
password = p
tls = off
allow_plaintext = true
manifest_dir = /_foldersyncer

[update]
enabled = false

[job:site]
enabled = true
local = $t\arc
remote = /site
verify_hash = true
"@ | Set-Content "$t\arc.ini" -Encoding UTF8
$p2 = Start-Process -FilePath $exe -ArgumentList @('--config', "$t\arc.ini", '--archive') -Wait -PassThru -WindowStyle Hidden
A ($p2.ExitCode -eq 0) "archive exit 0"
A (Test-Path "$t\arc\a.txt") "a.txt archived"
A (-not (Test-Path "$t\remote\site\a.txt")) "a.txt removed from server"
$lines2 = @(Get-Content $hashFile -Encoding UTF8 | Where-Object { $_ -and $_ -notlike '#*' })
A ($lines2.Count -eq 0) "journal cleared after archive (actual $($lines2.Count))"

"== 3) 哈希不符时拒绝删除服务器文件 =="
[System.IO.File]::WriteAllText("$t\up\a.txt", "AAA-changed", $utf8)
Start-Process -FilePath $exe -ArgumentList @('--config', "$t\up.ini", '--sync') -Wait -WindowStyle Hidden | Out-Null
# 篡改服务器上的哈希清单，模拟传输损坏
$h = Get-Content "$hashFile" -Encoding UTF8
$tampered = $h | ForEach-Object {
    if ($_ -match '^a\.txt\t') { "a.txt`t9`t" + ("0" * 64) } else { $_ }
}
[System.IO.File]::WriteAllText($hashFile, ($tampered -join "`r`n") + "`r`n", $utf8)
$p3 = Start-Process -FilePath $exe -ArgumentList @('--config', "$t\arc.ini", '--archive') -Wait -PassThru -WindowStyle Hidden
A ($p3.ExitCode -ne 0) "archive reports error on hash mismatch (exit $($p3.ExitCode))"
A (Test-Path "$t\remote\site\a.txt") "server file kept when hash mismatches"
$arcLog = Get-Content "$t\logs-arc\sync-$(Get-Date -Format yyyyMMdd).log" -Encoding UTF8 -Raw
A ($arcLog -match '哈希不符') "log explains hash mismatch"

"== 4) 单文件大小上限 =="
Stop-Process -Id $mock.Id -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 300
Remove-Item -Recurse -Force "$t\remote" -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force "$t\remote" | Out-Null
$mock2 = Start-Process -FilePath $mockExe -ArgumentList @("--root=$t\remote", '--port=2202') -PassThru -WindowStyle Hidden
Start-Sleep -Milliseconds 900
Cfg 2202 upbig 1
$p4 = Start-Process -FilePath $exe -ArgumentList @('--config', "$t\upbig.ini", '--sync') -Wait -PassThru -WindowStyle Hidden
A ($p4.ExitCode -eq 0) "sync with size limit exit 0"
A (-not (Test-Path "$t\remote\site\big.bin")) "1MB+1 file skipped when max_file_mb=1"
A (Test-Path "$t\remote\site\a.txt") "small files still uploaded"
$sum = Get-Content "$t\logs-upbig\last-run.txt" -Encoding UTF8 -Raw
A ($sum -match '超限跳过 1 个') "summary reports skipped oversize file"
Stop-Process -Id $mock2.Id -Force -ErrorAction SilentlyContinue
Get-Process MockFtpServer -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

"==== PASS=$pass FAIL=$fail ===="
