using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace FolderSyncer
{
    internal sealed class RedoEntry
    {
        public string Rel = "";
        public string Hash = "";
        public int Attempts;
        public DateTime LastFailUtc = DateTime.MinValue;
    }

    /// <summary>
    /// 保存端写的「需要重传」清单：某文件两次下载后哈希都不符时记在这里，
    /// 上传端下次运行会强制重传它，归档成功后由保存端删除该条。
    /// 文件位于 &lt;manifest_dir&gt;/redo-&lt;远端目录名&gt;.tsv。
    /// </summary>
    internal sealed class RedoList
    {
        private readonly Dictionary<string, RedoEntry> _map =
            new Dictionary<string, RedoEntry>(StringComparer.OrdinalIgnoreCase);

        public string RemotePath = "";
        public bool Changed;
        public int Count { get { return _map.Count; } }

        public static string PathFor(AppConfig cfg, string remoteAbs)
        {
            return Util.CombineRemote(cfg.Server.ManifestDir, "redo-" + Manifest.KeyForRemote(remoteAbs) + ".tsv");
        }

        private static string Norm(string rel)
        {
            return rel.Replace('\\', '/').TrimStart('/');
        }

        public bool Contains(string rel)
        {
            return _map.ContainsKey(Norm(rel));
        }

        public int Attempts(string rel)
        {
            RedoEntry e;
            return _map.TryGetValue(Norm(rel), out e) ? e.Attempts : 0;
        }

        public void Set(string rel, string hash, int attempts)
        {
            RedoEntry e = new RedoEntry();
            e.Rel = Norm(rel);
            e.Hash = hash == null ? "" : hash.Trim().ToUpperInvariant();
            e.Attempts = attempts;
            e.LastFailUtc = DateTime.UtcNow;
            _map[e.Rel] = e;
            Changed = true;
        }

        public void Remove(string rel)
        {
            if (_map.Remove(Norm(rel))) Changed = true;
        }

        public static RedoList Load(FtpClient ftp, string remotePath, string workDir)
        {
            RedoList list = new RedoList();
            list.RemotePath = remotePath;
            string tmp = Path.Combine(workDir, "redo-" + Guid.NewGuid().ToString("N") + ".tsv");
            try
            {
                if (ftp.Size(remotePath) < 0) return list;
                Directory.CreateDirectory(workDir);
                ftp.DownloadFile(remotePath, tmp, null);
                foreach (string line in File.ReadAllLines(tmp, Encoding.UTF8))
                {
                    if (line.Length == 0 || line[0] == '#') continue;
                    string[] p = line.Split('\t');
                    if (p.Length < 2) continue;
                    int attempts = 0;
                    if (p.Length >= 3) int.TryParse(p[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out attempts);
                    list.Set(p[0], p[1], attempts);
                }
                list.Changed = false;
                if (list.Count > 0) Log.Warn("服务器上有 " + list.Count + " 个文件被标记为需要重传");
            }
            catch (Exception ex)
            {
                Log.Warn("读取重传清单失败（忽略）：" + ex.Message);
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
            return list;
        }

        public void Save(FtpClient ftp, string workDir)
        {
            try
            {
                Directory.CreateDirectory(workDir);
                StringBuilder sb = new StringBuilder();
                sb.Append("# FolderSyncer redo v1\r\n");
                List<RedoEntry> items = new List<RedoEntry>(_map.Values);
                items.Sort(delegate(RedoEntry a, RedoEntry b)
                {
                    return string.Compare(a.Rel, b.Rel, StringComparison.OrdinalIgnoreCase);
                });
                foreach (RedoEntry e in items)
                {
                    sb.Append(e.Rel).Append('\t').Append(e.Hash).Append('\t')
                      .Append(e.Attempts.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
                }
                string tmp = Path.Combine(workDir, "redo-" + Guid.NewGuid().ToString("N") + ".tsv");
                File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                try
                {
                    if (items.Count == 0)
                    {
                        // 没有需要重传的文件时把服务器上的清单删掉，保持干净
                        try { ftp.DeleteFile(RemotePath); } catch { }
                    }
                    else
                    {
                        ftp.EnsureRemoteDir(Util.RemoteParent(RemotePath));
                        ftp.UploadFile(tmp, RemotePath, null);
                    }
                    Log.Info("已更新重传清单 " + RemotePath + "（" + items.Count + " 条）");
                }
                finally
                {
                    try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("保存重传清单失败：" + ex.Message);
            }
        }
    }
}
