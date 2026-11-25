$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent (Split-Path -Parent $here)
$exe = Join-Path $root 'release\upload\FolderSyncer.exe'
$dev = $here
$mockExe = "$dev\MockFtpServer.exe"
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:exe /platform:anycpu /codepage:65001 /out:"$mockExe" /r:System.dll /r:System.Core.dll "$dev\MockFtpServer.cs" | Out-Null
$t = "$root\_3t"
Remove-Item -Recurse -Force $t -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force "$t\upload\子目录", "$t\archive", "$t\remote" | Out-Null
$utf8 = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText("$t\upload\a.txt", "AAA v1", $utf8)
[System.IO.File]::WriteAllText("$t\upload\子目录\b.txt", "BBB v1", $utf8)
$pass = 0; $fail = 0
function A($c, $m) { if ($c) { $script:pass++; "  [PASS] $m" } else { $script:fail++; "  [FAIL] $m" } }

function UpCfg($port) {
@"
[general]
log_dir=logs-up
state_dir=state-up
verbose=true

[server]
host = 127.0.0.1
port = $port
user = u
password = p
tls = off
allow_plaintext = true
manifest_dir = /_foldersyncer

[job:site]
enabled = true
local = $t\upload
remote = /site
mode = upload
"@ | Set-Content "$t\upload.ini" -Encoding UTF8
}
function ArcCfg($port) {
@"
[general]
log_dir=logs-arc
state_dir=state-arc
verbose=true

[server]
host = 127.0.0.1
port = $port
user = u
password = p
tls = off
allow_plaintext = true
manifest_dir = /_foldersyncer

[job:site]
enabled = true
local = $t\archive
remote = /site
"@ | Set-Content "$t\archive.ini" -Encoding UTF8
}
$mock = Start-Process -FilePath $mockExe -ArgumentList @("--root=$t\remote", '--port=2181') -PassThru -WindowStyle Hidden
Start-Sleep -Milliseconds 900
UpCfg 2181
ArcCfg 2181

"== 1 uploader pushes 2 files =="
$p1 = Start-Process -FilePath $exe -ArgumentList @('--config', "$t\upload.ini", '--sync') -Wait -PassThru -WindowStyle Hidden
A ($p1.ExitCode -eq 0) "sync exit 0"
A (Test-Path "$t\remote\site\a.txt") "a.txt on server"
A (Test-Path "$t\remote\site\子目录\b.txt") "b.txt on server"

"== 2 archiver pulls and deletes =="
$p2 = Start-Process -FilePath $exe -ArgumentList @('--config', "$t\archive.ini", '--archive') -Wait -PassThru -WindowStyle Hidden
A ($p2.ExitCode -eq 0) "archive exit 0"
A (Test-Path "$t\archive\a.txt") "a.txt archived locally"
A (Test-Path "$t\archive\子目录\b.txt") "b.txt archived locally (chinese dir)"
A (-not (Test-Path "$t\remote\site\a.txt")) "a.txt removed from server"
A (Test-Path "$t\remote\_foldersyncer\site.tsv") "manifest written on server"
$manifest = Get-Content "$t\remote\_foldersyncer\site.tsv" -Encoding UTF8 -Raw
A ($manifest -match 'a\.txt') "manifest lists a.txt"
A ($manifest -match '子目录/b\.txt') "manifest lists chinese path"

"== 3 uploader sees manifest, does not re-upload =="
$p3 = Start-Process -FilePath $exe -ArgumentList @('--config', "$t\upload.ini", '--sync') -Wait -PassThru -WindowStyle Hidden
$sum = Get-Content "$t\logs-up\last-run.txt" -Encoding UTF8 -Raw
A ($sum -match '上传 0 个') "no re-upload after archive"
A ($sum -match '已归档 2') "reported as archived (2)"
A (-not (Test-Path "$t\remote\site\a.txt")) "server still empty"

"== 4 modify -> upload again =="
[System.IO.File]::WriteAllText("$t\upload\a.txt", "AAA v2 changed", $utf8)
$p4 = Start-Process -FilePath $exe -ArgumentList @('--config', "$t\upload.ini", '--sync') -Wait -PassThru -WindowStyle Hidden
A (Test-Path "$t\remote\site\a.txt") "modified file re-uploaded"
$p5 = Start-Process -FilePath $exe -ArgumentList @('--config', "$t\archive.ini", '--archive') -Wait -PassThru -WindowStyle Hidden
$c1 = Get-Content "$t\archive\a.txt" -Encoding UTF8 -Raw
A ($c1.Trim() -eq 'AAA v2 changed') "archive updated to v2"
$hist = @(Get-ChildItem -Recurse "$t\archive\_history" -File -ErrorAction SilentlyContinue)
A ($hist.Count -eq 1) "old version moved to _history ($($hist.Count))"
if ($hist.Count -ge 1) { A ($hist[0].FullName -match '\\_history\\\d{8}-\d{6}\\a\.txt$') "_history path keeps structure: $($hist[0].FullName)" }

"== 5 uploader skips v2 after archive =="
$p6 = Start-Process -FilePath $exe -ArgumentList @('--config', "$t\upload.ini", '--sync') -Wait -PassThru -WindowStyle Hidden
$sum2 = Get-Content "$t\logs-up\last-run.txt" -Encoding UTF8 -Raw
A ($sum2 -match '上传 0 个') "v2 not re-uploaded"

"== 6 archiver dry-run does nothing =="
[System.IO.File]::WriteAllText("$t\upload\子目录\b.txt", "BBB v2", $utf8)
Start-Process -FilePath $exe -ArgumentList @('--config', "$t\upload.ini", '--sync') -Wait -WindowStyle Hidden | Out-Null
$p7 = Start-Process -FilePath $exe -ArgumentList @('--config', "$t\archive.ini", '--archive', '--dry-run') -Wait -PassThru -WindowStyle Hidden
A (Test-Path "$t\remote\site\子目录\b.txt") "dry-run keeps file on server"
A ((Get-Content "$t\archive\子目录\b.txt" -Encoding UTF8 -Raw).Trim() -eq 'BBB v1') "dry-run does not overwrite local"

Stop-Process -Id $mock.Id -Force -ErrorAction SilentlyContinue
Get-Process MockFtpServer -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
"==== PASS=$pass FAIL=$fail ===="
