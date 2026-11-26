<div align="center">

# 🔁 Folder Syncer

[English](https://github.com/Bright9025/folder-syncer/blob/main/README.en.md) | [简体中文](https://github.com/Bright9025/folder-syncer/blob/main/README.md)

一个纯后台、永不弹窗的 Windows 目录同步工具：把指定目录经 **FTPS** 推到自建服务器，再由一台常开的电脑定时拉回本地归档，服务器上只保留清单和中转文件。

[![Windows](https://img.shields.io/badge/Windows-10%20%2F%2011-0078d4?logo=windows&logoColor=white)](https://www.microsoft.com/windows)
[![.NET Framework](https://img.shields.io/badge/.NET%20Framework-%E2%89%A54.5-512bd4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com)
[![License: MIT](https://img.shields.io/badge/License-MIT-22b394.svg)](https://github.com/Bright9025/folder-syncer/blob/main/LICENSE)
[![Conventional Commits](https://img.shields.io/badge/Conventional%20Commits-1.0.0-%23FE5196?logo=conventionalcommits&logoColor=white)](https://conventionalcommits.org)

> 无安装 · 无第三方依赖 · 无窗口 · 无托盘 · 无弹窗

> 当前版本：**v1.1.0** · [GitHub Releases](https://github.com/Bright9025/folder-syncer/releases)

</div>

## ✨ 功能特点

- 🔐 **全程 FTPS** – 显式 TLS + 证书 SHA256 指纹锁定；服务器没开加密时只写日志、不登录、不发送密码、不同步。
- 🪶 **零依赖单文件** – 用 Windows 自带的 .NET Framework 编译，62 KB 一个 exe，不需要 Python / Node / SSH 客户端，也不需要管理员权限常驻。
- 📤 **上传端（`--sync`）** – 把本地目录增量推到服务器；只传新增和变化的文件，上传用「临时文件 + 改名」，中断不会写坏服务器上的旧版本。
- 📥 **保存端（`--archive`）** – 把服务器文件拉回本地存档，**校验大小和内容后**才删除服务器副本，同名文件内容变化时旧版本自动进 `_history\`。
- 🧾 **已归档清单** – 保存端在服务器上维护一份 `_foldersyncer\<目录>.tsv`，记下哪些文件已经安全落地；上传端读到清单就跳过，**服务器删了也不会重传**。
- 🗂️ **服务器只当中转站** – 文件被拉走后立刻删除，服务器上长期只有几十 KB 的清单，适合空间很小的小服务器。
- 🧩 **多任务** – 一个 `[job:名字]` 就是一组「本地目录 → 服务器目录」，可以配任意组。
- 🚫 **默认不删别人的文件** – 只清理自己上传过、后来在本地被删掉的文件；服务器上原有文件默认不动。
- 🕒 **自动运行** – 自带计划任务注册脚本：登录后触发 + 每天定时 / 每 N 分钟，命令行带 `--sync` 或 `--archive`，完全静默。
- 📝 **只写文件不打扰** – 所有结果写到 `logs\last-run.txt`、`logs\archive-report.txt` 和按天切分的日志；程序里没有任何窗口、托盘图标或 MessageBox。
- 🧮 **内容哈希校验** – 上传端按 SHA256 记账，保存端下载后重算比对，**一致才删服务器副本**；传输截断或损坏会被拦下，服务器文件保留。
- 📏 **单文件大小上限** – `max_file_mb` 设个值，超过的文件自动跳过并在日志里列出，避免大文件把服务器塞满、把同步拖慢。
- 🔄 **自动更新** – 可从 GitHub Releases 检查新版本，下载后**下次启动自动替换**（旧版本保留可回滚），ini 里一个开关控制。

## 🧭 三端架构

```text
 上传端（目标 PC）              服务器（空间很小）                保存端（常开机）
┌────────────────┐   FTPS   ┌──────────────────────┐   FTPS   ┌──────────────────┐
│ D:\你的目录     │ ───────▶ │ /你的目录（中转文件）  │ ───────▶ │ D:\你的存档       │
│ state\（增量）  │          │ /_foldersyncer/*.tsv    │          │ _history\（旧版）  │
└────────────────┘          │ （只留清单，几十 KB）  │          └──────────────────┘
        ▲                   └──────────────────────┘
        └──────── 读清单，已归档的文件不再重传 ────────┘
```

1. **上传端**运行时比对本地 `state\`，把新增/修改的文件传到服务器。
2. **保存端**每 30 分钟（可配）把服务器文件下载到本地存档，校验通过后删除服务器副本，并把路径写进清单。
3. **上传端**下次运行先读清单：清单里有、本地又没改过的文件直接跳过——即使服务器上已经没有它。

## 📁 项目结构

```text
folder-syncer/
├── code/
│   ├── src/                    # 主程序源码（C#，C# 5 语法，兼容 .NET Framework 4.5+）
│   │   ├── Program.cs          # 入口：命令行、--sync / --archive / --test / --install-task
│   │   ├── SyncEngine.cs       # 上传端：增量比对、覆盖上传、回收站、清单跳过
│   │   ├── ArchiveEngine.cs    # 保存端：下载、校验、删除服务器副本、写清单
│   │   ├── FtpClient.cs        # 自研 FTP/FTPS 客户端（AUTH TLS、PBSZ/PROT、MLSD、MFMT）
│   │   ├── Manifest.cs         # 已归档清单的读写
│   │   ├── RemoteTree.cs       # 远端目录递归列举（跳过 _trash / _foldersyncer）
│   │   ├── TaskInstaller.cs    # 计划任务注册（schtasks + XML）
│   │   ├── Config.cs           # INI 解析
│   │   ├── Logger.cs           # 日志与报告
│   │   └── Util.cs             # 路径、glob、编码等工具
│   ├── devtools/               # 开发自测：模拟 FTP 服务器、清理工具、测试脚本
│   ├── build.bat               # 一键编译到 release\upload 和 release\archive
│   └── pack-release.ps1        # 打包三个 Releases zip（完整 / 上传端 / 保存端）
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

1. 把 `release\upload\` 整个目录拷到目标机（建议放 D 盘，C 盘重置也不影响）。exe 已随仓库提供，也可以从 [Releases](https://github.com/Bright9025/folder-syncer/releases) 下载打好的 zip。
2. 打开 `config.ini`，填写服务器地址、账号密码和要同步的目录（想要英文注释版就用 `config.en.ini`，**记得先把文件名改成 `config.ini`，把 `.en` 去掉**）：

```ini
[server]
host = 你的服务器IP或域名
user = 你的FTP账号
password = 你的FTP密码

[job:你的任务]
enabled = true
local = D:\你的目录
remote = /你的目录
mode = upload
```

3. 双击 `test-connection.bat` 确认「加密：FTPS（TLS 已启用）」且目标目录可达。
4. 双击 `install-task.bat` 注册计划任务（登录后 30 秒 + 每天定时）。

### 保存端（常开机）

1. 把 `release\archive\` 拷到那台常开的电脑。
2. 打开 `archive.ini`，`[server]` 与上传端保持一致，改好存档目录（英文注释版是 `archive.en.ini`，同样**先把文件名改成 `archive.ini`**）：

```ini
[schedule]
mode = archive
repeat_minutes = 30

[job:你的任务]
enabled = true
local = D:\你的存档
remote = /你的目录
```

3. 双击 `archive-now.bat` 先手动跑一次看报告（加 `--dry-run` 只看不动）。
4. 双击 `archive-install-task.bat` 注册归档任务。

## 🔐 FTPS 加密

程序只走**显式 FTPS**（`AUTH TLS` → `PBSZ` / `PROT P`，仍然用 21 端口），不依赖隐式 FTPS 的 990 端口。

**默认策略：没加密就不同步。** `allow_plaintext = false` 时，如果服务器没开 TLS，程序只写一条 WARN 日志（`logs\last-run.txt` 里写「未执行：服务器未启用加密」），不登录、不发送账号密码、不做任何同步，退出码 0——密码不会在公网上裸奔。

### 服务器端怎么开

**FileZilla Server**

- 0.9.x：`Edit → Settings → SSL/TLS settings`，勾选 *Enable SSL/TLS support* 和 *Allow explicit FTP over TLS*，点 *Generate new certificate* 生成证书
- 1.x：`Server → TLS settings`，启用 *Enable explicit FTP over TLS*，选择或生成证书
- 建议同时勾上「不允许未加密的 FTP」（Force explicit），明文连接会被服务器直接拒绝

**IIS FTP**

- IIS 管理器 → 站点 → *FTP SSL 设置* → 选择证书 → 「允许」或「需要」SSL 连接

### 本机怎么配

1. `tls = auto`：服务器支持就加密；改成 `require` 则没加密时直接报错
2. 自签证书：先把 `tls_verify = false`，双击 `test-connection.bat`
3. 报告里会打印「证书 SHA256 指纹」，把这串 64 位十六进制填进 `tls_fingerprint`，再把 `tls_verify` 改回 `true`——这样是按指纹锁定，比一直关校验安全得多
4. 服务器用受信任 CA 的证书（比如正式域名证书）时，直接 `tls_verify = true`，不用填指纹

### 报错对照

| 现象 | 原因与处理 |
| --- | --- |
| 日志「服务器未启用加密」（AUTH TLS 返回 5xx） | 服务器还没开 FTPS，按上面开一下 |
| 「证书校验失败」并打印指纹 | 自签证书：把指纹填进 `tls_fingerprint`，或临时 `tls_verify = false` |
| 登录失败 `530` | 账号或密码不对 |
| 能登录但传文件超时 | 被动模式数据端口被防火墙挡了，放行 PASV 端口范围 |
| 想强制加密 | 把 `tls` 改成 `require` |

> `allow_plaintext = true` 只建议在内网或测试环境用；程序每次都会在日志里记一条警告。
> `test-connection.bat` 生成的 `logs\test-report.txt` 里能看到服务器 banner、`FEAT` 和是否启用了 TLS，排错时先看它。
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
| `manifest_dir` | 清单目录，默认 `/_foldersyncer`，两端保持一致 |
| `timeout_sec` | 网络超时 |

### `[schedule]`

| 键 | 说明 |
| --- | --- |
| `mode` | `sync`（上传端）/ `archive`（保存端），决定计划任务跑哪个模式 |
| `on_logon` | 登录后 30 秒跑一次 |
| `daily_times` | 每天定时，如 `09:00, 18:00` |
| `repeat_minutes` | 每隔 N 分钟跑一次（0 = 不重复） |
| `task_name` | 计划任务名字 |

### `[update]`

| 键 | 说明 |
| --- | --- |
| `enabled` | 是否自动检查更新，默认 `true` |
| `repo` | 仓库，默认 `Bright9025/folder-syncer` |
| `check_hours` | 两次检查的最小间隔（小时），默认 24 |
| `asset` | 取哪个发布包：`auto`（按当前角色）/ `upload` / `archive` |

> 更新只在本次同步/归档**结束之后**检查，下载到 `updates\v<版本>\`，**下一次运行开始时**才替换 exe（旧文件保留为 `FolderSyncer.exe.old`）。
> 检查失败、下载失败、替换失败都只写日志，**绝不影响同步本身**。

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
| `max_file_mb` | 单文件大小上限（MB），`0` = 不限制；超过的自动跳过 |
| `verify_hash` | 是否做内容哈希校验，默认 `true` |

## 📜 已归档清单

保存端在服务器上维护 `_foldersyncer\<远端目录名>.tsv`：

```text
# FolderSyncer manifest v1
a.txt	14	639312164783098361
子目录/b.txt	6	639312164783098361
```

- 只有保存端会写它；上传端只读。
- 上传端规则：本地没变 **且** 服务器上没有 **且** 清单里有 → 跳过（已被归档）。
- 删掉清单不会丢数据，只会让上传端在服务器缺失时重传一次。

### 哈希清单

上传端每次上传成功后会算一遍该文件的 SHA256（在发送过程中顺带计算，不额外读盘），写入服务器上的
`_foldersyncer\hash-<目录名>.tsv`。保存端下载时边写边算，和这份记录比对：

- 一致 → 删除服务器副本、写进已归档清单、从哈希清单里划掉该条
- 不一致 → **自动重新下载一次**
  - 第二次一致 → 正常归档
  - 两次都不一致 → **保留服务器文件**、本次记错误，并在服务器上把该文件标记为「需要重传」；上传端下次运行会强制重传它，归档成功后标记自动清除
  - 同一个文件连续 3 次都失败 → 停止自动重传并写一条明显错误，避免网络持续异常时来回折腾

哈希结果同时缓存在本地 `state\` 里（按 大小 + 修改时间 命中），没变过的文件不会重复计算。
首次开启 `verify_hash` 时会把所有文件读一遍，之后都是增量。

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
code\build.bat                     :: 编译到 release\upload 和 release\archive
code\devtools\run-tests.ps1        :: 上传端自测（MLSD/MFMT/回退/未加密跳过）
code\devtools\run-tests-3tier.ps1  :: 三端自测（上传 -> 归档 -> 不重传）
powershell -File code\pack-release.ps1 -Version 1.1.0   :: 打 3 个 Releases 包 -> dist\v1.1.0\
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
