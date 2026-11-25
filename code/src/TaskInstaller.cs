using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace SyncAgent
{
    internal static class TaskInstaller
    {
        public static string BuildXml(AppConfig cfg, string exePath)
        {
            string user = Environment.UserDomainName + "\\" + Environment.UserName;
            StringBuilder sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n");
            sb.Append("<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n");
            sb.Append("  <RegistrationInfo>\r\n");
            sb.Append("    <Description>把本地目录同步到服务器的后台程序（SyncAgent）</Description>\r\n");
            sb.Append("  </RegistrationInfo>\r\n");
            sb.Append("  <Triggers>\r\n");

            if (cfg.Schedule.OnLogon)
            {
                sb.Append("    <LogonTrigger>\r\n");
                sb.Append("      <Enabled>true</Enabled>\r\n");
                sb.Append("      <UserId>").Append(Esc(user)).Append("</UserId>\r\n");
                sb.Append("      <Delay>PT30S</Delay>\r\n");
                sb.Append("    </LogonTrigger>\r\n");
            }

            foreach (string t in cfg.Schedule.DailyTimes)
            {
                DateTime when;
                if (!TryParseTime(t, out when)) continue;
                string boundary = DateTime.Today.Add(when.TimeOfDay).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
                sb.Append("    <CalendarTrigger>\r\n");
                sb.Append("      <StartBoundary>").Append(boundary).Append("</StartBoundary>\r\n");
                sb.Append("      <Enabled>true</Enabled>\r\n");
                if (cfg.Schedule.RepeatMinutes > 0)
                {
                    sb.Append("      <Repetition>\r\n");
                    sb.Append("        <Interval>PT").Append(cfg.Schedule.RepeatMinutes).Append("M</Interval>\r\n");
                    sb.Append("        <StopAtDurationEnd>false</StopAtDurationEnd>\r\n");
                    sb.Append("      </Repetition>\r\n");
                }
                sb.Append("      <ScheduleByDay>\r\n");
                sb.Append("        <DaysInterval>1</DaysInterval>\r\n");
                sb.Append("      </ScheduleByDay>\r\n");
                sb.Append("    </CalendarTrigger>\r\n");
            }

            if (cfg.Schedule.DailyTimes.Count == 0 && cfg.Schedule.RepeatMinutes > 0)
            {
                string boundary = DateTime.Now.AddMinutes(1).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
                sb.Append("    <CalendarTrigger>\r\n");
                sb.Append("      <StartBoundary>").Append(boundary).Append("</StartBoundary>\r\n");
                sb.Append("      <Enabled>true</Enabled>\r\n");
                sb.Append("      <Repetition>\r\n");
                sb.Append("        <Interval>PT").Append(cfg.Schedule.RepeatMinutes).Append("M</Interval>\r\n");
                sb.Append("        <StopAtDurationEnd>false</StopAtDurationEnd>\r\n");
                sb.Append("      </Repetition>\r\n");
                sb.Append("      <ScheduleByDay>\r\n");
                sb.Append("        <DaysInterval>1</DaysInterval>\r\n");
                sb.Append("      </ScheduleByDay>\r\n");
                sb.Append("    </CalendarTrigger>\r\n");
            }

            sb.Append("  </Triggers>\r\n");
            sb.Append("  <Principals>\r\n");
            sb.Append("    <Principal id=\"Author\">\r\n");
            sb.Append("      <UserId>").Append(Esc(user)).Append("</UserId>\r\n");
            sb.Append("      <LogonType>InteractiveToken</LogonType>\r\n");
            sb.Append("      <RunLevel>LeastPrivilege</RunLevel>\r\n");
            sb.Append("    </Principal>\r\n");
            sb.Append("  </Principals>\r\n");
            sb.Append("  <Settings>\r\n");
            sb.Append("    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\r\n");
            sb.Append("    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\r\n");
            sb.Append("    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\r\n");
            sb.Append("    <AllowHardTerminate>true</AllowHardTerminate>\r\n");
            sb.Append("    <StartWhenAvailable>true</StartWhenAvailable>\r\n");
            sb.Append("    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>\r\n");
            sb.Append("    <IdleSettings>\r\n");
            sb.Append("      <StopOnIdleEnd>false</StopOnIdleEnd>\r\n");
            sb.Append("      <RestartOnIdle>false</RestartOnIdle>\r\n");
            sb.Append("    </IdleSettings>\r\n");
            sb.Append("    <AllowStartOnDemand>true</AllowStartOnDemand>\r\n");
            sb.Append("    <Enabled>true</Enabled>\r\n");
            sb.Append("    <Hidden>false</Hidden>\r\n");
            sb.Append("    <RunOnlyIfIdle>false</RunOnlyIfIdle>\r\n");
            sb.Append("    <WakeToRun>false</WakeToRun>\r\n");
            sb.Append("    <ExecutionTimeLimit>PT2H</ExecutionTimeLimit>\r\n");
            sb.Append("    <Priority>7</Priority>\r\n");
            sb.Append("  </Settings>\r\n");
            sb.Append("  <Actions Context=\"Author\">\r\n");
            sb.Append("    <Exec>\r\n");
            sb.Append("      <Command>").Append(Esc(exePath)).Append("</Command>\r\n");
            sb.Append("      <Arguments>").Append(cfg.Schedule.Mode == "archive" ? "--archive" : "--sync").Append(" --config &quot;").Append(Esc(cfg.ConfigPath)).Append("&quot;</Arguments>\r\n");
            sb.Append("      <WorkingDirectory>").Append(Esc(Path.GetDirectoryName(exePath))).Append("</WorkingDirectory>\r\n");
            sb.Append("    </Exec>\r\n");
            sb.Append("  </Actions>\r\n");
            sb.Append("</Task>\r\n");
            return sb.ToString();
        }

        private static bool TryParseTime(string s, out DateTime t)
        {
            t = DateTime.MinValue;
            if (string.IsNullOrEmpty(s)) return false;
            string[] fmts = new string[] { "H:mm", "HH:mm", "H:mm:ss", "HH:mm:ss" };
            return DateTime.TryParseExact(s.Trim(), fmts, CultureInfo.InvariantCulture, DateTimeStyles.None, out t);
        }

        private static string Esc(string s)
        {
            if (s == null) return "";
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        }

        public static string Install(AppConfig cfg, string exePath, out bool ok)
        {
            ok = false;
            string xml = BuildXml(cfg, exePath);
            string tmp = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "syncagent-task-" + Guid.NewGuid().ToString("N") + ".xml");
            StringBuilder report = new StringBuilder();
            try
            {
                File.WriteAllText(tmp, xml, new UnicodeEncoding(false, true));
                string outFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "syncagent-schtasks-" + Guid.NewGuid().ToString("N") + ".txt");
                string args = "/c schtasks /Create /TN \"" + cfg.Schedule.TaskName + "\" /XML \"" + tmp + "\" /F > \"" + outFile + "\" 2>&1";
                string output;
                int code = Run("cmd.exe", args, out output);
                string detail = ReadConsoleFile(outFile);
                try { if (File.Exists(outFile)) File.Delete(outFile); } catch { }
                ok = code == 0;
                report.AppendLine(ok ? "计划任务已注册：" : "计划任务注册失败（退出码 " + code + "）：");
                report.AppendLine("  任务名：" + cfg.Schedule.TaskName);
                report.AppendLine("  开机/登录触发：" + (cfg.Schedule.OnLogon ? "是（登录后 30 秒）" : "否"));
                report.AppendLine("  每天时间：" + (cfg.Schedule.DailyTimes.Count > 0 ? string.Join("、", cfg.Schedule.DailyTimes.ToArray()) : "无"));
                if (cfg.Schedule.RepeatMinutes > 0) report.AppendLine("  重复间隔：" + cfg.Schedule.RepeatMinutes + " 分钟");
                if (!string.IsNullOrEmpty(detail)) report.AppendLine("  " + detail.Trim());
                if (!ok)
                {
                    string manual = Path.Combine(Path.GetDirectoryName(exePath), "task.xml");
                    try
                    {
                        File.WriteAllText(manual, xml, new UnicodeEncoding(false, true));
                        report.AppendLine();
                        report.AppendLine("已生成任务定义文件：" + manual);
                        report.AppendLine("可以手动导入：任务计划程序 -> 操作 -> 导入任务 -> 选这个 xml");
                        report.AppendLine("（也可以用管理员身份运行 install-task.bat 再试一次）");
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                report.AppendLine("注册计划任务出错：" + ex.Message);
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
            return report.ToString();
        }

        public static string Uninstall(AppConfig cfg, out bool ok)
        {
            ok = false;
            string outFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "syncagent-schtasks-" + Guid.NewGuid().ToString("N") + ".txt");
            string args = "/c schtasks /Delete /TN \"" + cfg.Schedule.TaskName + "\" /F > \"" + outFile + "\" 2>&1";
            string output;
            int code = Run("cmd.exe", args, out output);
            string detail = ReadConsoleFile(outFile);
            try { if (File.Exists(outFile)) File.Delete(outFile); } catch { }
            ok = code == 0;
            return (ok ? "已删除计划任务 " : "删除计划任务失败（可能本来就不存在）：") + cfg.Schedule.TaskName +
                   (detail.Length > 0 ? "\n" + detail.Trim() : "");
        }

        /// <summary>cmd 重定向出来的文本是控制台代码页（中文系统是 GBK），不能按 UTF-8 读。</summary>
        private static string ReadConsoleFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return "";
                byte[] raw = File.ReadAllBytes(path);
                try { return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage).GetString(raw); }
                catch { return Encoding.Default.GetString(raw); }
            }
            catch { return ""; }
        }

        private static int Run(string file, string args, out string output)
        {
            output = "";
            ProcessStartInfo psi = new ProcessStartInfo(file, args);
            psi.CreateNoWindow = true;
            psi.UseShellExecute = false;
            psi.WindowStyle = ProcessWindowStyle.Hidden;
            using (Process p = Process.Start(psi))
            {
                p.WaitForExit(60000);
                return p.HasExited ? p.ExitCode : -1;
            }
        }
    }
}
