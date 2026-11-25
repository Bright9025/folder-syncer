using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace SyncAgent
{
    internal static class Log
    {
        private static readonly object Sync = new object();
        private static string _dir;
        private static int _keepDays = 30;
        private static readonly List<string> Recent = new List<string>();

        public static bool Verbose = false;
        public static string CurrentFile = "";

        public static void Init(string dir, int keepDays, bool verbose)
        {
            lock (Sync)
            {
                _dir = dir;
                _keepDays = keepDays;
                Verbose = verbose;
                try { Directory.CreateDirectory(_dir); } catch { }
                CurrentFile = Path.Combine(_dir, "sync-" + DateTime.Now.ToString("yyyyMMdd") + ".log");
                Prune();
            }
        }

        private static void Prune()
        {
            try
            {
                if (_keepDays <= 0 || !Directory.Exists(_dir)) return;
                DateTime limit = DateTime.Now.AddDays(-_keepDays);
                foreach (string f in Directory.GetFiles(_dir, "sync-*.log"))
                {
                    try { if (File.GetLastWriteTime(f) < limit) File.Delete(f); } catch { }
                }
            }
            catch { }
        }

        private static void Write(string level, string msg)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [" + level + "] " + msg;
            lock (Sync)
            {
                if (Recent.Count > 400) Recent.RemoveRange(0, 200);
                Recent.Add(line);
                try
                {
                    if (!string.IsNullOrEmpty(_dir))
                    {
                        Directory.CreateDirectory(_dir);
                        File.AppendAllText(CurrentFile, line + "\r\n", new UTF8Encoding(false));
                    }
                }
                catch { }
            }
        }

        public static void Info(string msg) { Write("INFO", msg); }
        public static void Warn(string msg) { Write("WARN", msg); }
        public static void Error(string msg) { Write("ERROR", msg); }
        public static void Debug(string msg) { if (Verbose) Write("DEBUG", msg); }

        public static void Error(string msg, Exception ex)
        {
            if (ex == null) Error(msg);
            else Error(msg + " | " + ex.GetType().Name + ": " + ex.Message);
        }

        public static string[] RecentLines(int max)
        {
            lock (Sync)
            {
                int n = Math.Min(max, Recent.Count);
                string[] res = new string[n];
                Recent.CopyTo(Recent.Count - n, res, 0, n);
                return res;
            }
        }

        public static void ResetRecent()
        {
            lock (Sync) Recent.Clear();
        }
    }
}
