<div align="center">

# 🔁 Folder Syncer

[English](https://github.com/Bright9025/folder-syncer/blob/main/README.en.md) | [简体中文](https://github.com/Bright9025/folder-syncer/blob/main/README.md)

A silent, popup-free Windows directory sync tool: push folders to your own server over **FTPS**, let an always-on PC pull them back into a local archive, and keep nothing but a manifest on the server.

[![Windows](https://img.shields.io/badge/Windows-10%20%2F%2011-0078d4?logo=windows&logoColor=white)](https://www.microsoft.com/windows)
[![.NET Framework](https://img.shields.io/badge/.NET%20Framework-%E2%89%A54.5-512bd4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com)
[![License: MIT](https://img.shields.io/badge/License-MIT-22b394.svg)](https://github.com/Bright9025/folder-syncer/blob/main/LICENSE)
[![Conventional Commits](https://img.shields.io/badge/Conventional%20Commits-1.0.0-%23FE5196?logo=conventionalcommits&logoColor=white)](https://conventionalcommits.org)

> No installer · No third-party dependencies · No windows · No tray icon · No dialogs

> Current version: **v1.0.0** · [GitHub Releases](https://github.com/Bright9025/folder-syncer/releases)

</div>

## ✨ Features

- 🔐 **FTPS everywhere** – Explicit TLS plus SHA256 certificate pinning. If the server is not encrypted, the agent only writes a log line: no login, no password sent, no sync.
- 🪶 **One dependency-free binary** – Built with the bundled .NET Framework compiler; a single 62 KB exe, no Python / Node / SSH client, no long-running admin process.
- 📤 **Uploader (`--sync`)** – Incremental push of local folders to the server. Only new or changed files are transferred; uploads write a temp file and rename, so an interrupted transfer never corrupts the previous version.
- 📥 **Archiver (`--archive`)** – Pulls files back into a local archive, verifies size and content, and only then deletes the server copy. Changed files are moved into `_history\` before being replaced.
- 🧾 **Archived manifest** – The archiver keeps `_foldersyncer\<folder>.tsv` on the server. The uploader reads it and skips anything already archived, so deleting server copies never triggers a re-upload.
- 🗂️ **Server as a relay** – Files disappear from the server right after being archived; only a few KB of manifest stay. Perfect for tiny servers.
- 🧩 **Multiple jobs** – Each `[job:name]` section is one "local folder → server folder" mapping.
- 🚫 **Never deletes files it did not upload** – Only files this tool uploaded and that were later deleted locally are cleaned up.
- 🕒 **Unattended** – Ships task-registration scripts: logon trigger plus daily times or "every N minutes", running with `--sync` or `--archive` and completely silent.
- 📝 **Files only, no interruptions** – Results go to `logs\last-run.txt`, `logs\archive-report.txt` and daily logs. There is no window, tray icon or MessageBox in the program.

## 🧭 Three-tier design

```text
 Uploader (target PC)          Server (small disk)                Archiver (always-on)
┌─────────────────┐  FTPS   ┌──────────────────────┐   FTPS   ┌───────────────────┐
│ D:\your-folder  │ ──────▶ │ /your-folder         │ ───────▶ │ D:\your-archive   │
│ state\ (delta)  │         │ /_foldersyncer/*.tsv    │          │ _history\ (older) │
└─────────────────┘         │ (manifest only, KBs) │          └───────────────────┘
        ▲                   └──────────────────────┘
        └──────────── reads manifest, skips archived files ────────────┘
```

1. The **uploader** compares its local `state\` and uploads new or modified files.
2. The **archiver** runs every N minutes (default 30), downloads everything, verifies it, deletes the server copy and records the path in the manifest.
3. On its next run the **uploader** reads the manifest: unchanged files that are already archived are skipped, even though the server no longer has them.

## 📁 Project layout

```text
folder-syncer/
├── code/
│   ├── src/                    # Main sources (C# 5, works on .NET Framework 4.5+)
│   │   ├── Program.cs          # Entry point: --sync / --archive / --test / --install-task
│   │   ├── SyncEngine.cs       # Uploader: delta check, overwrite upload, trash, manifest skip
│   │   ├── ArchiveEngine.cs    # Archiver: download, verify, delete server copy, update manifest
│   │   ├── FtpClient.cs        # Hand-written FTP/FTPS client (AUTH TLS, PBSZ/PROT, MLSD, MFMT)
│   │   ├── Manifest.cs         # Archived-manifest reader/writer
│   │   ├── RemoteTree.cs       # Recursive remote listing (skips _trash / _foldersyncer)
│   │   ├── TaskInstaller.cs    # Scheduled-task registration (schtasks + XML)
│   │   ├── Config.cs           # INI parser
│   │   ├── Logger.cs           # Logs and reports
│   │   └── Util.cs             # Paths, globs, encodings
│   ├── devtools/               # Self-test: mock FTP server, cleanup tool, test scripts
│   ├── build.bat               # One-click build into release\upload and release\archive
│   └── pack-release.ps1        # Build the three release zips (all / uploader / archiver)
├── release/
│   ├── upload/                 # Uploader package: exe + config.ini + .bat files
│   └── archive/                # Archiver package: exe + archive.ini + .bat files
├── README.md
├── README.en.md
├── CONTRIBUTING.md
├── LICENSE
└── .gitignore
```

- **One exe, two roles** – Selected by `--sync` / `--archive` plus separate config files; both folders are self-contained.
- **No build system** – `code\build.bat` calls the Windows-bundled `csc.exe`; no Visual Studio or MSBuild required.

## 🚀 Quick start

### Requirements

- Windows 10 / 11 (the bundled .NET Framework 4.5+ is enough)
- An FTP server with **FTPS (explicit TLS)** enabled (FileZilla Server, IIS FTP, etc.)
- No admin rights, no background service

### Uploader (target PC)

1. Copy `release\upload\` to the target machine (put it on drive D if the system drive gets wiped). The exe ships with the repo; a ready-made zip is also attached to [Releases](https://github.com/Bright9025/folder-syncer/releases).
2. Open `config.ini` and fill in server, credentials and folders:

```ini
[server]
host = your-server-or-domain
user = your-ftp-user
password = your-ftp-password

[job:your-job]
enabled = true
local = D:\your-folder
remote = /your-folder
mode = upload
```

3. Run `test-connection.bat` and confirm "FTPS" plus a reachable target folder.
4. Run `install-task.bat` to register the scheduled task (30 s after logon plus daily times).

### Archiver (always-on PC)

1. Copy `release\archive\` to the always-on machine.
2. Open `archive.ini`, keep `[server]` identical to the uploader and set the archive folder:

```ini
[schedule]
mode = archive
repeat_minutes = 30

[job:your-job]
enabled = true
local = D:\your-archive
remote = /your-folder
```

3. Run `archive-now.bat` once to see the report (`--dry-run` to preview without changes).
4. Run `archive-install-task.bat` to register the archive task.

## 🔐 FTPS

The agent only speaks **explicit FTPS** (`AUTH TLS` → `PBSZ` / `PROT P`, still on port 21). Implicit FTPS on port 990 is not used.

**Default policy: no encryption, no sync.** With `allow_plaintext = false`, if the server has no TLS the agent writes one WARN line (`logs\last-run.txt` says it did nothing), does not log in, does not send the password and does not touch any file. Exit code 0.

### Enable it on the server

**FileZilla Server**

- 0.9.x: `Edit → Settings → SSL/TLS settings`, enable *Enable SSL/TLS support* and *Allow explicit FTP over TLS*, then *Generate new certificate*
- 1.x: `Server → TLS settings`, enable *Enable explicit FTP over TLS* and pick or create a certificate
- Recommended: also force TLS so plaintext connections are refused outright

**IIS FTP**

- IIS Manager → site → *FTP SSL Settings* → choose a certificate → allow (or require) SSL connections

### Configure the client

1. `tls = auto` encrypts whenever the server supports it; `require` turns "no TLS" into an error
2. Self-signed certificate: set `tls_verify = false` first and run `test-connection.bat`
3. The report prints the certificate SHA256; put those 64 hex characters into `tls_fingerprint` and set `tls_verify = true` again — that pins the certificate and is much safer than leaving validation off
4. With a CA-issued certificate just keep `tls_verify = true` and leave the fingerprint empty

### Troubleshooting

| Symptom | Cause / fix |
| --- | --- |
| Log says "server is not encrypted" (AUTH TLS returns 5xx) | FTPS is not enabled on the server yet |
| "Certificate validation failed" plus a fingerprint | Self-signed: pin the fingerprint or temporarily set `tls_verify = false` |
| Login fails with `530` | Wrong user name or password |
| Login works but transfers time out | Passive-mode data ports are blocked; open the PASV range in the firewall |
| Want to force encryption | Set `tls = require` |

> Use `allow_plaintext = true` for LAN or test environments only; the agent logs a warning every time.
> `logs\test-report.txt` (written by `test-connection.bat`) shows the server banner, `FEAT` and whether TLS is active — check it first when something fails.
## ⚙️ Configuration

### `[server]`

| Key | Meaning |
| --- | --- |
| `host` / `port` | Server address and port (default 21) |
| `user` / `password` | FTP credentials |
| `tls` | `auto` (encrypt when possible) / `require` / `off` |
| `tls_verify` | Validate the server certificate; set `false` for self-signed |
| `tls_fingerprint` | Certificate SHA256 (64 hex chars, no colons); when set, the cert is pinned |
| `remote_root` | FTP account root, usually `/` |
| `allow_plaintext` | Default `false`: unencrypted server means log only, no login, no sync |
| `manifest_dir` | Manifest folder, default `/_foldersyncer` (must match on both sides) |
| `timeout_sec` | Network timeout |

### `[schedule]`

| Key | Meaning |
| --- | --- |
| `mode` | `sync` (uploader) / `archive` (archiver) — picks the task argument |
| `on_logon` | Run 30 seconds after logon |
| `daily_times` | Daily times, e.g. `09:00, 18:00` |
| `repeat_minutes` | Repeat every N minutes (0 = never) |
| `task_name` | Scheduled task name |

### `[job:name]`

| Key | Meaning |
| --- | --- |
| `enabled` | Enable the job |
| `local` | Local folder (source on the uploader, archive root on the archiver) |
| `remote` | Server folder (relative to `remote_root`, created automatically) |
| `mode` | `mirror` (deletions go to trash) / `upload` (upload only) |
| `strict_mirror` | Also clean up server-only files, default `false` |
| `delete_to_trash` / `trash_dir` / `trash_keep_days` | Trash behaviour |
| `exclude` / `include` | Semicolon-separated glob rules |
| `follow_links` | Follow symlinks/junctions, default `false` |

## 📜 Archived manifest

The archiver maintains `_foldersyncer\<remote-folder>.tsv` on the server:

```text
# FolderSyncer manifest v1
a.txt	14	639312164783098361
sub/b.txt	6	639312164783098361
```

- Only the archiver writes it; the uploader just reads it.
- Uploader rule: unchanged locally **and** missing on the server **and** present in the manifest → skip (already archived).
- Deleting the manifest loses no data; the uploader would just re-send files that are missing on the server.

## 🛠️ Command line

| Argument | Purpose |
| --- | --- |
| none / `--sync` | Run one silent sync (uploader) |
| `--archive` | Run one archive pass (pull, delete server copy, update manifest) |
| `--test` | Connection test → `logs\test-report.txt` |
| `--install-task` / `--uninstall-task` | Register / remove the scheduled task → `logs\install-report.txt` |
| `--dry-run` | Preview only: nothing is uploaded, deleted or downloaded |
| `--verbose` | Log every FTP command |
| `--config <path>` | Use a specific config file |
| `--help` | Write usage to `logs\help.txt` |

## 🧪 Development and self-tests

```bat
code\build.bat                     :: build into release\upload and release\archive
code\devtools\run-tests.ps1        :: uploader tests (MLSD/MFMT/fallback/unencrypted skip)
code\devtools\run-tests-3tier.ps1  :: three-tier tests (upload -> archive -> no re-upload)
powershell -File code\pack-release.ps1 -Version 1.0.0   :: three release zips -> dist\v1.0.0\
```

`code\devtools\MockFtpServer.cs` is a minimal FTP server (MLSD, MFMT, optional TLS). The test scripts build and launch it locally, so nothing touches a real server.

## ⚠️ Limitations

- The uploader is one-way local → server; the archiver is one-way server → local (plus deletion).
- No content hashing on the upload side: it uses size, modification time and the manifest. The archiver verifies size and content after download.
- No resume support: an interrupted large file is transferred again (with retries).
- If the server does not support `MFMT`, server-side modification times become upload times (delta logic still works).
- While the archiver is offline, files pile up on the server; run `archive-now.bat` manually when space is tight.
- Chinese and spaces in file names are supported; directory depth is best kept under 64 levels.

## 🤝 Contributing

Issues and pull requests are welcome. Please read [CONTRIBUTING.md](./CONTRIBUTING.md) first.

## 📄 License

Released under the [MIT License](./LICENSE).
