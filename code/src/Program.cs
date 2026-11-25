using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;

namespace FolderSyncer
{
    internal static class Program
    {
        private const string AppName = "FolderSyncer";

        [STAThread]
        private static int Main(string[] args)
        {
            string exePath = Assembly.GetExecutingAssembly().Location;
            string baseDir = Path.GetDirectoryName(exePath);
            string configPath = Path.Combine(baseDir, "config.ini");
            bool dryRun = false, verbose = false, test = false, install = false, uninstall = false, help = false, archive = false;

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant();
                if (a == "--config" || a == "/config")
                {
                    if (i + 1 < args.Length) configPath = args[++i];
                }
                else if (a == "--test" || a == "/test") test = true;
                else if (a == "--install-task" || a == "/install-task") install = true;
                else if (a == "--uninstall-task" || a == "/uninstall-task") uninstall = true;
                else if (a == "--dry-run" || a == "/dry-run") dryRun = true;
                else if (a == "--verbose" || a == "/verbose") verbose = true;
                else if (a == "--archive" || a == "/archive") archive = true;
                else if (a == "--help" || a == "/help" || a == "-h" || a == "/?") help = true;
            }

            string defaultLogDir = Path.Combine(baseDir, "logs");

            if (help)
            {
                WriteReport(Path.Combine(baseDir, "logs"), "help.txt", HelpText());
                return 0;
            }

            AppConfig cfg = null;
            try
            {
                if (!File.Exists(configPath))
                {
                    string msg = "找不到配置文件：\r\n" + configPath + "\r\n\r\n请把 config.ini 放在程序同一个目录里。";
                    Log.Init(defaultLogDir, 30, false);
                    Log.Error(msg.Replace("\r\n", " "));
                    WriteReport(defaultLogDir, "last-run.txt", msg);
                    return 2;
                }
                cfg = AppConfig.Load(configPath);
            }
            catch (Exception ex)
            {
                Log.Init(defaultLogDir, 30, false);
                Log.Error("读取配置失败：" + ex.Message);
                WriteReport(defaultLogDir, "last-run.txt", "读取配置失败：" + ex.Message);
                return 2;
            }

            try { Log.Init(cfg.LogDirAbs, cfg.General.LogKeepDays, verbose || cfg.General.Verbose); }
            catch { }

            if (cfg.Errors.Count > 0)
            {
                StringBuilder sb = new StringBuilder("配置有问题，本次没有执行：\r\n");
                foreach (string e in cfg.Errors) sb.AppendLine("  - " + e);
                Log.Error("配置错误：" + string.Join(" / ", cfg.Errors.ToArray()));
                WriteReport(cfg.LogDirAbs, "last-run.txt", sb.ToString());
                return 2;
            }
            foreach (string w in cfg.Warnings) Log.Warn("配置提醒：" + w);

            if (install)
            {
                bool ok;
                string report = TaskInstaller.Install(cfg, exePath, out ok);
                Log.Info("安装计划任务：" + report.Replace("\r\n", " "));
                WriteReport(cfg.LogDirAbs, "install-report.txt", report);
                return ok ? 0 : 1;
            }

            if (uninstall)
            {
                bool ok;
                string report = TaskInstaller.Uninstall(cfg, out ok);
                Log.Info("删除计划任务：" + report);
                WriteReport(cfg.LogDirAbs, "install-report.txt", report);
                return ok ? 0 : 1;
            }

            if (test)
            {
                bool ok;
                string report = SyncEngine.TestConnection(cfg, out ok);
                Log.Info("连接测试：" + report.Replace("\r\n", " | "));
                WriteReport(cfg.LogDirAbs, "test-report.txt", report);
                return ok ? 0 : 1;
            }

            string mutexName = "Local\\" + AppName + "-" + HashCode(configPath);
            using (Mutex mtx = new Mutex(false, mutexName))
            {
                bool lockTaken = false;
                try { lockTaken = mtx.WaitOne(0); }
                catch (AbandonedMutexException) { lockTaken = true; }
                if (!lockTaken)
                {
                    Log.Info("已有一个同步进程在运行，本次跳过");
                    return 0;
                }
                try
                {
                    if (archive) return RunArchive(cfg, dryRun);
                    return RunSync(cfg, dryRun);
                }
                finally
                {
                    try { mtx.ReleaseMutex(); } catch { }
                }
            }
        }

