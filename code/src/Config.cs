using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace FolderSyncer
{
    internal sealed class GeneralConfig
    {
        public string LogDir = "logs";
        public int LogKeepDays = 30;
        public string StateDir = "state";
        public int MaxRetries = 3;
        public int RetryDelayMs = 2000;
        public bool Verbose = false;
    }

    internal sealed class ServerConfig
    {
        public string Host = "";
        public int Port = 21;
        public string User = "";
        public string Password = "";
        /// <summary>auto = 服务器支持就加密；require = 必须加密；off = 不尝试加密</summary>
        public string Tls = "auto";
        public bool TlsVerify = true;
        public string TlsFingerprint = "";
        /// <summary>after = 先发命令收到 150 再握手（IIS 常见）；before = 连上数据口先握手</summary>
        public string TlsDataHandshake = "after";
        public bool Passive = true;
        public bool UseControlIp = true;
        public bool Utf8 = true;
        public int TimeoutSec = 30;
        public string RemoteRoot = "/";
        /// <summary>服务器上存放「已归档清单」的目录（相对 remote_root）</summary>
        public string ManifestDir = "/_foldersyncer";
        public bool AllowPlaintext = false;

        public bool TlsWanted { get { return !"off".Equals(Tls, StringComparison.OrdinalIgnoreCase); } }
        public bool TlsRequired { get { return "require".Equals(Tls, StringComparison.OrdinalIgnoreCase); } }
    }

    internal sealed class ScheduleConfig
    {
        public bool OnLogon = true;
        public List<string> DailyTimes = new List<string>();
        public int RepeatMinutes = 0;
        public string TaskName = "FolderSyncer";
        /// <summary>计划任务跑哪种模式：sync = 上传端；archive = 保存端</summary>
        public string Mode = "sync";
    }

    internal sealed class UpdateConfig
    {
        /// <summary>是否自动从 GitHub Releases 检查并更新</summary>
        public bool Enabled = true;
        public string Repo = "Bright9025/folder-syncer";
        /// <summary>两次检查之间至少间隔多少小时</summary>
        public int CheckHours = 24;
        /// <summary>取哪个发布包：auto（按当前角色）/ upload / archive</summary>
        public string Asset = "auto";
    }

    internal sealed class JobConfig
    {
        public string Name = "";
        public bool Enabled = true;
        public string LocalDir = "";
        /// <summary>相对 server.remote_root 的服务器目录，如 /wwwroot/myapp</summary>
        public string RemoteDir = "";
        /// <summary>mirror = 本地删了服务器也删（进回收站）；upload = 只上传，从不删服务器</summary>
        public string Mode = "mirror";
        /// <summary>true = 服务器上本地没有的文件也全部清走（危险，默认 false，只清理自己上传过的）</summary>
        public bool StrictMirror = false;
        public bool DeleteToTrash = true;
        public string TrashDir = "/_trash";
        public int TrashKeepDays = 30;
        public List<string> Excludes = new List<string>();
        public List<string> Includes = new List<string>();
        public bool FollowLinks = false;
        /// <summary>单文件大小上限（MB），0 = 不限制；超过的文件自动跳过</summary>
        public int MaxFileMb = 0;
        /// <summary>是否做内容哈希校验（上传端记哈希、保存端下载后比对）</summary>
        public bool VerifyHash = true;

        public bool IsMirror { get { return "mirror".Equals(Mode, StringComparison.OrdinalIgnoreCase); } }
    }

    internal sealed class AppConfig
    {
        public string ConfigPath = "";
        public string BaseDir = "";
        public GeneralConfig General = new GeneralConfig();
        public ServerConfig Server = new ServerConfig();
        public ScheduleConfig Schedule = new ScheduleConfig();
        public UpdateConfig Update = new UpdateConfig();
        public List<JobConfig> Jobs = new List<JobConfig>();
        public List<string> Warnings = new List<string>();
        public List<string> Errors = new List<string>();

        public string LogDirAbs { get { return Util.CombineLocal(BaseDir, General.LogDir); } }
        public string StateDirAbs { get { return Util.CombineLocal(BaseDir, General.StateDir); } }

        public static AppConfig Load(string path)
        {
            AppConfig cfg = new AppConfig();
            cfg.ConfigPath = Path.GetFullPath(path);
            cfg.BaseDir = Path.GetDirectoryName(cfg.ConfigPath);
            string text = Util.ReadTextAuto(cfg.ConfigPath);
            string section = "";
            JobConfig curJob = null;
            Dictionary<string, JobConfig> jobs = new Dictionary<string, JobConfig>(StringComparer.OrdinalIgnoreCase);

            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            int lineNo = 0;
            foreach (string rawLine in lines)
            {
                lineNo++;
                string line = rawLine.Trim();
                if (line.Length == 0) continue;
                if (line[0] == ';' || line[0] == '#') continue;
                if (line[0] == '[')
                {
                    int close = line.IndexOf(']');
                    if (close < 0) { cfg.Errors.Add("第 " + lineNo + " 行：节名缺少 ]"); continue; }
                    string name = line.Substring(1, close - 1).Trim();
                    section = name.ToLowerInvariant();
                    curJob = null;
                    if (section.StartsWith("job:") || section.StartsWith("job."))
                    {
                        string jobName = name.Substring(4).Trim();
                        if (jobName.Length == 0) { cfg.Errors.Add("第 " + lineNo + " 行：[job:] 后面缺少任务名"); continue; }
                        curJob = new JobConfig();
                        curJob.Name = jobName;
                        jobs[jobName] = curJob;
                    }
                    continue;
                }
                int eq = line.IndexOf('=');
                if (eq <= 0) { cfg.Errors.Add("第 " + lineNo + " 行：不是 key=value 格式 -> " + line); continue; }
                string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                string val = StripInlineComment(line.Substring(eq + 1)).Trim();
                if (val.Length >= 2 && ((val[0] == '"' && val[val.Length - 1] == '"') || (val[0] == '\'' && val[val.Length - 1] == '\'')))
                    val = val.Substring(1, val.Length - 2);
                val = Environment.ExpandEnvironmentVariables(val);
                try
                {
                    if (curJob != null) ApplyJob(curJob, key, val);
                    else ApplySection(cfg, section, key, val, lineNo);
                }
                catch (Exception ex)
                {
                    cfg.Errors.Add("第 " + lineNo + " 行 [" + key + "] 解析失败：" + ex.Message);
                }
            }

            cfg.Jobs.AddRange(jobs.Values);
            cfg.Normalize();
            return cfg;
        }

        /// <summary>
        /// 允许行尾注释：值后面跟「空格 + ; 或 #」就算注释开始。
        /// 引号内的内容原样保留，所以密码里带 ; 或 # 时请写成 password = "abc;def"。
        /// </summary>
        private static string StripInlineComment(string v)
        {
            if (string.IsNullOrEmpty(v)) return "";
            bool inDouble = false, inSingle = false;
            for (int i = 0; i < v.Length; i++)
            {
                char c = v[i];
                if (c == '"') inDouble = !inDouble;
                else if (c == '\'') inSingle = !inSingle;
                else if (!inDouble && !inSingle && (c == ';' || c == '#') && i > 0 && (v[i - 1] == ' ' || v[i - 1] == '\t'))
                    return v.Substring(0, i);
            }
            return v;
        }

        private static bool ParseBool(string v, bool def)
        {
            if (string.IsNullOrEmpty(v)) return def;
            v = v.Trim().ToLowerInvariant();
            if (v == "1" || v == "true" || v == "yes" || v == "on" || v == "y") return true;
            if (v == "0" || v == "false" || v == "no" || v == "off" || v == "n") return false;
            return def;
        }

        private static int ParseInt(string v, int def)
        {
            int n;
            if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) return n;
            return def;
        }

        private static void ApplySection(AppConfig cfg, string section, string key, string val, int lineNo)
        {
            switch (section)
            {
                case "general":
                case "":
                    switch (key)
                    {
                        case "log_dir": cfg.General.LogDir = val; break;
                        case "log_keep_days": cfg.General.LogKeepDays = ParseInt(val, 30); break;
                        case "state_dir": cfg.General.StateDir = val; break;
                        case "max_retries": cfg.General.MaxRetries = ParseInt(val, 3); break;
                        case "retry_delay_ms": cfg.General.RetryDelayMs = ParseInt(val, 2000); break;
                        case "verbose": cfg.General.Verbose = ParseBool(val, false); break;
                        default: cfg.Warnings.Add("第 " + lineNo + " 行：[general] 未知键 " + key); break;
                    }
                    break;
                case "server":
                    switch (key)
                    {
                        case "host": cfg.Server.Host = val; break;
                        case "port": cfg.Server.Port = ParseInt(val, 21); break;
                        case "user": cfg.Server.User = val; break;
                        case "password": case "pass": cfg.Server.Password = val; break;
                        case "tls": cfg.Server.Tls = val.ToLowerInvariant(); break;
                        case "tls_verify": cfg.Server.TlsVerify = ParseBool(val, true); break;
                        case "tls_fingerprint": cfg.Server.TlsFingerprint = val.Replace(" ", "").Replace(":", "").ToUpperInvariant(); break;
                        case "tls_data_handshake": cfg.Server.TlsDataHandshake = val.ToLowerInvariant(); break;
                        case "passive": cfg.Server.Passive = ParseBool(val, true); break;
                        case "use_control_ip": cfg.Server.UseControlIp = ParseBool(val, true); break;
                        case "utf8": cfg.Server.Utf8 = ParseBool(val, true); break;
                        case "timeout_sec": cfg.Server.TimeoutSec = ParseInt(val, 30); break;
                        case "remote_root": cfg.Server.RemoteRoot = val; break;
                        case "manifest_dir": cfg.Server.ManifestDir = val; break;
                        case "allow_plaintext": cfg.Server.AllowPlaintext = ParseBool(val, false); break;
                        default: cfg.Warnings.Add("第 " + lineNo + " 行：[server] 未知键 " + key); break;
                    }
                    break;
                case "update":
                    switch (key)
                    {
                        case "enabled": cfg.Update.Enabled = ParseBool(val, true); break;
                        case "auto_update": cfg.Update.Enabled = ParseBool(val, true); break;
                        case "repo": cfg.Update.Repo = val.Trim(); break;
                        case "check_hours": cfg.Update.CheckHours = ParseInt(val, 24); break;
                        case "asset": cfg.Update.Asset = val.ToLowerInvariant(); break;
                        default: cfg.Warnings.Add("第 " + lineNo + " 行：[update] 未知键 " + key); break;
                    }
                    break;
                case "schedule":
                    switch (key)
                    {
                        case "on_logon": cfg.Schedule.OnLogon = ParseBool(val, true); break;
                        case "daily_times":
                            cfg.Schedule.DailyTimes.AddRange(Util.SplitList(val));
                            break;
                        case "repeat_minutes": cfg.Schedule.RepeatMinutes = ParseInt(val, 0); break;
                        case "task_name": if (val.Length > 0) cfg.Schedule.TaskName = val; break;
                        case "mode": cfg.Schedule.Mode = val.ToLowerInvariant(); break;
                        default: cfg.Warnings.Add("第 " + lineNo + " 行：[schedule] 未知键 " + key); break;
                    }
                    break;
                default:
                    cfg.Warnings.Add("第 " + lineNo + " 行：未知节 [" + section + "]");
                    break;
            }
        }

        private static void ApplyJob(JobConfig job, string key, string val)
        {
            switch (key)
            {
                case "enabled": job.Enabled = ParseBool(val, true); break;
                case "local": job.LocalDir = val; break;
                case "remote": job.RemoteDir = val; break;
                case "mode": job.Mode = val.ToLowerInvariant(); break;
                case "strict_mirror": job.StrictMirror = ParseBool(val, false); break;
                case "delete_to_trash": job.DeleteToTrash = ParseBool(val, true); break;
                case "trash_dir": job.TrashDir = val; break;
                case "trash_keep_days": job.TrashKeepDays = ParseInt(val, 30); break;
                case "exclude": job.Excludes.AddRange(Util.SplitList(val)); break;
                case "include": job.Includes.AddRange(Util.SplitList(val)); break;
                case "follow_links": job.FollowLinks = ParseBool(val, false); break;
                case "max_file_mb": job.MaxFileMb = ParseInt(val, 0); break;
                case "verify_hash": job.VerifyHash = ParseBool(val, true); break;
                default: break;
            }
        }

        private void Normalize()
        {
            if (Server.TimeoutSec <= 0) Server.TimeoutSec = 30;
            if (Server.Port <= 0) Server.Port = 21;
            if (string.IsNullOrEmpty(Server.Tls)) Server.Tls = "auto";
            if (General.LogKeepDays <= 0) General.LogKeepDays = 30;
            if (General.MaxRetries < 0) General.MaxRetries = 0;
            if (Schedule.Mode != "archive") Schedule.Mode = "sync";
            if (Update.CheckHours < 1) Update.CheckHours = 24;
            if (Update.Asset != "upload" && Update.Asset != "archive") Update.Asset = "auto";
            if (string.IsNullOrEmpty(Update.Repo)) Update.Repo = "Bright9025/folder-syncer";
            Server.RemoteRoot = Util.NormalizeRemote(Server.RemoteRoot);
            Server.ManifestDir = Util.NormalizeRemote(Server.ManifestDir);

            List<string> defaultExcludes = new List<string> { "~$*", "*.tmp", "Thumbs.db", "desktop.ini", ".DS_Store", "*.swp", "*.lock" };

            foreach (JobConfig j in Jobs)
            {
                if (string.IsNullOrEmpty(j.RemoteDir)) j.RemoteDir = "/" + SafeName(j.Name);
                j.RemoteDir = Util.NormalizeRemote(j.RemoteDir);
                j.TrashDir = Util.NormalizeRemote(j.TrashDir);
                if (j.MaxFileMb < 0) j.MaxFileMb = 0;
                if (string.IsNullOrEmpty(j.Mode)) j.Mode = "mirror";
                if (j.Mode != "mirror" && j.Mode != "upload")
                {
                    Warnings.Add("[job:" + j.Name + "] mode=" + j.Mode + " 不认识，按 mirror 处理");
                    j.Mode = "mirror";
                }
                j.Excludes.AddRange(defaultExcludes);
                List<string> uniq = new List<string>();
                foreach (string s in j.Excludes) if (!uniq.Contains(s)) uniq.Add(s);
                j.Excludes = uniq;

                if (string.IsNullOrEmpty(j.LocalDir))
                    Errors.Add("[job:" + j.Name + "] 缺少 local= 本地目录");
            }

            if (string.IsNullOrEmpty(Server.Host)) Errors.Add("[server] 缺少 host=");
            if (string.IsNullOrEmpty(Server.User)) Errors.Add("[server] 缺少 user=");
            if (string.IsNullOrEmpty(Server.Password)) Errors.Add("[server] 缺少 password=");
            if (Jobs.Count == 0) Errors.Add("没有配置任何 [job:名称] 同步任务");
        }

        private static string SafeName(string s)
        {
            StringBuilder sb = new StringBuilder();
            foreach (char c in s) sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            return sb.ToString();
        }

        public static string DefaultPath()
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.ini");
        }
    }
}
