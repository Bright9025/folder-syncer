param(
    [string]$Version = "1.0.0"
)

# ============================================================
#  打包上传到 GitHub Releases 用的 zip
#  产物：dist\folder-syncer-v<Version>.zip
#  里面是 release\upload\ 和 release\archive\ 两个自包含目录
# ============================================================

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $here
$dist = Join-Path $root 'dist'
$stage = Join-Path $dist ("_stage-v" + $Version)

if (-not (Test-Path (Join-Path $root 'release\upload\FolderSyncer.exe'))) {
    Write-Host "release 里还没有 exe，先跑 code\build.bat" -ForegroundColor Yellow
    exit 1
}

if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Force $stage | Out-Null

foreach ($name in 'upload', 'archive') {
    $src = Join-Path $root ("release\" + $name)
    $dst = Join-Path $stage ("folder-syncer\" + $name)
    New-Item -ItemType Directory -Force $dst | Out-Null
    Get-ChildItem $src -File | Where-Object {
        $_.Name -notlike 'logs*' -and $_.Name -ne 'state'
    } | ForEach-Object { Copy-Item $_.FullName $dst -Force }
}

$zip = Join-Path $dist ("folder-syncer-v" + $Version + ".zip")
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $stage 'folder-syncer') -DestinationPath $zip -Force
Remove-Item -Recurse -Force $stage

Write-Host ("[OK] " + $zip) -ForegroundColor Green
Write-Host "把 release\upload 或 release\archive 整个目录交给使用者即可；zip 可直接上传到 GitHub Releases。"
