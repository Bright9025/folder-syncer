using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace FolderSyncer
{
    /// <summary>
    /// 自动更新：检查 GitHub Releases，下载对应角色的包，解出 FolderSyncer.exe，
    /// 下次启动时把当前 exe 换掉（运行中的 exe 可以改名，不能删除/覆盖）。
    /// 任何一步失败都不影响同步本身。
    /// </summary>
    internal static class Updater
    {
        private const string ExeName = "FolderSyncer.exe";

        public static string UpdatesDir(string baseDir)
        {
            return Path.Combine(baseDir, "updates");
        }

        private static string PendingPath(string baseDir)
        {
            return Path.Combine(UpdatesDir(baseDir), "pending.txt");
        }

        private static string LastCheckPath(string baseDir)
        {
            return Path.Combine(UpdatesDir(baseDir), "last-check.txt");
        }

        /// <summary>启动时调用：如果上次下载了新版本，把它换到正式位置（本次仍用旧进程跑完）。</summary>
        public static void ApplyPending(string exePath)
        {
            try
            {
                string baseDir = Path.GetDirectoryName(exePath);
                string pending = PendingPath(baseDir);
                if (!File.Exists(pending)) return;

                string[] lines = File.ReadAllLines(pending, Encoding.UTF8);
                string newExe = lines.Length > 0 ? lines[0].Trim() : "";
                string ver = lines.Length > 1 ? lines[1].Trim() : "";
                if (newExe.Length == 0 || !File.Exists(newExe))
                {
                    Log.Warn("待应用的更新文件不存在，已忽略：" + newExe);
                    try { File.Delete(pending); } catch { }
                    return;
                }

                string oldExe = exePath + ".old";
                try { if (File.Exists(oldExe)) File.Delete(oldExe); } catch { }

                try
                {
                    File.Move(exePath, oldExe);
                }
                catch (Exception ex)
                {
                    Log.Warn("更新：无法重命名当前程序（可能被杀软占用），本次跳过，下次再试：" + ex.Message);
                    return;
                }

                try
                {
                    File.Move(newExe, exePath);
                }
                catch (Exception ex)
                {
                    Log.Error("更新：写入新版本失败，已回滚：" + ex.Message);
                    try { File.Move(oldExe, exePath); } catch { }
                    return;
                }

                try { File.Delete(pending); } catch { }
                Log.Info("更新：已切换到新版本 " + ver + "，下次运行生效（旧版本保留为 " + Path.GetFileName(oldExe) + "）");
            }
            catch (Exception ex)
            {
                Log.Warn("更新：应用待更新失败（忽略）：" + ex.Message);
            }
        }

        /// <summary>同步/归档结束后调用：间隔够久就检查一次，有新版本就下载并写好 pending。</summary>
        public static void CheckAndDownload(AppConfig cfg, bool isArchive)
        {
            if (cfg == null || !cfg.Update.Enabled) return;
            string baseDir = cfg.BaseDir;
            try
            {
                string dir = UpdatesDir(baseDir);
                Directory.CreateDirectory(dir);

                if (File.Exists(PendingPath(baseDir)))
                {
                    Log.Debug("更新：已有待应用的版本，跳过检查");
                    return;
                }

                string last = LastCheckPath(baseDir);
                if (File.Exists(last))
                {
                    string raw = File.ReadAllText(last, Encoding.UTF8).Trim();
                    DateTime when;
                    if (DateTime.TryParseExact(raw, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeLocal, out when) &&
                        (DateTime.Now - when).TotalHours < cfg.Update.CheckHours)
                    {
                        Log.Debug("更新：距上次检查不到 " + cfg.Update.CheckHours + " 小时，跳过");
                        return;
                    }
                }

                Version current = Assembly.GetExecutingAssembly().GetName().Version;
                string json;
                try
                {
                    json = HttpGet("https://api.github.com/repos/" + cfg.Update.Repo + "/releases/latest");
                }
                catch (Exception ex)
                {
                    Log.Warn("更新：检查失败（不影响同步）：" + ex.Message);
                    SaveLastCheck(last);
                    return;
                }
                SaveLastCheck(last);

                string tag;
                if (!TryMatch(json, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"", out tag))
                {
                    Log.Warn("更新：无法解析 GitHub 返回的版本信息");
                    return;
                }

                Version latest;
                if (!TryParseVersion(tag, out latest))
                {
                    Log.Warn("更新：版本号无法识别：" + tag);
                    return;
                }
                if (latest <= current)
                {
                    Log.Info("更新：已是最新版本 v" + current + "（远端 " + tag + "）");
                    return;
                }

                string verText = latest.ToString(3);
                string wanted = AssetName(cfg, verText, isArchive);
                string url = FindAssetUrl(json, wanted);
                if (url == null)
                {
                    string fallback = "folder-syncer-v" + verText + ".zip";
                    url = FindAssetUrl(json, fallback);
                    if (url == null)
                    {
                        Log.Warn("更新：远端有 v" + verText + "，但找不到 " + wanted + " 或 " + fallback);
                        return;
                    }
                    Log.Info("更新：没有找到 " + wanted + "，改用完整包 " + fallback);
                }

                Log.Info("更新：发现新版本 v" + verText + "，开始下载 " + url);
                string zip = Path.Combine(dir, "v" + verText + ".zip");
                DownloadFile(url, zip);

                string extractDir = Path.Combine(dir, "v" + verText);
                Directory.CreateDirectory(extractDir);
                string newExe = ExtractExe(zip, extractDir);
                if (newExe == null)
                {
                    Log.Warn("更新：下载的包里没有 " + ExeName);
                    return;
                }

                File.WriteAllText(PendingPath(baseDir), newExe + "\r\nv" + verText + "\r\n", new UTF8Encoding(false));
                Log.Info("更新：v" + verText + " 已下载，下次运行自动生效");
            }
            catch (Exception ex)
            {
                Log.Warn("更新：处理失败（忽略）：" + ex.Message);
            }
        }

        private static string AssetName(AppConfig cfg, string ver, bool isArchive)
        {
            string asset = cfg.Update.Asset;
            if (asset == "auto") asset = isArchive ? "archive" : "upload";
            if (asset == "archive") return "folder-syncer-archive-v" + ver + ".zip";
            return "folder-syncer-upload-v" + ver + ".zip";
        }

        private static string FindAssetUrl(string json, string assetName)
        {
            MatchCollection ms = Regex.Matches(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]+)\"");
            foreach (Match m in ms)
            {
                string u = m.Groups[1].Value;
                if (u.EndsWith("/" + assetName, StringComparison.OrdinalIgnoreCase)) return u;
            }
            return null;
        }

        private static bool TryParseVersion(string tag, out Version v)
        {
            v = null;
            string s = (tag ?? "").Trim();
            if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase)) s = s.Substring(1);
            int dash = s.IndexOf('-');
            if (dash > 0) s = s.Substring(0, dash);
            return Version.TryParse(s, out v);
        }

        private static bool TryMatch(string text, string pattern, out string value)
        {
            value = "";
            Match m = Regex.Match(text, pattern);
            if (!m.Success) return false;
            value = m.Groups[1].Value;
            return true;
        }

        private static string HttpGet(string url)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "FolderSyncer-Updater";
            req.Accept = "application/vnd.github+json";
            req.Timeout = 15000;
            req.ReadWriteTimeout = 20000;
            req.KeepAlive = false;
            using (WebResponse resp = req.GetResponse())
            using (Stream s = resp.GetResponseStream())
            using (StreamReader r = new StreamReader(s, Encoding.UTF8))
            {
                return r.ReadToEnd();
            }
        }

        private static void DownloadFile(string url, string path)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "FolderSyncer-Updater";
            req.Timeout = 30000;
            req.ReadWriteTimeout = 60000;
            req.KeepAlive = false;
            string part = path + ".part";
            using (WebResponse resp = req.GetResponse())
            using (Stream s = resp.GetResponseStream())
            using (FileStream fs = new FileStream(part, FileMode.Create, FileAccess.Write))
            {
                byte[] buf = new byte[65536];
                int n;
                while ((n = s.Read(buf, 0, buf.Length)) > 0) fs.Write(buf, 0, n);
            }
            if (File.Exists(path)) File.Delete(path);
            File.Move(part, path);
            Log.Info("更新：下载完成 " + Util.FormatBytes(new FileInfo(path).Length));
        }

        private static string ExtractExe(string zipPath, string extractDir)
        {
            using (ZipArchive zip = ZipFile.OpenRead(zipPath))
            {
                foreach (ZipArchiveEntry e in zip.Entries)
                {
                    string name = e.FullName.Replace('\\', '/');
                    if (!name.Equals(ExeName, StringComparison.OrdinalIgnoreCase) &&
                        !name.EndsWith("/" + ExeName, StringComparison.OrdinalIgnoreCase)) continue;
                    string dest = Path.Combine(extractDir, ExeName);
                    if (File.Exists(dest)) File.Delete(dest);
                    e.ExtractToFile(dest, true);
                    return dest;
                }
            }
            return null;
        }

        private static void SaveLastCheck(string path)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\r\n", new UTF8Encoding(false));
            }
            catch { }
        }
    }
}