        private static int RunSync(AppConfig cfg, bool dryRun)
        {
            DateTime started = DateTime.Now;
            StringBuilder report = new StringBuilder();
            report.AppendLine("FolderSyncer 同步结果");
            report.AppendLine("时间：" + started.ToString("yyyy-MM-dd HH:mm:ss"));
            report.AppendLine("配置：" + cfg.ConfigPath);
            report.AppendLine();

            Log.Info("===== 同步开始 " + started.ToString("yyyy-MM-dd HH:mm:ss") + (dryRun ? "（演练模式）" : "") + " =====");

            string preMsg;
            bool noEncryption;
            if (!SyncEngine.Preflight(cfg, out preMsg, out noEncryption))
            {
                if (noEncryption)
                {
                    Log.Warn("===== 未执行：服务器未启用加密 =====");
                    Log.Warn(preMsg);
                    report.AppendLine("未执行：服务器未启用加密。");
                    report.AppendLine(preMsg);
                    report.AppendLine();
                    report.AppendLine("（allow_plaintext=false 时，程序不会在没有加密的通道上发送账号密码，也不会做任何同步。）");
                    WriteReport(cfg.LogDirAbs, "last-run.txt", report.ToString());
                    return 0;
                }
                Log.Error("连接失败：" + preMsg);
                report.AppendLine("未执行：连接服务器失败。");
                report.AppendLine(preMsg);
                WriteReport(cfg.LogDirAbs, "last-run.txt", report.ToString());
                return 1;
            }
            report.AppendLine("服务器：" + cfg.Server.Host + ":" + cfg.Server.Port + "（" + preMsg + "）");
            report.AppendLine();

            int errors = 0;
            int jobs = 0;
            int skipped = 0;
            try
            {
                SyncEngine engine = new SyncEngine(cfg);
                foreach (JobConfig job in cfg.Jobs)
                {
                    if (!job.Enabled)
                    {
                        Log.Info("[job:" + job.Name + "] 已禁用，跳过");
                        report.AppendLine("任务 [" + job.Name + "]：已禁用，跳过");
                        continue;
                    }
                    jobs++;
                    SyncResult r = engine.RunJob(job, dryRun);
                    if (r.PolicySkipped) skipped++;
                    if (!r.Ok) errors++;
                    report.AppendLine("任务 [" + job.Name + "]：" + r.Summary());
                }
            }
            catch (Exception ex)
            {
                errors++;
                Log.Error("同步过程出现未处理异常：" + ex);
                report.AppendLine("同步过程异常：" + ex.Message);
            }

            TimeSpan elapsed = DateTime.Now - started;
            string tail = "总计：任务 " + jobs + " 个，失败 " + errors + " 个" + (skipped > 0 ? "，跳过 " + skipped + " 个" : "") +
                          "，耗时 " + Util.FormatSpan(elapsed);
            report.AppendLine();
            report.AppendLine(tail);
            Log.Info("===== 同步结束：" + tail.Replace("总计：", "") + " =====");
            WriteReport(cfg.LogDirAbs, "last-run.txt", report.ToString());
            return errors == 0 ? 0 : 1;
        }

