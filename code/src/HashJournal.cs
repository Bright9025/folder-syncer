using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace FolderSyncer
{
    internal sealed class HashEntry
    {
        public string Rel = "";
        public long Size;
        public string Hash = "";
    }

    /// <summary>
    /// 上传端写在服务器上的「待归档文件哈希清单」：文件上传成功后记下 SHA256，
    /// 保存端下载后用同一算法验证，一致才删除服务器副本。
    /// 文件位于 &lt;manifest_dir&gt;/hash-&lt;远端目录名&gt;.tsv。
    /// </summary>
    internal sealed class HashJournal
    {
        private readonly Dictionary<string, HashEntry> _map =
            new Dictionary<string, HashEntry>(StringComparer.OrdinalIgnoreCase);

        public string RemotePath = "";
        public bool Changed;
        public int Count { get { return _map.Count; } }

        public static string PathFor(AppConfig cfg, string remoteAbs)
        {
            return Util.CombineRemote(cfg.Server.ManifestDir, "hash-" + Manifest.KeyForRemote(remoteAbs) + ".tsv");
        }

        public bool TryGet(string rel, out long size, out string hash)
        {
            HashEntry e;
            if (_map.TryGetValue(Norm(rel), out e))
            {
                size = e.Size;
                hash = e.Hash;
                return true;
            }
            size = 0;
            hash = "";
            return false;
        }

        public void Set(string rel, long size, string hash)
        {
            HashEntry e = new HashEntry();
            e.Rel = Norm(rel);
            e.Size = size;
            e.Hash = hash == null ? "" : hash.Trim().ToUpperInvariant();
            _map[e.Rel] = e;
            Changed = true;
        }

        public void Remove(string rel)
        {
            if (_map.Remove(Norm(rel))) Changed = true;
        }

        private static string Norm(string rel)
        {
            return rel.Replace('\\', '/').TrimStart('/');
        }

        public static HashJournal Load(FtpClient ftp, string remotePath, string workDir)
        {
            HashJournal j = new HashJournal();
            j.RemotePath = remotePath;
            string tmp = Path.Combine(workDir, "hash-" + Guid.NewGuid().ToString("N") + ".tsv");
            try
            {
                if (ftp.Size(remotePath) < 0)
                {
                    Log.Debug("服务器上没有哈希清单 " + remotePath + "（跳过哈希校验）");
                    return j;
                }
                Directory.CreateDirectory(workDir);
                ftp.DownloadFile(remotePath, tmp, null);
                foreach (string line in File.ReadAllLines(tmp, Encoding.UTF8))
                {
                    if (line.Length == 0 || line[0] == '#') continue;
                    string[] p = line.Split('\t');
                    if (p.Length < 3) continue;
                    long size;
                    long.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out size);
                    j.Set(p[0], size, p[2]);
                }
                j.Changed = false;
                Log.Info("哈希清单 " + remotePath + " 共 " + j.Count + " 条");
            }
            catch (Exception ex)
            {
                Log.Warn("读取哈希清单失败（本次跳过哈希校验）：" + ex.Message);
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
            return j;
        }

        public void Save(FtpClient ftp, string workDir)
        {
            try
            {
                Directory.CreateDirectory(workDir);
                StringBuilder sb = new StringBuilder();
                sb.Append("# FolderSyncer hash v1\r\n");
                List<HashEntry> list = new List<HashEntry>(_map.Values);
                list.Sort(delegate(HashEntry a, HashEntry b)
                {
                    return string.Compare(a.Rel, b.Rel, StringComparison.OrdinalIgnoreCase);
                });
                foreach (HashEntry e in list)
                {
                    sb.Append(e.Rel).Append('\t').Append(e.Size.ToString(CultureInfo.InvariantCulture))
                      .Append('\t').Append(e.Hash).Append("\r\n");
                }
                string tmp = Path.Combine(workDir, "hash-" + Guid.NewGuid().ToString("N") + ".tsv");
                File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                try
                {
                    ftp.EnsureRemoteDir(Util.RemoteParent(RemotePath));
                    ftp.UploadFile(tmp, RemotePath, null);
                    Log.Info("已保存哈希清单 " + RemotePath + " 共 " + Count + " 条");
                }
                finally
                {
                    try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("保存哈希清单失败：" + ex.Message);
            }
        }
    }
}
