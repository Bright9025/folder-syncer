using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace SyncAgent
{
    internal sealed class LocalItem
    {
        public string Rel = "";
        public string FullPath = "";
        public long Size;
        public long Ticks;
    }

    internal sealed class SyncResult
    {
        public string Job = "";
        public int Uploaded;
        public int Skipped;
        public int Trashed;
        public int Archived;
        public int Deleted;
        public int Errors;
        public int Dirs;
        public long Bytes;
        public string Message = "";
        public bool Aborted;
        public bool PolicySkipped;
        public TimeSpan Elapsed;

        public bool Ok { get { return !Aborted && Errors == 0; } }

        public string Summary()
        {
            string s = string.Format(CultureInfo.InvariantCulture,
                "上传 {0} 个 / {1}，跳过 {2}（其中已归档 {8}），回收 {3}，删除 {4}，新建目录 {5}，错误 {6}，耗时 {7}",
                Uploaded, Util.FormatBytes(Bytes), Skipped, Trashed, Deleted, Dirs, Errors, Util.FormatSpan(Elapsed), Archived);
            if (Message.Length > 0) s += "（" + Message + "）";
            return s;
        }
    }

    /// <summary>记录「上次同步时本地文件长什么样」的本地状态，用于增量判断。</summary>
    internal sealed class StateStore
    {
        private readonly string _path;
        private readonly Dictionary<string, long[]> _map = new Dictionary<string, long[]>(StringComparer.OrdinalIgnoreCase);
        public bool Loaded;
        public int Count { get { return _map.Count; } }

        public StateStore(string path)
        {
            _path = path;
        }

        public bool TryGet(string rel, out long size, out long ticks)
        {
            long[] v;
            if (_map.TryGetValue(rel, out v))
            {
                size = v[0];
                ticks = v[1];
                return true;
            }
            size = 0;
            ticks = 0;
            return false;
        }

        public void Set(string rel, long size, long ticks)
        {
            _map[rel] = new long[] { size, ticks };
        }

        public void Remove(string rel)
        {
            _map.Remove(rel);
        }

        public IEnumerable<string> Keys { get { return _map.Keys; } }

        public void Load()
        {
            _map.Clear();
            try
            {
                if (File.Exists(_path))
                {
                    foreach (string line in File.ReadAllLines(_path, Encoding.UTF8))
                    {
                        if (line.Length == 0) continue;
                        string[] p = line.Split('\t');
                        if (p.Length < 3) continue;
                        long sz, tk;
                        if (long.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out sz) &&
                            long.TryParse(p[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out tk))
                            _map[p[0]] = new long[] { sz, tk };
                    }
                    Loaded = true;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("读取状态文件失败 " + _path + "：" + ex.Message);
            }
        }

        public void Save()
        {
            try
            {
                string dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                StringBuilder sb = new StringBuilder();
                foreach (KeyValuePair<string, long[]> kv in _map)
                {
                    sb.Append(kv.Key).Append('\t')
                      .Append(kv.Value[0].ToString(CultureInfo.InvariantCulture)).Append('\t')
                      .Append(kv.Value[1].ToString(CultureInfo.InvariantCulture)).Append("\r\n");
                }
                string tmp = _path + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                if (File.Exists(_path))
                {
                    try { File.Replace(tmp, _path, null); }
                    catch
                    {
                        File.Delete(_path);
                        File.Move(tmp, _path);
                    }
                }
                else File.Move(tmp, _path);
            }
            catch (Exception ex)
            {
                Log.Warn("保存状态文件失败 " + _path + "：" + ex.Message);
            }
        }
    }

    internal sealed class SyncEngine
    {
        private readonly AppConfig _cfg;
        private readonly string _runStamp;

        public SyncEngine(AppConfig cfg)
        {
            _cfg = cfg;
            _runStamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        }

        public SyncResult RunJob(JobConfig job, bool dryRun)
        {
            DateTime started = DateTime.Now;
            SyncResult res = new SyncResult();
            res.Job = job.Name;

            string localRoot = job.LocalDir;
            if (!Path.IsPathRooted(localRoot)) localRoot = Util.CombineLocal(_cfg.BaseDir, localRoot);
            if (!Directory.Exists(localRoot))
            {
                res.Errors++;
                res.Message = "本地目录不存在：" + localRoot;
                Log.Error("[job:" + job.Name + "] " + res.Message);
                return res;
            }

            string remoteRoot = Util.CombineRemote(_cfg.Server.RemoteRoot, job.RemoteDir);
            string statePath = Path.Combine(_cfg.StateDirAbs, SafeFileName(job.Name) + ".tsv");
            StateStore state = new StateStore(statePath);
            state.Load();
            bool firstRun = !state.Loaded || state.Count == 0;

            Log.Info("[job:" + job.Name + "] 开始：本地 " + localRoot + " -> 服务器 " + remoteRoot +
                     "（模式=" + job.Mode + (job.StrictMirror ? "，严格镜像" : "") + (dryRun ? "，演练" : "") + "）");

            FtpClient ftp = new FtpClient(_cfg.Server);
            try
            {
                ftp.Connect();
                ftp.Login();
                if (!dryRun) ftp.EnsureRemoteDir(remoteRoot);

                Dictionary<string, LocalItem> localFiles = new Dictionary<string, LocalItem>(StringComparer.OrdinalIgnoreCase);
                List<string> localDirs = new List<string>();
                WalkLocal(localRoot, "", job, localFiles, localDirs, 0);

                // 服务器上的「已归档清单」：保存端拉走并删掉的文件会记在这里，用来避免重传
                string manifestPath = Manifest.PathFor(_cfg, remoteRoot);
                Manifest manifest = Manifest.Load(ftp, manifestPath, _cfg.StateDirAbs);

                Dictionary<string, RemoteEntry> remote = RemoteTree.Walk(ftp, remoteRoot, SkipMetaDirs(job));
                Log.Info("[job:" + job.Name + "] 本地文件 " + localFiles.Count + " 个，服务器文件 " + CountFiles(remote) + " 个，" +
                         "已归档 " + manifest.Count + " 条");

                // 1) 建目录（含空目录），服务器上已有的不重复创建
                foreach (string d in localDirs)
                {
                    if (remote.ContainsKey(d)) continue;
                    if (dryRun) { res.Dirs++; continue; }
                    string abs = Util.CombineRemote(remoteRoot, d);
                    try { ftp.EnsureRemoteDir(abs); res.Dirs++; }
                    catch (Exception ex) { res.Errors++; Log.Error("建目录失败 " + abs + "：" + ex.Message); }
                }

                // 2) 上传新增 / 变更 / 服务器缺失
                List<string> rels = new List<string>(localFiles.Keys);
                rels.Sort(StringComparer.OrdinalIgnoreCase);
                foreach (string rel in rels)
                {
                    LocalItem it = localFiles[rel];
                    RemoteEntry re;
                    remote.TryGetValue(rel, out re);
                    long remoteSize = -1;
                    if (re != null)
                    {
                        remoteSize = re.Size >= 0 ? re.Size : (dryRun ? -2 : ftp.Size(Util.CombineRemote(remoteRoot, rel)));
                    }

                    long stSize, stTicks;
                    bool known = state.TryGet(rel, out stSize, out stTicks);
                    bool unchangedLocal = known && stSize == it.Size && stTicks == it.Ticks;
                    bool remoteOk = re != null && remoteSize == it.Size;

                    if (unchangedLocal && remoteOk)
                    {
                        res.Skipped++;
                        continue;
                    }

                    // 服务器上已经没有这个文件，但保存端已经把它拉回本地归档过了 -> 不用重传
                    if (unchangedLocal && re == null && manifest.Contains(rel))
                    {
                        res.Skipped++;
                        res.Archived++;
                        Log.Debug("[job:" + job.Name + "] 已归档，跳过 " + rel);
                        continue;
                    }

                    if (dryRun)
                    {
                        res.Uploaded++;
                        res.Bytes += it.Size;
                        Log.Info("[job:" + job.Name + "] 演练-上传 " + rel + "（" + Util.FormatBytes(it.Size) + (re == null ? "，服务器缺失" : "，内容有变化") + "）");
                        state.Set(rel, it.Size, it.Ticks);
                        continue;
                    }

                    string abs = Util.CombineRemote(remoteRoot, rel);
                    bool ok = UploadWithRetry(ftp, it, abs, rel);
                    if (ok)
                    {
                        FileInfo fi = new FileInfo(it.FullPath);
                        long ticks = fi.LastWriteTimeUtc.Ticks;
                        if (ticks != it.Ticks || fi.Length != it.Size)
                        {
                            Log.Warn("[job:" + job.Name + "] " + rel + " 在上传过程中被改动，本次不计入基线，下次会重传");
                            state.Remove(rel);
                        }
                        else state.Set(rel, it.Size, it.Ticks);
                        res.Uploaded++;
                        res.Bytes += it.Size;
                        Log.Info("[job:" + job.Name + "] 上传 " + rel + "（" + Util.FormatBytes(it.Size) + "）");
                    }
                    else
                    {
                        res.Errors++;
                    }
                }

                // 3) 本地已删除 -> 服务器进回收站
                List<string> gone = new List<string>();
                foreach (string rel in state.Keys)
                    if (!localFiles.ContainsKey(rel)) gone.Add(rel);

                if (!job.IsMirror)
                {
                    if (gone.Count > 0) Log.Info("[job:" + job.Name + "] mode=upload，跳过 " + gone.Count + " 个服务器端删除");
                    foreach (string rel in gone) state.Remove(rel);
                }
                else
                {
                    foreach (string rel in gone)
                    {
                        RemoteEntry re;
                        remote.TryGetValue(rel, out re);
                        if (re == null) { state.Remove(rel); continue; }
                        if (dryRun)
                        {
                            res.Trashed++;
                            Log.Info("[job:" + job.Name + "] 演练-回收 " + rel);
                            state.Remove(rel);
                            continue;
                        }
                        int act = TrashRemote(ftp, job, remoteRoot, rel);
                        if (act == 1) res.Trashed++;
                        else if (act == 2) res.Deleted++;
                        else res.Errors++;
                        state.Remove(rel);
                    }
                }

                // 4) 严格镜像：服务器上多出来的也清掉
                if (job.IsMirror && job.StrictMirror)
                {
                    List<string> extra = new List<string>();
                    foreach (KeyValuePair<string, RemoteEntry> kv in remote)
                        if (!kv.Value.IsDir && !localFiles.ContainsKey(kv.Key)) extra.Add(kv.Key);
                    foreach (string rel in extra)
                    {
                        if (dryRun) { res.Trashed++; Log.Info("[job:" + job.Name + "] 演练-严格镜像回收 " + rel); continue; }
                        int act = TrashRemote(ftp, job, remoteRoot, rel);
                        if (act == 1) res.Trashed++;
                        else if (act == 2) res.Deleted++;
                        else res.Errors++;
                    }
                }
                else if (firstRun)
                {
                    int extra = 0;
                    foreach (KeyValuePair<string, RemoteEntry> kv in remote)
                        if (!kv.Value.IsDir && !localFiles.ContainsKey(kv.Key)) extra++;
                    if (extra > 0)
                        Log.Warn("[job:" + job.Name + "] 服务器上有 " + extra + " 个文件不是本程序上传的，已保留不动（想清掉请设置 strict_mirror=true）");
                }

                if (!job.DeleteToTrash || job.TrashKeepDays > 0)
                {
                    if (!dryRun) PurgeTrash(ftp, job);
                }

                if (!dryRun) state.Save();
                res.Message = firstRun ? "首次运行：只上传不删除，已建立基线" : "";
            }
            catch (FtpNoEncryptionException ex)
            {
                res.PolicySkipped = true;
                res.Message = "服务器未启用加密，按配置跳过（allow_plaintext=false）";
                Log.Warn("[job:" + job.Name + "] " + res.Message + "：" + ex.Message);
            }
            catch (Exception ex)
            {
                res.Errors++;
                res.Aborted = true;
                res.Message = ex.Message;
                Log.Error("[job:" + job.Name + "] 中止：" + ex.GetType().Name + " " + ex.Message);
            }
            finally
            {
                try { ftp.Dispose(); } catch { }
            }

            res.Elapsed = DateTime.Now - started;
            Log.Info("[job:" + job.Name + "] 结束：" + res.Summary());
            return res;
        }

        private static int CountFiles(Dictionary<string, RemoteEntry> map)
        {
            int n = 0;
            foreach (RemoteEntry e in map.Values) if (!e.IsDir) n++;
            return n;
        }

        private bool UploadWithRetry(FtpClient ftp, LocalItem it, string absRemote, string rel)
        {
            int attempts = Math.Max(1, _cfg.General.MaxRetries);
            for (int i = 1; i <= attempts; i++)
            {
                try
                {
                    ftp.EnsureRemoteDir(Util.RemoteParent(absRemote));
                    ftp.UploadFile(it.FullPath, absRemote, null);
                    return true;
                }
                catch (Exception ex)
                {
                    Log.Warn("[job] 上传失败 " + rel + "（第 " + i + "/" + attempts + " 次）：" + ex.Message);
                    if (i == attempts)
                    {
                        Log.Error("[job] 放弃 " + rel + "：" + ex.Message);
                        return false;
                    }
                    Thread.Sleep(_cfg.General.RetryDelayMs);
                    try { if (!ftp.Connected) ftp.Reconnect(); } catch { try { ftp.Reconnect(); } catch { } }
                }
            }
            return false;
        }

        /// <returns>0=失败 1=已移入回收站 2=已直接删除</returns>
        private int TrashRemote(FtpClient ftp, JobConfig job, string remoteRoot, string rel)
        {
            string src = Util.CombineRemote(remoteRoot, rel);
            try
            {
                if (!job.DeleteToTrash)
                {
                    ftp.DeleteFile(src);
                    Log.Info("[job:" + job.Name + "] 删除 " + rel);
                    return 2;
                }
                string dst = Util.CombineRemote(_cfg.Server.RemoteRoot,
                    job.TrashDir.Trim('/') + "/" + SafeFileName(job.Name) + "/" + _runStamp + "/" + rel);
                ftp.EnsureRemoteDir(Util.RemoteParent(dst));
                ftp.Rename(src, dst);
                Log.Info("[job:" + job.Name + "] 回收 " + rel + " -> " + dst);
                return 1;
            }
            catch (Exception ex)
            {
                Log.Error("[job:" + job.Name + "] 回收失败（文件保留在服务器上）" + rel + "：" + ex.Message);
                return 0;
            }
        }

        private void PurgeTrash(FtpClient ftp, JobConfig job)
        {
            if (job.TrashKeepDays <= 0) return;
            string baseDir = Util.CombineRemote(_cfg.Server.RemoteRoot, job.TrashDir.Trim('/') + "/" + SafeFileName(job.Name));
            try
            {
                List<RemoteEntry> stamps = ftp.ListDir(baseDir);
                DateTime limit = DateTime.Now.AddDays(-job.TrashKeepDays);
                foreach (RemoteEntry e in stamps)
                {
                    if (!e.IsDir) continue;
                    DateTime when;
                    if (!DateTime.TryParseExact(e.Name, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out when)) continue;
                    if (when >= limit) continue;
                    Log.Info("[job:" + job.Name + "] 清理过期回收站 " + e.Name);
                    DeleteRemoteTree(ftp, Util.CombineRemote(baseDir, e.Name));
                }
            }
            catch (FtpException) { /* 回收站目录还不存在 */ }
            catch (Exception ex) { Log.Warn("[job:" + job.Name + "] 清理回收站失败：" + ex.Message); }
        }

        private void DeleteRemoteTree(FtpClient ftp, string absDir)
        {
            List<RemoteEntry> list;
            try { list = ftp.ListDir(absDir); }
            catch (FtpException) { return; }
            foreach (RemoteEntry e in list)
            {
                string child = Util.CombineRemote(absDir, e.Name);
                if (e.IsDir) DeleteRemoteTree(ftp, child);
                else
                {
                    try { ftp.DeleteFile(child); }
                    catch (Exception ex) { Log.Warn("删除失败 " + child + "：" + ex.Message); }
                }
            }
            try { ftp.RemoveDir(absDir); }
            catch (Exception ex) { Log.Warn("删除目录失败 " + absDir + "：" + ex.Message); }
        }

        /// <summary>遍历服务器目录时跳过回收站和清单目录，避免把程序自己的元数据当成用户文件。</summary>
        private Predicate<string> SkipMetaDirs(JobConfig job)
        {
            string trash = job.TrashDir.Trim('/');
            string meta = _cfg.Server.ManifestDir.Trim('/');
            return delegate(string rel)
            {
                if (IsUnder(rel, trash) || IsUnder(rel, meta)) return true;
                return false;
            };
        }

        private static bool IsUnder(string rel, string prefix)
        {
            if (prefix.Length == 0) return false;
            if (rel.Equals(prefix, StringComparison.OrdinalIgnoreCase)) return true;
            return rel.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase);
        }
        private void WalkLocal(string dir, string relPrefix, JobConfig job,
                               Dictionary<string, LocalItem> files, List<string> dirs, int depth)
        {
            if (depth > 128) return;
            string[] entries;
            try { entries = Directory.GetFileSystemEntries(dir); }
            catch (UnauthorizedAccessException ex) { Log.Warn("无法读取目录 " + dir + "：" + ex.Message); return; }
            catch (Exception ex) { Log.Warn("枚举目录失败 " + dir + "：" + ex.Message); return; }

            foreach (string p in entries)
            {
                string name = Path.GetFileName(p);
                string rel = relPrefix.Length == 0 ? name : relPrefix + "/" + name;
                FileAttributes attrs;
                try { attrs = File.GetAttributes(p); }
                catch (Exception ex) { Log.Warn("读取属性失败 " + p + "：" + ex.Message); continue; }

                bool isDir = (attrs & FileAttributes.Directory) != 0;
                bool isLink = (attrs & FileAttributes.ReparsePoint) != 0;

                if (Util.IsExcluded(job.Excludes, rel, isDir)) continue;

                if (isDir)
                {
                    if (isLink && !job.FollowLinks) continue;
                    dirs.Add(rel);
                    WalkLocal(p, rel, job, files, dirs, depth + 1);
                }
                else
                {
                    if (isLink && !job.FollowLinks) continue;
                    if (job.Includes.Count > 0 && !Util.MatchesAny(job.Includes, rel, false)) continue;
                    try
                    {
                        FileInfo fi = new FileInfo(p);
                        LocalItem it = new LocalItem();
                        it.Rel = rel;
                        it.FullPath = p;
                        it.Size = fi.Length;
                        it.Ticks = fi.LastWriteTimeUtc.Ticks;
                        files[rel] = it;
                    }
                    catch (Exception ex) { Log.Warn("读取文件信息失败 " + p + "：" + ex.Message); }
                }
            }
        }

        private static string SafeFileName(string s)
        {
            StringBuilder sb = new StringBuilder();
            foreach (char c in s)
            {
                if (char.IsLetterOrDigit(c) || c == '-' || c == '_') sb.Append(c);
                else sb.Append('_');
            }
            return sb.ToString();
        }

        /// <summary>开跑前的连通性检查。返回 false 时 message 里是原因；noEncryption=true 表示按策略跳过。</summary>
        public static bool Preflight(AppConfig cfg, out string message, out bool noEncryption)
        {
            message = "";
            noEncryption = false;
            FtpClient ftp = new FtpClient(cfg.Server);
            try
            {
                ftp.Connect();
                ftp.Login();
                message = (ftp.IsSecure ? "FTPS 已加密" : "明文 FTP") + "，服务器 " + (ftp.ServerBanner.Length > 0 ? ftp.ServerBanner : "无 banner");
                Log.Info("预检通过：" + message);
                return true;
            }
            catch (FtpNoEncryptionException ex)
            {
                noEncryption = true;
                message = ex.Message;
                return false;
            }
            catch (Exception ex)
            {
                message = ex.GetType().Name + "：" + ex.Message;
                return false;
            }
            finally
            {
                try { ftp.Dispose(); } catch { }
            }
        }

        /// <summary>只连上来看看服务器状态，不上传不删除。</summary>
        public static string TestConnection(AppConfig cfg, out bool ok)
        {
            ok = false;
            FtpClient ftp = new FtpClient(cfg.Server);
            StringBuilder sb = new StringBuilder();
            try
            {
                ftp.Connect();
                sb.AppendLine("连接成功：" + cfg.Server.Host + ":" + cfg.Server.Port);
                sb.AppendLine("服务器：" + (ftp.ServerBanner.Length > 0 ? ftp.ServerBanner : "(无 banner)"));
                ftp.Login();
                sb.AppendLine("加密：" + (ftp.IsSecure ? "FTPS（TLS 已启用）" : "明文 FTP"));
                if (ftp.LastTlsFingerprint.Length > 0)
                    sb.AppendLine("证书 SHA256 指纹：" + ftp.LastTlsFingerprint);
                sb.AppendLine("FEAT：" + ftp.FeatText());
                sb.AppendLine("目录列表方式：" + (ftp.SupportsMlsd ? "MLSD（精确）" : "LIST（文本解析）"));
                List<string> rootLines;
                try { rootLines = ftp.ListRaw("/"); }
                catch (Exception ex) { rootLines = null; sb.AppendLine("列根目录失败：" + ex.Message); }
                if (rootLines != null)
                {
                    List<string> shown = new List<string>();
                    foreach (string l in rootLines)
                    {
                        if (l == null || l.Trim().Length == 0) continue;
                        shown.Add(l);
                        if (shown.Count >= 5) break;
                    }
                    sb.AppendLine("根目录 / 可访问，样例 " + shown.Count + " 行（最多 5 行）：");
                    foreach (string l in shown) sb.AppendLine("  " + l);
                }
                foreach (JobConfig job in cfg.Jobs)
                {
                    string remote = Util.CombineRemote(cfg.Server.RemoteRoot, job.RemoteDir);
                    string local = job.LocalDir;
                    bool localOk = Directory.Exists(local);
                    string probe;
                    try
                    {
                        ftp.ListDir(remote);
                        probe = "已存在，可访问";
                    }
                    catch (FtpException)
                    {
                        probe = "尚未创建（首次同步会自动创建）";
                    }
                    sb.AppendLine("任务 [" + job.Name + "] 本地 " + (localOk ? "存在" : "缺失!") + " / 服务器 " + remote + " " + probe);
                }
                ok = true;
            }
            catch (FtpNoEncryptionException ex)
            {
                sb.AppendLine("加密：未启用 TLS（AUTH TLS 被服务器拒绝）");
                sb.AppendLine("状态：" + ex.Message);
                sb.AppendLine();
                sb.AppendLine("按当前配置（allow_plaintext=false）程序不会登录、不会发送账号密码、不会同步。");
                sb.AppendLine("要让它工作，请在服务器的 FTP 上开启显式 FTPS（FileZilla Server: Edit -> Settings -> SSL/TLS settings，");
                sb.AppendLine("勾选 Enable SSL/TLS support + Allow explicit FTP over TLS，生成并选择证书，然后确定）。");
            }
            catch (Exception ex)
            {
                sb.AppendLine("失败：" + ex.GetType().Name + "：" + ex.Message);
                if (!string.IsNullOrEmpty(ftp.LastTlsFingerprint))
                {
                    sb.AppendLine("服务器证书 SHA256 指纹：" + ftp.LastTlsFingerprint);
                    sb.AppendLine("如果是自签证书：把 config.ini 里的 tls_verify 改成 false，或把这个指纹填进 tls_fingerprint。");
                }
            }
            finally
            {
                try { ftp.Dispose(); } catch { }
            }
            return sb.ToString();
        }
    }
}