        private static int RunArchive(AppConfig cfg, bool dryRun)
        {
            DateTime started = DateTime.Now;
            StringBuilder report = new StringBuilder();
            report.AppendLine("FolderSyncer 归档结果（保存端）");
            report.AppendLine("时间：" + started.ToString("yyyy-MM-dd HH:mm:ss"));
            report.AppendLine("配置：" + cfg.ConfigPath);
            report.AppendLine();

            Log.Info("===== 归档开始 " + started.ToString("yyyy-MM-dd HH:mm:ss") + (dryRun ? "（演练模式）" : "") + " =====");

            string preMsg;
            bool noEncryption;
            if (!SyncEngine.Preflight(cfg, out preMsg, out noEncryption))
            {
                if (noEncryption)
                {
                    Log.Warn("===== 未执行：服务器未启用加密 =====");
                    report.AppendLine("未执行：服务器未启用加密。");
                    report.AppendLine(preMsg);
                    WriteReport(cfg.LogDirAbs, "archive-report.txt", report.ToString());
                    return 0;
                }
                Log.Error("连接失败：" + preMsg);
                report.AppendLine("未执行：连接服务器失败。");
                report.AppendLine(preMsg);
                WriteReport(cfg.LogDirAbs, "archive-report.txt", report.ToString());
                return 1;
            }
            report.AppendLine("服务器：" + cfg.Server.Host + ":" + cfg.Server.Port + "（" + preMsg + "）");
            report.AppendLine();

            int errors = 0;
            int jobs = 0;
            int policySkipped = 0;
            try
            {
                ArchiveEngine engine = new ArchiveEngine(cfg);
                foreach (JobConfig job in cfg.Jobs)
                {
                    if (!job.Enabled)
                    {
                        Log.Info("[archive:" + job.Name + "] 已禁用，跳过");
                        report.AppendLine("任务 [" + job.Name + "]：已禁用，跳过");
                        continue;
                    }
                    jobs++;
                    ArchiveResult r = engine.RunJob(job, dryRun);
                    if (r.PolicySkipped) policySkipped++;
                    if (!r.Ok) errors++;
                    report.AppendLine("任务 [" + job.Name + "]：" + r.Summary());
                }
            }
            catch (Exception ex)
            {
                errors++;
                Log.Error("归档过程出现未处理异常：" + ex);
                report.AppendLine("归档过程异常：" + ex.Message);
            }

            TimeSpan elapsed = DateTime.Now - started;
            string tail = "总计：任务 " + jobs + " 个，失败 " + errors + " 个" +
                          (policySkipped > 0 ? "，跳过 " + policySkipped + " 个" : "") +
                          "，耗时 " + Util.FormatSpan(elapsed);
            report.AppendLine();
            report.AppendLine(tail);
            Log.Info("===== 归档结束：" + tail.Replace("总计：", "") + " =====");
            WriteReport(cfg.LogDirAbs, "archive-report.txt", report.ToString());
            return errors == 0 ? 0 : 1;
        }

        private static string HelpText()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(AppName + " 用法（所有结果都写到日志文件，程序不会弹任何窗口）");
            sb.AppendLine();
            sb.AppendLine("  FolderSyncer.exe                     静默执行一次同步");
            sb.AppendLine("  FolderSyncer.exe --sync              同上（计划任务用）");
            sb.AppendLine("  FolderSyncer.exe --test              连接测试 -> logs\\test-report.txt");
            sb.AppendLine("  FolderSyncer.exe --archive           保存端：把服务器文件拉回本地归档并更新清单");
            sb.AppendLine("  FolderSyncer.exe --install-task      注册计划任务 -> logs\\install-report.txt");
            sb.AppendLine("  FolderSyncer.exe --uninstall-task    删除计划任务 -> logs\\install-report.txt");
            sb.AppendLine("  FolderSyncer.exe --dry-run           演练，不上传不删除");
            sb.AppendLine("  FolderSyncer.exe --verbose           日志里记录每一条 FTP 命令");
            sb.AppendLine("  FolderSyncer.exe --config <路径>     指定配置文件");
            sb.AppendLine("  FolderSyncer.exe --help              把本说明写到 logs\\help.txt");
            sb.AppendLine();
            sb.AppendLine("常用文件：logs\\last-run.txt（最近一次同步的摘要）、logs\\sync-年月日.log（详细日志）");
            return sb.ToString();
        }

        private static void WriteReport(string logDir, string fileName, string text)
        {
            try
            {
                Directory.CreateDirectory(logDir);
                File.WriteAllText(Path.Combine(logDir, fileName), text, new UTF8Encoding(false));
            }
            catch { }
        }

        private static string HashCode(string s)
        {
            uint h = 2166136261;
            foreach (char c in s) { h ^= c; h *= 16777619; }
            return h.ToString("X8");
        }
    }
}
