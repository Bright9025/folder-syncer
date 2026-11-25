using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace FolderSyncer
{
    internal sealed class ManifestEntry
    {
        public string Rel = "";
        public long Size;
        public DateTime ArchivedAtUtc = DateTime.MinValue;
    }

    /// <summary>
    /// 服务器上的「已归档清单」：保存端把文件拉回本地后，把路径记到这里，
    /// 上传端看到清单里已有的文件就不再重传（即使服务器上已经被删掉了）。
    /// 文件存在服务器 &lt;manifest_dir&gt;/&lt;远端目录名&gt;.tsv。
    /// </summary>
    internal sealed class Manifest
    {
        private readonly Dictionary<string, ManifestEntry> _map =
            new Dictionary<string, ManifestEntry>(StringComparer.OrdinalIgnoreCase);

        public string RemotePath = "";
        public bool Loaded;
        public int Count { get { return _map.Count; } }

        public bool Contains(string rel)
        {
            return _map.ContainsKey(Normalize(rel));
        }

        public void Add(string rel, long size, DateTime archivedAtUtc)
        {
            ManifestEntry e = new ManifestEntry();
            e.Rel = Normalize(rel);
            e.Size = size;
            e.ArchivedAtUtc = archivedAtUtc;
            _map[e.Rel] = e;
        }

        private static string Normalize(string rel)
        {
            return rel.Replace('\\', '/').TrimStart('/');
        }

        /// <summary>由远端目录算出清单名：/课件 -> 课件；/ -> root；/a/b -> a_b</summary>
        public static string KeyForRemote(string remoteAbs)
        {
            string p = Util.NormalizeRemote(remoteAbs).Trim('/');
            if (p.Length == 0) return "root";
            return p.Replace('/', '_');
        }

        public static string PathFor(AppConfig cfg, string remoteAbs)
        {
            return Util.CombineRemote(cfg.Server.ManifestDir, KeyForRemote(remoteAbs) + ".tsv");
        }

        public static Manifest Load(FtpClient ftp, string remotePath, string workDir)
        {
            Manifest m = new Manifest();
            m.RemotePath = remotePath;
            Directory.CreateDirectory(workDir);
            string tmp = Path.Combine(workDir, "manifest-" + Guid.NewGuid().ToString("N") + ".tsv");
            try
            {
                if (ftp.Size(remotePath) < 0)
                {
                    Log.Debug("服务器上没有清单 " + remotePath + "（当作空清单）");
                    m.Loaded = true;
                    return m;
                }
                ftp.DownloadFile(remotePath, tmp, null);
                foreach (string line in File.ReadAllLines(tmp, Encoding.UTF8))
                {
                    if (line.Length == 0 || line[0] == '#') continue;
                    string[] p = line.Split('\t');
                    if (p.Length < 2) continue;
                    long size;
                    long.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out size);
                    DateTime at = DateTime.MinValue;
                    if (p.Length >= 3)
                    {
                        long ticks;
                        if (long.TryParse(p[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out ticks) && ticks > 0)
                            at = new DateTime(ticks, DateTimeKind.Utc);
                    }
                    m.Add(p[0], size, at);
                }
                m.Loaded = true;
                Log.Info("已归档清单 " + remotePath + " 共 " + m.Count + " 条");
            }
            catch (Exception ex)
            {
                Log.Warn("读取归档清单失败（按空清单继续）：" + ex.Message);
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
            return m;
        }

        public void Save(FtpClient ftp, string workDir)
        {
            Directory.CreateDirectory(workDir);
            string tmp = Path.Combine(workDir, "manifest-" + Guid.NewGuid().ToString("N") + ".tsv");
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.Append("# FolderSyncer manifest v1\r\n");
                List<ManifestEntry> list = new List<ManifestEntry>(_map.Values);
                list.Sort(delegate(ManifestEntry a, ManifestEntry b)
                {
                    return string.Compare(a.Rel, b.Rel, StringComparison.OrdinalIgnoreCase);
                });
                foreach (ManifestEntry e in list)
                {
                    sb.Append(e.Rel).Append('\t').Append(e.Size.ToString(CultureInfo.InvariantCulture))
                      .Append('\t').Append(e.ArchivedAtUtc.Ticks.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
                }
                File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                ftp.EnsureRemoteDir(Util.RemoteParent(RemotePath));
                ftp.UploadFile(tmp, RemotePath, null);
                Log.Info("已保存归档清单 " + RemotePath + " 共 " + Count + " 条");
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }
    }
}
