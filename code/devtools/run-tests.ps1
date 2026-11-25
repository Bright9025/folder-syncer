$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent (Split-Path -Parent $here)
$exe = Join-Path $root 'release\upload\SyncAgent.exe'
$dev = $here
$mockExe = "$dev\MockFtpServer.exe"
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:exe /platform:anycpu /codepage:65001 /out:"$mockExe" /r:System.dll /r:System.Core.dll "$dev\MockFtpServer.cs" | Out-Null
$t = "$root\_tests"
Remove-Item -Recurse -Force $t -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force "$t\local\子目录", "$t\remote" | Out-Null
$utf8 = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText("$t\local\a.txt", "hello", $utf8)
[System.IO.File]::WriteAllText("$t\local\子目录\b.txt", "world", $utf8)
$localMtime = (Get-Item "$t\local\a.txt").LastWriteTimeUtc
$pass = 0; $fail = 0
function A($c, $m) { if ($c) { $script:pass++; "  [PASS] $m" } else { $script:fail++; "  [FAIL] $m" } }
function Cfg($port, $name) {
    $cfg = @"
[general]
log_dir=logs
state_dir=state
verbose=true

[server]
host = 127.0.0.1
port = $port
user = u
password = p
tls = off
allow_plaintext = true

[job:j]
enabled = true
local = $t\local
remote = /site
"@
    $p = "$t\$name.ini"
    [System.IO.File]::WriteAllText($p, $cfg, $utf8)
    return $p
}
function ReadLog { Get-Content "$t\logs\sync-$(Get-Date -Format yyyyMMdd).log" -Encoding UTF8 -Raw }

"== A: MLSD + MFMT =="
$m1 = Start-Process -FilePath $mockExe -ArgumentList @("--root=$t\remote", '--port=2181') -PassThru -WindowStyle Hidden
Start-Sleep -Milliseconds 800
$c1 = Cfg 2181 cfgA
$p1 = Start-Process -FilePath $exe -ArgumentList @('--config', $c1, '--sync') -Wait -PassThru -WindowStyle Hidden
A ($p1.ExitCode -eq 0) "sync exit 0 (actual $($p1.ExitCode))"
A (Test-Path "$t\remote\site\a.txt") "a.txt uploaded"
A (Test-Path "$t\remote\site\子目录\b.txt") "chinese subdir uploaded"
$log1 = ReadLog
A ($log1 -match 'MLSD') "MLSD advertised and used"
A ($log1 -match 'MFMT') "MFMT used"
$rm = (Get-Item "$t\remote\site\a.txt").LastWriteTimeUtc
A ([math]::Abs(($rm - $localMtime).TotalSeconds) -lt 2) "mtime preserved ($rm vs $localMtime)"

"== B: second run skips =="
$p2 = Start-Process -FilePath $exe -ArgumentList @('--config', $c1, '--sync') -Wait -PassThru -WindowStyle Hidden
$sum = Get-Content "$t\logs\last-run.txt" -Encoding UTF8 -Raw
A ($sum -match '跳过 2') "second run skipped 2 files"

"== C: no MLSD -> LIST fallback =="
Stop-Process -Id $m1.Id -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 400
$m2 = Start-Process -FilePath $mockExe -ArgumentList @("--root=$t\remote", '--port=2182', '--no-mlsd') -PassThru -WindowStyle Hidden
Start-Sleep -Milliseconds 800
$c2 = Cfg 2182 cfgB
[System.IO.File]::WriteAllText("$t\local\c.txt", "list fallback", $utf8)
$p3 = Start-Process -FilePath $exe -ArgumentList @('--config', $c2, '--sync') -Wait -PassThru -WindowStyle Hidden
A ($p3.ExitCode -eq 0) "fallback sync exit 0 (actual $($p3.ExitCode))"
A (Test-Path "$t\remote\site\c.txt") "c.txt uploaded via LIST fallback"
Stop-Process -Id $m2.Id -Force -ErrorAction SilentlyContinue

"== D: unencrypted -> do nothing =="
New-Item -ItemType Directory -Force "$t\remote2" | Out-Null
$m3 = Start-Process -FilePath $mockExe -ArgumentList @("--root=$t\remote2", '--port=2183') -PassThru -WindowStyle Hidden
Start-Sleep -Milliseconds 800
$cfgNo = @"
[server]
host = 127.0.0.1
port = 2183
user = u
password = p
tls = auto
allow_plaintext = false

[job:j]
local = $t\local
remote = /site
"@
$c3 = "$t\cfgD.ini"
[System.IO.File]::WriteAllText($c3, $cfgNo, $utf8)
$p4 = Start-Process -FilePath $exe -ArgumentList @('--config', $c3, '--sync') -Wait -PassThru -WindowStyle Hidden
A ($p4.ExitCode -eq 0) "no-tls run exit 0, no error (actual $($p4.ExitCode))"
A (-not (Test-Path "$t\remote2\site")) "nothing uploaded when unencrypted"
$sum2 = Get-Content "$t\logs\last-run.txt" -Encoding UTF8 -Raw
A ($sum2 -match '未执行：服务器未启用加密') "last-run says not executed"
A ((ReadLog) -match '未执行：服务器未启用加密') "log has the WARN"

"== E: --test writes report, exits by itself =="
$m4 = Start-Process -FilePath $mockExe -ArgumentList @("--root=$t\remote", '--port=2181') -PassThru -WindowStyle Hidden
Start-Sleep -Milliseconds 900
$sw = [Diagnostics.Stopwatch]::StartNew()
$p5 = Start-Process -FilePath $exe -ArgumentList @('--config', $c1, '--test') -Wait -PassThru -WindowStyle Hidden
A ($sw.Elapsed.TotalSeconds -lt 20) "test returned in $([int]$sw.Elapsed.TotalSeconds)s (no modal dialog)"
A (Test-Path "$t\logs\test-report.txt") "test-report.txt written"
$rep = Get-Content "$t\logs\test-report.txt" -Encoding UTF8 -Raw
A ($rep -match 'MLSD') "report names MLSD"
Stop-Process -Id $m3.Id -Force -ErrorAction SilentlyContinue
Stop-Process -Id $m4.Id -Force -ErrorAction SilentlyContinue
Get-Process MockFtpServer -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

"==== PASS=$pass FAIL=$fail ===="
