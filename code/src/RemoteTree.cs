using System;
using System.Collections.Generic;

namespace SyncAgent
{
    internal static class RemoteTree
    {
        /// <summary>递归列出远端目录（含子目录），返回 相对路径 -> 条目。目录不存在时返回空表。</summary>
        public static Dictionary<string, RemoteEntry> Walk(FtpClient ftp, string root)
        {
            Dictionary<string, RemoteEntry> map = new Dictionary<string, RemoteEntry>(StringComparer.OrdinalIgnoreCase);
            WalkLevel(ftp, root, "", map, 0, null);
            return map;
        }

        public static Dictionary<string, RemoteEntry> Walk(FtpClient ftp, string root, Predicate<string> skipRel)
        {
            Dictionary<string, RemoteEntry> map = new Dictionary<string, RemoteEntry>(StringComparer.OrdinalIgnoreCase);
            WalkLevel(ftp, root, "", map, 0, skipRel);
            return map;
        }

        private static void WalkLevel(FtpClient ftp, string absDir, string relPrefix,
                                      Dictionary<string, RemoteEntry> map, int depth, Predicate<string> skipRel)
        {
            if (depth > 64) return;
            if (map.Count > 500000) return;

            List<RemoteEntry> list;
            try
            {
                list = ftp.ListDir(absDir);
            }
            catch (FtpException ex)
            {
                if (ex.Code == 550) return;
                throw;
            }

            List<string> subDirs = new List<string>();
            foreach (RemoteEntry e in list)
            {
                if (e.IsDir)
                {
                    if (string.Equals(e.Name, ".", StringComparison.Ordinal) || string.Equals(e.Name, "..", StringComparison.Ordinal)) continue;
                    e.Size = 0;
                }
                else if (e.Size < 0)
                {
                    try { e.Size = ftp.Size(Util.CombineRemote(absDir, e.Name)); }
                    catch { e.Size = -1; }
                }

                string rel = relPrefix.Length == 0 ? e.Name : relPrefix + "/" + e.Name;
                if (skipRel != null && skipRel(rel)) continue;

                map[rel] = e;
                if (e.IsDir) subDirs.Add(e.Name);
            }

            foreach (string d in subDirs)
            {
                string rel = relPrefix.Length == 0 ? d : relPrefix + "/" + d;
                WalkLevel(ftp, Util.CombineRemote(absDir, d), rel, map, depth + 1, skipRel);
            }
        }
    }
}
