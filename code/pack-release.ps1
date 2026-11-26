param(
    [string]$Version = "1.0.0"
)

# ============================================================
#  打包上传到 GitHub Releases 用的 zip
#
#  产物（都在 dist\v<版本>\ 下）：
#    folder-syncer-v<版本>.zip            完整包：上传端 + 保存端
#    folder-syncer-upload-v<版本>.zip     只有上传端
#    folder-syncer-archive-v<版本>.zip    只有保存端
# ============================================================

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $here
$dist = Join-Path $root ('dist\v' + $Version)

if (-not (Test-Path (Join-Path $root 'release\upload\FolderSyncer.exe'))) {
    Write-Host "release 里还没有 FolderSyncer.exe，先跑 code\build.bat" -ForegroundColor Yellow
    exit 1
}

if (-not (Test-Path $dist)) { New-Item -ItemType Directory -Force $dist | Out-Null }

function Remove-PathWithRetry {
    param([string]$Path)
    for ($i = 1; $i -le 5; $i++) {
        try {
            if (Test-Path $Path) { Remove-Item -Recurse -Force $Path -ErrorAction Stop }
            return $true
        } catch {
            Start-Sleep -Milliseconds 800
        }
    }
    return $false
}

function Test-ZipOk {
    param([string]$Path)
    try {
        if (-not (Test-Path $Path)) { return $false }
        if ((Get-Item $Path).Length -lt 1024) { return $false }
        Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction SilentlyContinue
        $z = [System.IO.Compression.ZipFile]::OpenRead($Path)
        $n = $z.Entries.Count
        $z.Dispose()
        return ($n -gt 0)
    } catch {
        return $false
    }
}

function New-Package {
    param(
        [string]$ZipName,
        [string[]]$Parts
    )
    $stage = Join-Path $dist ("_stage-" + $ZipName)
    Remove-PathWithRetry $stage | Out-Null
    foreach ($part in $Parts) {
        $src = Join-Path $root ("release\" + $part)
        $dst = Join-Path $stage ("folder-syncer\" + $part)
        New-Item -ItemType Directory -Force $dst | Out-Null
        Get-ChildItem $src -File | Where-Object {
            $_.Name -notlike 'logs*' -and $_.Name -ne 'state'
        } | ForEach-Object {
            for ($i = 1; $i -le 5; $i++) {
                try { Copy-Item $_.FullName $dst -Force -ErrorAction Stop; break }
                catch { Start-Sleep -Milliseconds 800 }
            }
        }
    }
    $zip = Join-Path $dist $ZipName
    $ok = $false
    for ($i = 1; $i -le 5 -and -not $ok; $i++) {
        try {
            if (Test-Path $zip) { Remove-Item $zip -Force -ErrorAction Stop }
            Compress-Archive -Path (Join-Path $stage 'folder-syncer') -DestinationPath $zip -CompressionLevel Optimal -Force -ErrorAction Stop
            $ok = Test-ZipOk $zip
        } catch {
            $ok = $false
        }
        if (-not $ok) { Start-Sleep -Milliseconds 1000 }
    }
    if ($ok) {
        Write-Host ("[OK] " + $zip) -ForegroundColor Green
    } else {
        Write-Host ("[FAIL] " + $zip + "（重试 5 次仍失败，可能被杀软占用，稍后再跑一次）") -ForegroundColor Red
    }
    Remove-PathWithRetry $stage | Out-Null
}

New-Package -ZipName ("folder-syncer-v" + $Version + ".zip") -Parts @('upload', 'archive')
New-Package -ZipName ("folder-syncer-upload-v" + $Version + ".zip") -Parts @('upload')
New-Package -ZipName ("folder-syncer-archive-v" + $Version + ".zip") -Parts @('archive')

Write-Host ""
Write-Host ("三个包都在 " + $dist + "，可直接作为 GitHub Releases 的附件。")
Write-Host "单独发上传端就传 upload 包，单独发保存端就传 archive 包，新人拿到完整包更省事。"
