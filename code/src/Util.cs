using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace SyncAgent
{
    internal static class Util
    {
        /// <summary>把任意路径规范成 FTP 绝对路径：以 / 开头，无反斜杠，无 . / ..。</summary>
        public static string NormalizeRemote(string p)
        {
            if (string.IsNullOrEmpty(p)) return "/";
            p = p.Replace('\\', '/');
            string[] segs = p.Split('/');
            List<string> parts = new List<string>();
            foreach (string s in segs)
            {
                if (s.Length == 0 || s == ".") continue;
                if (s == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); continue; }
                parts.Add(s);
            }
            return "/" + string.Join("/", parts.ToArray());
        }

        public static string CombineRemote(string a, string b)
        {
            if (string.IsNullOrEmpty(b)) return NormalizeRemote(a);
            if (b.StartsWith("/") || b.StartsWith("\\")) return NormalizeRemote(b);
            return NormalizeRemote(NormalizeRemote(a) + "/" + b.Replace('\\', '/'));
        }

        public static string RemoteParent(string p)
        {
            p = NormalizeRemote(p);
            int i = p.LastIndexOf('/');
            if (i <= 0) return "/";
            return p.Substring(0, i);
        }

        public static string RemoteName(string p)
        {
            p = NormalizeRemote(p);
            int i = p.LastIndexOf('/');
            return i < 0 ? p : p.Substring(i + 1);
        }

        public static string CombineLocal(string a, string b)
        {
            if (Path.IsPathRooted(b)) return b;
            return Path.GetFullPath(Path.Combine(a, b));
        }

        /// <summary>把 glob 转成正则并缓存。</summary>
        private static readonly Dictionary<string, Regex> GlobCache = new Dictionary<string, Regex>(StringComparer.OrdinalIgnoreCase);
        private static readonly object GlobLock = new object();

        public static Regex GlobToRegex(string pattern)
        {
            lock (GlobLock)
            {
                Regex re;
                if (GlobCache.TryGetValue(pattern, out re)) return re;
                StringBuilder sb = new StringBuilder("^");
                for (int i = 0; i < pattern.Length; i++)
                {
                    char c = pattern[i];
                    if (c == '*')
                    {
                        if (i + 1 < pattern.Length && pattern[i + 1] == '*')
                        {
                            sb.Append(".*");
                            i++;
                        }
                        else sb.Append("[^/]*");
                    }
                    else if (c == '?') sb.Append("[^/]");
                    else sb.Append(Regex.Escape(c.ToString()));
                }
                sb.Append("$");
                re = new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                GlobCache[pattern] = re;
                return re;
            }
        }

        public static List<string> SplitList(string raw)
        {
            List<string> res = new List<string>();
            if (string.IsNullOrEmpty(raw)) return res;
            foreach (string s in raw.Split(new char[] { ';', ',', '|' }))
            {
                string t = s.Trim();
                if (t.Length > 0 && !res.Contains(t)) res.Add(t);
            }
            return res;
        }

        /// <summary>
        /// 排除规则：pattern 含 / 时匹配相对路径；不含 / 时匹配文件名；以 / 结尾视为目录前缀。
        /// </summary>
        public static bool IsExcluded(List<string> patterns, string relPath, bool isDir)
        {
            return MatchesAny(patterns, relPath, isDir);
        }

        /// <summary>relPath 是否命中 patterns 中的任意一条。</summary>
        public static bool MatchesAny(List<string> patterns, string relPath, bool isDir)
        {
            if (patterns == null || patterns.Count == 0) return false;
            string rel = relPath.Replace('\\', '/');
            int slash = rel.LastIndexOf('/');
            string name = slash < 0 ? rel : rel.Substring(slash + 1);
            string test = isDir ? rel + "/" : rel;
            for (int i = 0; i < patterns.Count; i++)
            {
                string pat = patterns[i].Replace('\\', '/');
                if (pat.EndsWith("/"))
                {
                    // 目录前缀：.git/ 或 some/dir/
                    string prefix = pat;
                    if (rel.Equals(pat.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)) return true;
                    if (rel.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
                    continue;
                }
                bool hasSlash = pat.IndexOf('/') >= 0;
                if (hasSlash)
                {
                    if (GlobToRegex(pat).IsMatch(test) || GlobToRegex(pat).IsMatch(rel)) return true;
                }
                else
                {
                    if (GlobToRegex(pat).IsMatch(name)) return true;
                }
            }
            return false;
        }

        public static string FormatBytes(long n)
        {
            if (n < 0) return "?";
            if (n < 1024) return n + " B";
            double d = n / 1024.0;
            if (d < 1024) return d.ToString("0.0", CultureInfo.InvariantCulture) + " KB";
            d /= 1024.0;
            if (d < 1024) return d.ToString("0.0", CultureInfo.InvariantCulture) + " MB";
            d /= 1024.0;
            return d.ToString("0.00", CultureInfo.InvariantCulture) + " GB";
        }

        public static string FormatSpan(TimeSpan t)
        {
            if (t.TotalSeconds < 60) return t.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s";
            return ((int)t.TotalMinutes) + "m" + t.Seconds + "s";
        }

        /// <summary>FTP 命令参数里不能出现 CR LF，做一次清洗。</summary>
        public static string SanitizeArg(string s)
        {
            if (s == null) return "";
            return s.Replace("\r", "").Replace("\n", "");
        }

        public static string Sha256Hex(byte[] data)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                byte[] h = sha.ComputeHash(data);
                StringBuilder sb = new StringBuilder(h.Length * 2);
                foreach (byte b in h) sb.Append(b.ToString("X2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        /// <summary>读取一个「可能带 BOM、可能是 UTF-8 或 GBK」的文本文件。</summary>
        public static string ReadTextAuto(string path)
        {
            byte[] all = File.ReadAllBytes(path);
            int off = 0;
            if (all.Length >= 3 && all[0] == 0xEF && all[1] == 0xBB && all[2] == 0xBF) off = 3;
            else if (all.Length >= 2 && all[0] == 0xFF && all[1] == 0xFE) return Encoding.Unicode.GetString(all, 2, all.Length - 2);
            else if (all.Length >= 2 && all[0] == 0xFE && all[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(all, 2, all.Length - 2);
            byte[] body = new byte[all.Length - off];
            Buffer.BlockCopy(all, off, body, 0, body.Length);
            try
            {
                return new UTF8Encoding(false, true).GetString(body);
            }
            catch
            {
                try { return Encoding.GetEncoding(936).GetString(body); }
                catch { return Encoding.UTF8.GetString(body); }
            }
        }

        public static string ToLocalTimeText(DateTime utc)
        {
            if (utc.Kind == DateTimeKind.Utc) return utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            return utc.ToString("yyyy-MM-dd HH:mm:ss");
        }
    }
}
