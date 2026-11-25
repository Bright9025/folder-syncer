using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace FolderSyncer
{
    internal sealed class ArchiveResult
    {
        public string Job = "";
        public int Downloaded;
        public long Bytes;
        public int Deleted;
        public int Duplicate;   // 本地已有同样内容，没重复写
        public int Errors;
        public bool Aborted;
        public bool PolicySkipped;
        public TimeSpan Elapsed;
        public string Message = "";

        public bool Ok { get { return !Aborted && Errors == 0; } }

        public string Summary()
        {
            string s = string.Format(CultureInfo.InvariantCulture,
                "下载 {0} 个 / {1}（其中 {2} 个本地已有相同内容），删除服务器 {3} 个，错误 {4}，耗时 {5}",
                Downloaded, Util.FormatBytes(Bytes), Duplicate, Deleted, Errors, Util.FormatSpan(Elapsed));
            if (Message.Length > 0) s += "（" + Message + "）";
            return s;
        }
    }

    /// <summary>
    /// 保存端：把服务器上的文件拉回本地归档，然后删掉服务器上的副本，并把路径记进清单。
    /// 服务器因此只占很少空间，只留清单，供上传端比对。
    /// </summary>
    internal sealed class ArchiveEngine
    {
        private readonly AppConfig _cfg;

        public ArchiveEngine(AppConfig cfg)
        {
            _cfg = cfg;
        }

        public ArchiveResult RunJob(JobConfig job, bool dryRun)
        {
            DateTime started = DateTime.Now;
            ArchiveResult res = new ArchiveResult();
            res.Job = job.Name;

            string localRoot = job.LocalDir;
            if (!Path.IsPathRooted(localRoot)) localRoot = Util.CombineLocal(_cfg.BaseDir, localRoot);

            string remoteRoot = Util.CombineRemote(_cfg.Server.RemoteRoot, job.RemoteDir);
            string manifestPath = Manifest.PathFor(_cfg, remoteRoot);
            string stamp = started.ToString("yyyyMMdd-HHmmss");

            Log.Info("[archive:" + job.Name + "] 开始：服务器 " + remoteRoot + " -> 本地 " + localRoot + (dryRun ? "（演练）" : ""));

            FtpClient ftp = new FtpClient(_cfg.Server);
            try
            {
                ftp.Connect();
                ftp.Login();
                Manifest manifest = Manifest.Load(ftp, manifestPath, _cfg.StateDirAbs);

                Dictionary<string, RemoteEntry> remote = RemoteTree.Walk(ftp, remoteRoot, SkipMetaDirs(job));
                List<string> rels = new List<string>();
                foreach (KeyValuePair<string, RemoteEntry> kv in remote)
                    if (!kv.Value.IsDir) rels.Add(kv.Key);
                rels.Sort(StringComparer.OrdinalIgnoreCase);

                if (rels.Count == 0)
                {
                    res.Message = "服务器上没有待归档的文件";
                    Log.Info("[archive:" + job.Name + "] " + res.Message);
                    return res;
                }
                Log.Info("[archive:" + job.Name + "] 待归档文件 " + rels.Count + " 个 / " + Util.FormatBytes(TotalSize(remote, rels)));

                if (!dryRun) Directory.CreateDirectory(localRoot);

                foreach (string rel in rels)
                {
                    RemoteEntry e = remote[rel];
                    string target = Path.Combine(localRoot, rel.Replace('/', Path.DirectorySeparatorChar));
                    string absRemote = Util.CombineRemote(remoteRoot, rel);

                    if (dryRun)
                    {
                        res.Downloaded++;
                        res.Bytes += e.Size;
                        Log.Info("[archive:" + job.Name + "] 演练-下载 " + rel + "（" + Util.FormatBytes(e.Size) + "）");
                        continue;
                    }

                    string part = target + ".part";
                    try
                    {
                        long n = ftp.DownloadFile(absRemote, part, null);
                        if (e.Size >= 0 && n != e.Size)
                            throw new IOException("下载大小不符：期望 " + e.Size + "，实际 " + n);

                        bool duplicated = false;
                        if (File.Exists(target) && SameContent(target, part))
                        {
                            duplicated = true;
                            File.Delete(part);
                        }
                        else
                        {
                            if (File.Exists(target))
                            {
                                string backup = Path.Combine(localRoot, "_history", stamp, rel.Replace('/', Path.DirectorySeparatorChar));
                                Directory.CreateDirectory(Path.GetDirectoryName(backup));
                                if (File.Exists(backup)) File.Delete(backup);
                                File.Move(target, backup);
                            }
                            Directory.CreateDirectory(Path.GetDirectoryName(target));
                            if (File.Exists(target)) File.Delete(target);
                            File.Move(part, target);
                        }

                        ftp.DeleteFile(absRemote);
                        res.Deleted++;
                        res.Downloaded++;
                        res.Bytes += n;
                        if (duplicated) res.Duplicate++;
                        manifest.Add(rel, n, DateTime.UtcNow);
                        Log.Info("[archive:" + job.Name + "] 归档 " + rel + "（" + Util.FormatBytes(n) + "）" +
                                 (duplicated ? "，本地已有相同内容" : ""));
                    }
                    catch (Exception ex)
                    {
                        res.Errors++;
                        try { if (File.Exists(part)) File.Delete(part); } catch { }
                        Log.Error("[archive:" + job.Name + "] 归档失败（服务器文件保留）：" + rel + "：" + ex.Message);
                        if (!ftp.Connected) { try { ftp.Reconnect(); } catch { } }
                    }
                }

                if (!dryRun) manifest.Save(ftp, _cfg.StateDirAbs);
            }
            catch (FtpNoEncryptionException ex)
            {
                res.PolicySkipped = true;
                res.Message = "服务器未启用加密，按配置跳过";
                Log.Warn("[archive:" + job.Name + "] " + res.Message + "：" + ex.Message);
            }
            catch (Exception ex)
            {
                res.Errors++;
                res.Aborted = true;
                res.Message = ex.Message;
                Log.Error("[archive:" + job.Name + "] 中止：" + ex.GetType().Name + " " + ex.Message);
            }
            finally
            {
                try { ftp.Dispose(); } catch { }
            }

            res.Elapsed = DateTime.Now - started;
            Log.Info("[archive:" + job.Name + "] 结束：" + res.Summary());
            return res;
        }

        private static long TotalSize(Dictionary<string, RemoteEntry> map, List<string> rels)
        {
            long sum = 0;
            foreach (string rel in rels)
            {
                RemoteEntry e = map[rel];
                if (e.Size > 0) sum += e.Size;
            }
            return sum;
        }

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

        /// <summary>逐块比较两个文件内容是否一致（避免大文件一次性读入内存）。</summary>
        private static bool SameContent(string a, string b)
        {
            try
            {
                FileInfo fa = new FileInfo(a);
                FileInfo fb = new FileInfo(b);
                if (fa.Length != fb.Length) return false;
                using (FileStream sa = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (FileStream sb = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    byte[] ba = new byte[65536];
                    byte[] bb = new byte[65536];
                    while (true)
                    {
                        int na = sa.Read(ba, 0, ba.Length);
                        int nb = sb.Read(bb, 0, bb.Length);
                        if (na != nb) return false;
                        if (na <= 0) return true;
                        for (int i = 0; i < na; i++) if (ba[i] != bb[i]) return false;
                    }
                }
            }
            catch { return false; }
        }
    }
}
