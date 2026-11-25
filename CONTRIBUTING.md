# 贡献指南 / Contributing to Dir Sync Agent

感谢你愿意改进 Dir Sync Agent。无论是 Bug 报告、文档修订还是代码提交，都欢迎。

> English speakers: the commands and PR checklist below apply to everyone. Feel free to open an issue in English.

## 开始之前

1. 先阅读 [README.md](./README.md) 和 [English README](./README.en.md)，确认当前功能与已知边界。
2. 搜索现有 [Issues](https://github.com/Bright9025/dir-sync-agent/issues)，避免重复报告。
3. 较大的功能改动请先开 Issue 讨论方向，不要直接提交一个难以评审的大 PR。
4. **不要提交任何真实配置、账号或数据**：`release/**/config.ini`、`release/**/archive.ini` 是随程序提供的空模板，只能保留占位符；真实的服务器地址、账号、密码、证书指纹请放在仓库外。`logs/`、`state/`、`dist/` 已在 `.gitignore` 中排除。

## 开发环境

- Windows 10 / 11
- .NET Framework 4.5 及以上（系统自带；编译用 `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`）
- 不需要 Visual Studio、MSBuild、Node 或任何第三方库

```bat
git clone https://github.com/Bright9025/dir-sync-agent.git
cd dir-sync-agent
code\build.bat
```

编译产物会同时写入 `release\upload\SyncAgent.exe` 和 `release\archive\SyncAgent.exe`。

## 修改后如何验证

1. 跑上传端自测：

```powershell
powershell -ExecutionPolicy Bypass -File code\devtools\run-tests.ps1
```

2. 跑三端自测：

```powershell
powershell -ExecutionPolicy Bypass -File code\devtools\run-tests-3tier.ps1
```

两个脚本都会自动编译并启动本地的模拟 FTP 服务器（不碰真实服务器），覆盖 MLSD/MFMT、无 MLSD 时回退 LIST、未加密时跳过、上传→归档→不重传、`_history` 等场景。

3. 涉及真实服务器时，先 `--test` 再 `--dry-run`，确认无误后再跑 `--sync` / `--archive`。

## 代码约定

- **C# 5 语法**：必须能用 Windows 自带的 csc（只支持到 C# 5）编译。不要用 `?.`、`$"..."`、`nameof`、`out var`、模式匹配、元组等新语法。
- **零第三方依赖**：只引用 `System.dll` / `System.Core.dll` 这类系统程序集；不要引入 NuGet 包。
- **没有 UI**：程序里不出现 `MessageBox`、`Form`、托盘图标或控制台窗口；所有反馈写到日志与报告文件。
- **不静默发密码**：服务器未启用 TLS 且 `allow_plaintext=false` 时，必须只写日志、不登录、不同步。
- **路径统一走 `Util`**：远端路径用 `Util.CombineRemote` / `Util.NormalizeRemote`，本地路径用 `Path.Combine`；不要手拼 `/` 或 `\`。
- **异常要落到日志**：作业级异常不能吞掉，至少 `Log.Error` 一条并计入 `Errors`。
- **列表与元数据**：优先 `MLSD`，失败再退回 `LIST`；不要把 `_trash` / `_syncagent` 当成用户目录递归。
- **文件写入**：上传先写 `.synctmp` 再改名；清单等临时文件放 `state\` 或程序目录，不要用 `%TEMP%`（有些环境不可写）。
- 文案用中文；日志格式保持 `[job:名字] 动作 内容` 的风格，便于用户贴日志排错。

## 分支与提交

从 `main` 拉出功能分支：

```bash
git switch -c feat/your-feature
# 或
git switch -c fix/your-bug
```

一个提交只做一件事，提交信息使用 [约定式提交](https://www.conventionalcommits.org/zh-hans/v1.0.0/)：

```text
feat: 增加按任务单独设置重试次数
fix(archive): 修复中文目录归档后空目录残留
docs: 补充保存端配置说明
test: 覆盖服务器不支持 MFMT 的情况
chore: 更新构建脚本
```

- 描述用中文、动词开头、不超过 40 字；正文说明原因与影响。
- 不要提交 `release/**/*.exe`、日志、`state/`、真实配置或临时文件。
- 提交前先同步 `main`，解决冲突后再发起 PR。

## Pull Request 检查清单

- [ ] 已关联对应 Issue（如有）
- [ ] `code\build.bat` 编译通过且无警告
- [ ] `run-tests.ps1` 与 `run-tests-3tier.ps1` 通过
- [ ] 手动验证了改动涉及的场景（上传 / 归档 / 未加密跳过）
- [ ] 中英文 README 已同步（如涉及行为变化）
- [ ] 没有提交真实配置、日志、构建产物
- [ ] 改动聚焦，提交信息符合约定式提交

## 许可证

提交代码即表示你同意以本项目的 [MIT License](./LICENSE) 授权你的贡献。

## 联系方式

- Bug / 功能建议：GitHub Issues
- 安全问题：请通过 GitHub 私下联系维护者，不要在公开 Issue 中贴出利用细节
