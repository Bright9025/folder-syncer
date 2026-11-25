<div align="center">

# 🔁 Dir Sync Agent

[English](https://github.com/Bright9025/dir-sync-agent/blob/main/README.en.md) | [简体中文](https://github.com/Bright9025/dir-sync-agent/blob/main/README.md)

一个纯后台、永不弹窗的 Windows 目录同步工具：把指定目录经 **FTPS** 推到自建服务器，再由一台常开的电脑定时拉回本地归档，服务器上只保留清单和中转文件。

[![Windows](https://img.shields.io/badge/Windows-10%20%2F%2011-0078d4?logo=windows&logoColor=white)](https://www.microsoft.com/windows)
[![.NET Framework](https://img.shields.io/badge/.NET%20Framework-%E2%89%A54.5-512bd4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com)
[![License: MIT](https://img.shields.io/badge/License-MIT-22b394.svg)](https://github.com/Bright9025/dir-sync-agent/blob/main/LICENSE)
[![Conventional Commits](https://img.shields.io/badge/Conventional%20Commits-1.0.0-%23FE5196?logo=conventionalcommits&logoColor=white)](https://conventionalcommits.org)

> 无安装 · 无第三方依赖 · 无窗口 · 无托盘 · 无弹窗

</div>

## ✨ 功能特点

- 🔐 **全程 FTPS** – 显式 TLS + 证书 SHA256 指纹锁定；服务器没开加密时只写日志、不登录、不发送密码、不同步。
- 🪶 **零依赖单文件** – 用 Windows 自带的 .NET Framework 编译，62 KB 一个 exe，不需要 Python / Node / SSH 客户端，也不需要管理员权限常驻。
- 📤 **上传端（`--sync`）** – 把本地目录增量推到服务器；只传新增和变化的文件，上传用「临时文件 + 改名」，中断不会写坏服务器上的旧版本。
- 📥 **保存端（`--archive`）** – 把服务器文件拉回本地存档，**校验大小和内容后**才删除服务器副本，同名文件内容变化时旧版本自动进 `_history\`。
- 🧾 **已归档清单** – 保存端在服务器上维护一份 `_syncagent\<目录>.tsv`，记下哪些文件已经安全落地；上传端读到清单就跳过，**服务器删了也不会重传**。
- 🗂️ **服务器只当中转站** – 文件被拉走后立刻删除，服务器上长期只有几十 KB 的清单，适合空间很小的小服务器。
- 🧩 **多任务** – 一个 `[job:名字]` 就是一组「本地目录 → 服务器目录」，可以配任意组。
- 🚫 **默认不删别人的文件** – 只清理自己上传过、后来在本地被删掉的文件；服务器上原有文件默认不动。
- 🕒 **自动运行** – 自带计划任务注册脚本：登录后触发 + 每天定时 / 每 N 分钟，命令行带 `--sync` 或 `--archive`，完全静默。
- 📝 **只写文件不打扰** – 所有结果写到 `logs\last-run.txt`、`logs\archive-report.txt` 和按天切分的日志；程序里没有任何窗口、托盘图标或 MessageBox。

## 🧭 三端架构

```text
 上传端（目标 PC）              服务器（空间很小）                保存端（常开机）
┌────────────────┐   FTPS   ┌──────────────────────┐   FTPS   ┌──────────────────┐
│ D:\课件         │ ───────▶ │ /课件（中转文件）      │ ───────▶ │ D:\存档\课件       │
│ state\（增量）  │          │ /_syncagent/*.tsv    │          │ _history\（旧版）  │
└────────────────┘          │ （只留清单，几十 KB）  │          └──────────────────┘
        ▲                   └──────────────────────┘
        └──────── 读清单，已归档的文件不再重传 ────────┘
```

1. **上传端**运行时比对本地 `state\`，把新增/修改的文件传到服务器。
2. **保存端**每 30 分钟（可配）把服务器文件下载到本地存档，校验通过后删除服务器副本，并把路径写进清单。
3. **上传端**下次运行先读清单：清单里有、本地又没改过的文件直接跳过——即使服务器上已经没有它。

## 📁 项目结构

```text
dir-sync-agent/
├── code/
│   ├── src/                    # 主程序源码（C#，C# 5 语法，兼容 .NET Framework 4.5+）
│   │   ├── Program.cs          # 入口：命令行、--sync / --archive / --test / --install-task
│   │   ├── SyncEngine.cs       # 上传端：增量比对、覆盖上传、回收站、清单跳过
│   │   ├── ArchiveEngine.cs    # 保存端：下载、校验、删除服务器副本、写清单
│   │   ├── FtpClient.cs        # 自研 FTP/FTPS 客户端（AUTH TLS、PBSZ/PROT、MLSD、MFMT）
│   │   ├── Manifest.cs         # 已归档清单的读写
│   │   ├── RemoteTree.cs       # 远端目录递归列举（跳过 _trash / _syncagent）
│   │   ├── TaskInstaller.cs    # 计划任务注册（schtasks + XML）
│   │   ├── Config.cs           # INI 解析
│   │   ├── Logger.cs           # 日志与报告
│   │   └── Util.cs             # 路径、glob、编码等工具
│   ├── devtools/               # 开发自测：模拟 FTP 服务器、清理工具、测试脚本
│   └── build.bat               # 一键编译到 release\upload 和 release\archive
├── release/
│   ├── upload/                 # 上传端（目标 PC）：exe + config.ini + bat
│   └── archive/                # 保存端（常开机）：exe + archive.ini + bat
├── README.md
├── README.en.md
├── CONTRIBUTING.md
├── LICENSE
└── .gitignore
```

- **一份 exe 两个角色**：靠 `--sync` / `--archive` 和各自的配置文件区分，两个目录完全自包含。
- **没有构建系统**：`code\build.bat` 直接用 Windows 自带的 `csc.exe` 编译，不需要 Visual Studio / MSBuild。

## 🚀 快速开始

### 环境要求

- Windows 10 / 11（自带 .NET Framework 4.5+ 即可，无需额外安装）
- 一台支持 **FTPS（显式 TLS）** 的 FTP 服务器（FileZilla Server、IIS FTP 等都可以）
- 不需要管理员权限，不需要开机常驻进程

### 上传端（目标 PC）

1. 把 `release\upload\` 整个目录拷到目标机（建议放 D 盘，C 盘重置也不影响）。
2. 复制 `config.example.ini` 为 `config.ini`，填写服务器地址、账号密码和要同步的目录：

```ini
[server]
host = 你的服务器IP或域名
user = 你的FTP账号
password = 你的FTP密码

[job:课件]
enabled = true
local = D:\课件
remote = /课件
mode = upload
```

3. 双击 `test-connection.bat` 确认「加密：FTPS（TLS 已启用）」且目标目录可达。
4. 双击 `install-task.bat` 注册计划任务（登录后 30 秒 + 每天定时）。

### 保存端（常开机）

1. 把 `release\archive\` 拷到那台常开的电脑。
2. 复制 `archive.example.ini` 为 `archive.ini`，`[server]` 与上传端保持一致，改好存档目录：

```ini
[schedule]
mode = archive
repeat_minutes = 30

[job:课件]
enabled = true
local = D:\存档\课件
remote = /课件
```

3. 双击 `archive-now.bat` 先手动跑一次看报告（加 `--dry-run` 只看不动）。
4. 双击 `archive-install-task.bat` 注册归档任务。

## ⚙️ 配置说明

### `[server]`

| 键 | 说明 |
| --- | --- |
| `host` / `port` | 服务器地址与端口，默认 21 |
| `user` / `password` | FTP 账号 |
| `tls` | `auto`（能加密就加密）/ `require`（必须）/ `off` |
| `tls_verify` | 校验服务器证书；自签证书先设 `false` |
| `tls_fingerprint` | 证书 SHA256 指纹（64 位十六进制，不带冒号），填了就按指纹锁定 |
| `remote_root` | FTP 账号根目录，一般 `/` |
| `allow_plaintext` | 默认 `false`：服务器没加密时只写日志、不登录、不同步 |
| `manifest_dir` | 清单目录，默认 `/_syncagent`，两端保持一致 |
| `timeout_sec` | 网络超时 |

### `[schedule]`

| 键 | 说明 |
| --- | --- |
| `mode` | `sync`（上传端）/ `archive`（保存端），决定计划任务跑哪个模式 |
| `on_logon` | 登录后 30 秒跑一次 |
| `daily_times` | 每天定时，如 `09:00, 18:00` |
| `repeat_minutes` | 每隔 N 分钟跑一次（0 = 不重复） |
| `task_name` | 计划任务名字 |

### `[job:任务名]`

| 键 | 说明 |
| --- | --- |
| `enabled` | 是否启用 |
| `local` | 本地目录（上传端＝要同步的目录；保存端＝存档目录） |
| `remote` | 服务器目录（相对 `remote_root`，不存在会自动创建） |
| `mode` | `mirror`（删除进回收站）/ `upload`（只上传） |
| `strict_mirror` | 是否连服务器上多余的也清掉，默认 `false` |
| `delete_to_trash` / `trash_dir` / `trash_keep_days` | 回收站开关、目录、保留天数 |
| `exclude` / `include` | 排除 / 包含规则，分号分隔 |
| `follow_links` | 是否跟随符号链接与目录联接，默认 `false` |

## 📜 已归档清单

保存端在服务器上维护 `_syncagent\<远端目录名>.tsv`：

```text
# SyncAgent manifest v1
a.txt	14	639312164783098361
子目录/b.txt	6	639312164783098361
```

- 只有保存端会写它；上传端只读。
- 上传端规则：本地没变 **且** 服务器上没有 **且** 清单里有 → 跳过（已被归档）。
- 删掉清单不会丢数据，只会让上传端在服务器缺失时重传一次。

## 🛠️ 命令行

| 参数 | 作用 |
| --- | --- |
| 无参数 / `--sync` | 静默同步一次（上传端） |
| `--archive` | 归档一次（保存端：拉取 + 删除服务器副本 + 更新清单） |
| `--test` | 连接测试 → `logs\test-report.txt` |
| `--install-task` / `--uninstall-task` | 注册 / 删除计划任务 → `logs\install-report.txt` |
| `--dry-run` | 演练：只写「将要做什么」，不传不删不下载 |
| `--verbose` | 日志记录每一条 FTP 命令 |
| `--config <路径>` | 指定配置文件 |
| `--help` | 参数说明写到 `logs\help.txt` |

## 🧪 开发与自测

```bat
code\build.bat                 :: 编译到 release\upload 和 release\archive
code\devtools\run-tests.ps1        :: 上传端自测（MLSD/MFMT/回退/未加密跳过）
code\devtools\run-tests-3tier.ps1  :: 三端自测（上传 -> 归档 -> 不重传）
```

`code\devtools\MockFtpServer.cs` 是一个最小 FTP 服务器（支持 MLSD/MFMT/可选 TLS），测试脚本会自动编译并启动它，全部在本机完成，不会碰真实服务器。

## ⚠️ 边界

- 上传端只做本机 → 服务器单向；保存端只做服务器 → 本机单向加删除。
- 不做内容哈希比对：上传端靠「大小 + 修改时间 + 清单」，保存端下载后做大小校验与内容比对。
- 不做断点续传，大文件中途断了会整个重传（有重试）。
- 服务器不支持 `MFMT` 时，服务器端文件修改时间会变成上传时间（不影响增量判断）。
- 保存端没开机时文件会堆在服务器上，空间紧张时手动跑一次 `archive-now.bat`。
- 支持中文、空格文件名；目录深度建议不超过 64 层。

## 🤝 贡献

欢迎提交 Issue 或 Pull Request。请先阅读 [CONTRIBUTING.md](./CONTRIBUTING.md)。

## 📄 许可证

本项目采用 [MIT License](./LICENSE) 开源，可自由使用、修改和分发。
