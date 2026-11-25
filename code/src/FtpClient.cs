using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace SyncAgent
{
    internal class FtpException : Exception
    {
        public readonly int Code;
        public FtpException(int code, string message) : base("FTP " + code + " " + message) { Code = code; }
        public FtpException(string message) : base(message) { Code = 0; }
    }

    internal sealed class FtpReply
    {
        public int Code;
        public readonly List<string> Lines = new List<string>();
        /// <summary>去掉 "213 " 这类状态码前缀后的内容（多行取最后一行）。</summary>
        public string Payload
        {
            get
            {
                if (Lines.Count == 0) return "";
                string l = Lines[Lines.Count - 1];
                if (l.Length > 4 && char.IsDigit(l[0]) && char.IsDigit(l[1]) && char.IsDigit(l[2]) &&
                    (l[3] == ' ' || l[3] == '-'))
                    return l.Substring(4).Trim();
                return l.Trim();
            }
        }

        public string Text
        {
            get
            {
                if (Lines.Count == 0) return "";
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < Lines.Count; i++)
                {
                    if (i > 0) sb.Append(" | ");
                    sb.Append(Lines[i]);
                }
                return sb.ToString();
            }
        }
    }

    internal sealed class FtpNoEncryptionException : FtpException
    {
        public FtpNoEncryptionException(string message) : base(message) { }
    }

    internal sealed class RemoteEntry
    {
        public string Name = "";
        public bool IsDir = false;
        public long Size = -1;
        public DateTime ModifiedUtc = DateTime.MinValue;
    }

    internal sealed class Transfer : IDisposable
    {
        public TcpClient Client;
        public Stream Stream;
        public Transfer(TcpClient c, Stream s) { Client = c; Stream = s; }
        public void Dispose()
        {
            try { if (Stream != null) Stream.Dispose(); } catch { }
            try { if (Client != null) Client.Close(); } catch { }
        }
    }

    internal sealed class FtpClient : IDisposable
    {
        private readonly ServerConfig _cfg;
        private TcpClient _ctrl;
        private Stream _ctrlStream;
        private readonly byte[] _buf = new byte[8192];
        private int _pos, _len;
        private Encoding _enc = new UTF8Encoding(false, false);
        private string _pasvHost;
        private int _pasvPort;
        private bool _tlsData;
        private string _handshakeMode;
        private readonly HashSet<string> _knownDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private int _seq;
        public string LastTlsFingerprint = "";
        public string ServerBanner = "";
        public bool SupportsMlsd = false;
        public bool SupportsMfmt = false;

        public FtpClient(ServerConfig cfg)
        {
            _cfg = cfg;
            _handshakeMode = string.IsNullOrEmpty(cfg.TlsDataHandshake) ? "after" : cfg.TlsDataHandshake.ToLowerInvariant();
        }

        public bool Connected { get { return _ctrl != null && _ctrl.Connected; } }

        public void Connect()
        {
            CloseControl();
            TcpClient c = new TcpClient();
            c.NoDelay = true;
            try
            {
                IAsyncResult ar = c.BeginConnect(_cfg.Host, _cfg.Port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(_cfg.TimeoutSec * 1000))
                    throw new IOException("连接 " + _cfg.Host + ":" + _cfg.Port + " 超时（" + _cfg.TimeoutSec + "s）");
                c.EndConnect(ar);
                c.ReceiveTimeout = _cfg.TimeoutSec * 1000;
                c.SendTimeout = _cfg.TimeoutSec * 1000;
                _ctrl = c;
                _ctrlStream = c.GetStream();
                FtpReply hello = ReadReply();
                if (hello.Code != 220) throw new FtpException(hello.Code, hello.Text);
                ServerBanner = hello.Text;
                Log.Debug("FTP 已连接：" + hello.Text);
            }
            catch
            {
                try { c.Close(); } catch { }
                _ctrl = null;
                throw;
            }
        }

        public void Login()
        {
            FtpReply authReply = null;
            if (_cfg.TlsWanted)
            {
                authReply = SendCommand("AUTH TLS");
                if (authReply.Code == 234)
                {
                    UpgradeControlTls();
                    Log.Info("控制通道已启用 TLS");
                }
                else if (_cfg.TlsRequired)
                {
                    throw new FtpException(authReply.Code,
                        "服务器拒绝 AUTH TLS（" + authReply.Text + "），但 config.ini 里 tls=require");
                }
                else
                {
                    Log.Warn("服务器未启用 TLS（AUTH TLS " + authReply.Code + " " + authReply.Text + "）");
                }
            }

            if (!IsSecure && !_cfg.AllowPlaintext)
            {
                throw new FtpNoEncryptionException(
                    "服务器未启用加密（" + (authReply == null ? "未尝试 AUTH TLS" : "AUTH TLS 返回 " + authReply.Code) +
                    "），allow_plaintext=false，本次不登录、不同步");
            }

            FtpReply r = SendCommand("USER " + _cfg.User);
            if (r.Code == 331)
            {
                r = SendCommand("PASS " + _cfg.Password);
                if (r.Code != 230) throw new FtpException(r.Code, "登录失败：" + r.Text);
            }
            else if (r.Code != 230) throw new FtpException(r.Code, "USER 失败：" + r.Text);
            Log.Info("已登录 FTP：" + _cfg.User + "@" + _cfg.Host + ":" + _cfg.Port + (IsSecure ? "（FTPS）" : "（明文）"));

            Expect("TYPE I", 200);
            if (_cfg.Utf8)
            {
                FtpReply u = SendCommand("OPTS UTF8 ON");
                if (u.Code != 200) Log.Debug("服务器不接受 OPTS UTF8 ON（" + u.Code + "）");
            }
            if (IsSecure)
            {
                _tlsData = true;
                Expect("PBSZ 0", 200);
                Expect("PROT P", 200);
            }
            ReadFeatures();
        }

        private void ReadFeatures()
        {
            try
            {
                FtpReply r = SendCommand("FEAT");
                if (r.Code != 211) return;
                string t = r.Text.ToUpperInvariant();
                SupportsMlsd = t.IndexOf("MLSD", StringComparison.Ordinal) >= 0;
                SupportsMfmt = t.IndexOf("MFMT", StringComparison.Ordinal) >= 0;
                if (SupportsMlsd) Log.Debug("服务器支持 MLSD（精确目录列表）");
                if (SupportsMfmt) Log.Debug("服务器支持 MFMT（可保留文件修改时间）");
            }
            catch (Exception ex)
            {
                Log.Debug("FEAT 失败（忽略）：" + ex.Message);
            }
        }

        public bool IsSecure { get { return _ctrlStream is SslStream; } }

        private void UpgradeControlTls()
        {
            SslStream ssl = new SslStream(_ctrlStream, false, ValidateCert);
            ssl.AuthenticateAsClient(_cfg.Host, null, AllowedProtocols, false);
            _ctrlStream = ssl;
            _pos = _len = 0;
        }

        private static SslProtocols AllowedProtocols
        {
            get { return SslProtocols.Tls12 | SslProtocols.Tls11 | SslProtocols.Tls; }
        }

        private bool ValidateCert(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors errors)
        {
            try
            {
                if (certificate != null) LastTlsFingerprint = Util.Sha256Hex(certificate.GetRawCertData());
            }
            catch { }

            if (!string.IsNullOrEmpty(_cfg.TlsFingerprint))
            {
                bool ok = string.Equals(LastTlsFingerprint, _cfg.TlsFingerprint, StringComparison.OrdinalIgnoreCase);
                if (!ok) Log.Error("服务器证书指纹不匹配：期望 " + _cfg.TlsFingerprint + "，实际 " + LastTlsFingerprint);
                return ok;
            }
            if (!_cfg.TlsVerify)
            {
                Log.Warn("已跳过证书校验（tls_verify=false），指纹=" + LastTlsFingerprint);
                return true;
            }
            if (errors != SslPolicyErrors.None) Log.Error("证书校验失败：" + errors + "，指纹=" + LastTlsFingerprint);
            return errors == SslPolicyErrors.None;
        }

        // ---------------- 控制通道 ----------------

        private string ReadLine()
        {
            MemoryStream ms = new MemoryStream(256);
            while (true)
            {
                if (_pos >= _len)
                {
                    _len = _ctrlStream.Read(_buf, 0, _buf.Length);
                    _pos = 0;
                    if (_len <= 0)
                    {
                        if (ms.Length == 0) return null;
                        break;
                    }
                }
                byte b = _buf[_pos++];
                if (b == (byte)'\n')
                {
                    byte[] raw = ms.ToArray();
                    int n = raw.Length;
                    if (n > 0 && raw[n - 1] == (byte)'\r') n--;
                    return _enc.GetString(raw, 0, n);
                }
                ms.WriteByte(b);
                if (ms.Length > 65536) throw new IOException("FTP 响应过长");
            }
            return _enc.GetString(ms.ToArray(), 0, (int)ms.Length);
        }

        private FtpReply ReadReply()
        {
            FtpReply rep = new FtpReply();
            string line = ReadLine();
            // 个别服务器会在响应之间插入空行或多余换行，直接跳过，避免应答错位
            int guard = 0;
            while (line != null && line.Length == 0 && guard++ < 10) line = ReadLine();
            if (line == null) throw new IOException("FTP 控制连接被服务器关闭（无响应）");
            rep.Lines.Add(line);
            int code;
            if (line.Length < 3 || !int.TryParse(line.Substring(0, 3), NumberStyles.Integer, CultureInfo.InvariantCulture, out code))
                throw new IOException("无法解析 FTP 响应：" + line);
            rep.Code = code;
            if (line.Length > 3 && line[3] == '-')
            {
                string prefix = code.ToString(CultureInfo.InvariantCulture) + " ";
                while (true)
                {
                    string l = ReadLine();
                    if (l == null) throw new IOException("FTP 控制连接中途关闭（多行响应未结束）");
                    rep.Lines.Add(l);
                    if (l.StartsWith(prefix, StringComparison.Ordinal)) break;
                }
            }
            return rep;
        }

        private void SendRaw(string cmd)
        {
            string safe = Util.SanitizeArg(cmd);
            byte[] data = _enc.GetBytes(safe + "\r\n");
            _ctrlStream.Write(data, 0, data.Length);
            _ctrlStream.Flush();
        }

        private FtpReply SendCommand(string cmd)
        {
            string shown = cmd;
            if (shown.StartsWith("PASS ", StringComparison.OrdinalIgnoreCase)) shown = "PASS ******";
            int n = Interlocked.Increment(ref _seq);
            Log.Debug(">> #" + n + " t" + System.Threading.Thread.CurrentThread.ManagedThreadId + " " + shown);
            try
            {
                SendRaw(cmd);
                FtpReply r = ReadReply();
                Log.Debug("<< #" + n + " t" + System.Threading.Thread.CurrentThread.ManagedThreadId + " " + r.Code + " " + r.Text);
                return r;
            }
            catch (Exception ex)
            {
                Log.Error("命令 #" + n + " '" + shown + "' 异常：" + ex.GetType().Name + " " + ex.Message);
                throw;
            }
        }

        private FtpReply Expect(string cmd, int code)
        {
            FtpReply r = SendCommand(cmd);
            if (r.Code != code) throw new FtpException(r.Code, cmd + " -> " + r.Text);
            return r;
        }

        // ---------------- 数据通道 ----------------

        private void ParsePassive()
        {
            FtpReply r = SendCommand("PASV");
            if (r.Code != 227) throw new FtpException(r.Code, "PASV 失败：" + r.Text);
            Match m = Regex.Match(r.Text, @"(\d{1,3})\s*,\s*(\d{1,3})\s*,\s*(\d{1,3})\s*,\s*(\d{1,3})\s*,\s*(\d{1,3})\s*,\s*(\d{1,3})");
            if (!m.Success) throw new FtpException("无法解析 PASV 响应：" + r.Text);
            _pasvHost = m.Groups[1].Value + "." + m.Groups[2].Value + "." + m.Groups[3].Value + "." + m.Groups[4].Value;
            _pasvPort = int.Parse(m.Groups[5].Value) * 256 + int.Parse(m.Groups[6].Value);
            if (_cfg.UseControlIp && _ctrl != null)
            {
                try { _pasvHost = ((IPEndPoint)_ctrl.Client.RemoteEndPoint).Address.ToString(); } catch { }
            }
        }

        private TcpClient ConnectData()
        {
            TcpClient c = new TcpClient();
            c.NoDelay = true;
            IAsyncResult ar = c.BeginConnect(_pasvHost, _pasvPort, null, null);
            if (!ar.AsyncWaitHandle.WaitOne(_cfg.TimeoutSec * 1000))
            {
                c.Close();
                throw new IOException("数据连接超时 " + _pasvHost + ":" + _pasvPort);
            }
            c.EndConnect(ar);
            c.ReceiveTimeout = _cfg.TimeoutSec * 1000;
            c.SendTimeout = _cfg.TimeoutSec * 1000;
            return c;
        }

        private Stream SecureData(Stream raw)
        {
            SslStream ssl = new SslStream(raw, false, ValidateCert);
            ssl.AuthenticateAsClient(_cfg.Host, null, AllowedProtocols, false);
            return ssl;
        }

        /// <summary>发起一次数据传输：先 PASV+连数据口，再发命令，最后按配置决定握手时机。</summary>
        private Transfer BeginTransfer(string command)
        {
            int attempt = 0;
            while (true)
            {
                attempt++;
                ParsePassive();
                TcpClient tcp = ConnectData();
                Stream s = tcp.GetStream();
                try
                {
                    bool secured = false;
                    if (_tlsData && _handshakeMode == "before")
                    {
                        s = SecureData(s);
                        secured = true;
                    }
                    FtpReply r = SendCommand(command);
                    if (r.Code < 100 || r.Code >= 200)
                        throw new FtpException(r.Code, command + " 被服务器拒绝：" + r.Text);
                    if (_tlsData && !secured)
                    {
                        s = SecureData(s);
                    }
                    return new Transfer(tcp, s);
                }
                catch (Exception ex)
                {
                    try { tcp.Close(); } catch { }
                    // IIS / ProFTPD 对数据通道 TLS 握手时机要求不同，自动换一种方式重试一次
                    if (_tlsData && attempt == 1)
                    {
                        string other = _handshakeMode == "after" ? "before" : "after";
                        Log.Warn("数据通道 TLS 握手（" + _handshakeMode + "）失败，改用 " + other + " 重试：" + ex.Message);
                        _handshakeMode = other;
                        Reconnect();
                        continue;
                    }
                    throw;
                }
            }
        }

        private void EndTransfer(Transfer t)
        {
            try { t.Stream.Flush(); } catch { }
            try { t.Dispose(); } catch { }
            FtpReply r = ReadReply();
            if (r.Code >= 400) throw new FtpException(r.Code, "传输结束后服务器报错：" + r.Text);
        }

        // ---------------- 常用命令 ----------------

        public void Reconnect()
        {
            try { if (_ctrl != null) { SendRaw("QUIT"); } } catch { }
            CloseControl();
            Connect();
            Login();
        }

        private void CloseControl()
        {
            try { if (_ctrlStream != null) _ctrlStream.Dispose(); } catch { }
            try { if (_ctrl != null) _ctrl.Close(); } catch { }
            _ctrlStream = null;
            _ctrl = null;
            _pos = _len = 0;
        }

        public void MkDir(string absPath)
        {
            if (_knownDirs.Contains(absPath)) return;
            FtpReply r = SendCommand("MKD " + absPath);
            if (r.Code == 257)
            {
                _knownDirs.Add(absPath);
                return;
            }
            // 已存在：IIS 返回 550
            FtpReply c = SendCommand("CWD " + absPath);
            if (c.Code == 250)
            {
                _knownDirs.Add(absPath);
                return;
            }
            throw new FtpException(r.Code, "创建目录失败 " + absPath + " -> " + r.Text);
        }

        public void EnsureRemoteDir(string absPath)
        {
            absPath = Util.NormalizeRemote(absPath);
            if (absPath == "/") return;
            if (_knownDirs.Contains(absPath)) return;
            string[] segs = absPath.Trim('/').Split('/');
            string cur = "";
            foreach (string seg in segs)
            {
                if (seg.Length == 0) continue;
                cur += "/" + seg;
                if (!_knownDirs.Contains(cur)) MkDir(cur);
            }
        }

        public List<string> ListRaw(string absDir)
        {
            return RawList("LIST " + absDir, "LIST", absDir);
        }

        private List<string> RawList(string fullCommand, string bareCommand, string absDir)
        {
            try
            {
                using (Transfer t = BeginTransfer(fullCommand))
                {
                    List<string> lines = ReadAllLines(t.Stream);
                    EndTransfer(t);
                    return lines;
                }
            }
            catch (FtpException ex)
            {
                if (ex.Code != 500 && ex.Code != 550 && ex.Code != 502) throw;
                return ListViaCwd(bareCommand, absDir);
            }
        }

        public List<RemoteEntry> ListDir(string absDir)
        {
            if (SupportsMlsd)
            {
                try
                {
                    return ParseMlsd(RawList("MLSD " + absDir, "MLSD", absDir));
                }
                catch (FtpException ex)
                {
                    if (ex.Code == 550) throw; // 目录不存在，换命令也没用
                    if (ex.Code != 500 && ex.Code != 502 && ex.Code != 504) throw;
                    Log.Debug("MLSD 不被接受（" + ex.Code + "），改用 LIST");
                    SupportsMlsd = false;
                }
            }
            return ParseList(ListRaw(absDir));
        }

        public string FeatText()
        {
            FtpReply r = SendCommand("FEAT");
            if (r.Code == 211) return r.Text;
            return "(服务器不支持 FEAT，返回 " + r.Code + ")";
        }

        private List<string> ListViaCwd(string bareCommand, string absDir)
        {
            FtpReply c = SendCommand("CWD " + absDir);
            if (c.Code != 250) throw new FtpException(c.Code, "无法进入目录 " + absDir + " -> " + c.Text);
            try
            {
                using (Transfer t = BeginTransfer(bareCommand))
                {
                    List<string> lines = ReadAllLines(t.Stream);
                    EndTransfer(t);
                    return lines;
                }
            }
            finally
            {
                try { SendCommand("CWD /"); } catch { }
            }
        }

        private List<RemoteEntry> ParseMlsd(List<string> lines)
        {
            List<RemoteEntry> res = new List<RemoteEntry>();
            foreach (string raw in lines)
            {
                if (raw == null) continue;
                string line = raw.TrimEnd();
                if (line.Length == 0) continue;
                int sp = line.IndexOf(' ');
                if (sp <= 0) continue;
                string facts = line.Substring(0, sp);
                string name = line.Substring(sp + 1);
                if (name.Length == 0) continue;

                RemoteEntry e = new RemoteEntry();
                e.Name = name;
                e.IsDir = false;
                e.Size = -1;
                foreach (string kv in facts.Split(';'))
                {
                    int eq = kv.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = kv.Substring(0, eq).Trim().ToLowerInvariant();
                    string v = kv.Substring(eq + 1).Trim();
                    if (k == "type")
                    {
                        e.IsDir = v.StartsWith("dir", StringComparison.OrdinalIgnoreCase) ||
                                  v.StartsWith("cdir", StringComparison.OrdinalIgnoreCase) ||
                                  v.StartsWith("pdir", StringComparison.OrdinalIgnoreCase);
                    }
                    else if (k == "size")
                    {
                        long n;
                        if (long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) e.Size = n;
                    }
                    else if (k == "modify")
                    {
                        e.ModifiedUtc = ParseMlsdTime(v);
                    }
                }
                if (e.Name == "." || e.Name == "..") continue;
                if (e.IsDir && e.Size < 0) e.Size = 0;
                res.Add(e);
            }
            return res;
        }

        private static DateTime ParseMlsdTime(string v)
        {
            if (v == null) return DateTime.MinValue;
            string s = v.Trim();
            int dot = s.IndexOf('.');
            if (dot > 0) s = s.Substring(0, dot);
            DateTime dt;
            if (s.Length == 14 && DateTime.TryParseExact(s, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out dt))
                return DateTime.SpecifyKind(dt, DateTimeKind.Utc);
            return DateTime.MinValue;
        }

        /// <summary>上传完成后用 MFMT 把修改时间还原成和本地一样（服务器支持时）。</summary>
        private void TrySetMtime(string remotePath, DateTime utc)
        {
            if (!SupportsMfmt) return;
            try
            {
                string stamp = utc.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
                FtpReply r = SendCommand("MFMT " + stamp + " " + remotePath);
                if (r.Code != 213 && r.Code != 200) Log.Debug("MFMT 未被接受（" + r.Code + "）" + r.Text);
            }
            catch (Exception ex)
            {
                Log.Debug("MFMT 失败（忽略）：" + ex.Message);
            }
        }

        private List<string> ReadAllLines(Stream s)
        {
            MemoryStream ms = new MemoryStream();
            byte[] buf = new byte[16384];
            while (true)
            {
                int n = s.Read(buf, 0, buf.Length);
                if (n <= 0) break;
                ms.Write(buf, 0, n);
                if (ms.Length > 64L * 1024 * 1024) throw new IOException("目录列表过大（>64MB）");
            }
            string text = _enc.GetString(ms.ToArray());
            return new List<string>(text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'));
        }

        private static readonly Regex ReDos = new Regex(
            @"^\s*(\d{1,4}[-/.]\d{1,2}[-/.]\d{1,4})\s+(\d{1,2}:\d{2}(?::\d{2})?)\s*([AaPp][Mm])?\s+(<DIR>|\d[\d,]*)\s+(.+?)\s*$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex ReUnix = new Regex(
            @"^\s*([-dl])[rwxstST-]{9}[.+@]?\s+\d+\s+(?:\S+\s+){1,3}(\d+)\s+([A-Za-z]{3}\s+\d{1,2}\s+(?:[\d:]{4,5}|\d{4}))\s+(.+?)\s*$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private List<RemoteEntry> ParseList(List<string> lines)
        {
            List<RemoteEntry> res = new List<RemoteEntry>();
            int unparsed = 0;
            string sample = null;
            foreach (string raw in lines)
            {
                if (raw == null) continue;
                string line = raw.TrimEnd();
                if (line.Length == 0) continue;
                if (line.StartsWith("total ", StringComparison.OrdinalIgnoreCase)) continue;

                RemoteEntry e = null;
                Match m = ReDos.Match(line);
                if (m.Success)
                {
                    e = new RemoteEntry();
                    e.Name = m.Groups[5].Value;
                    string sz = m.Groups[4].Value;
                    if (sz.StartsWith("<", StringComparison.Ordinal)) { e.IsDir = true; e.Size = 0; }
                    else
                    {
                        e.IsDir = false;
                        long v;
                        if (long.TryParse(sz.Replace(",", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) e.Size = v;
                    }
                    e.ModifiedUtc = TryParseDosDate(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value);
                }
                else
                {
                    Match u = ReUnix.Match(line);
                    if (u.Success)
                    {
                        e = new RemoteEntry();
                        e.Name = u.Groups[4].Value;
                        e.IsDir = u.Groups[1].Value == "d";
                        long v;
                        if (long.TryParse(u.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) e.Size = v;
                        e.ModifiedUtc = TryParseUnixDate(u.Groups[3].Value);
                    }
                }

                if (e == null)
                {
                    // 解析不了的（不同 locale 的 LIST 格式）：先把名字猜出来、大小未知，让上层去探测
                    string name = LastToken(line);
                    if (name.Length == 0) continue;
                    if (sample == null) sample = line;
                    unparsed++;
                    e = new RemoteEntry();
                    e.Name = name;
                    e.IsDir = false;
                    e.Size = -1;
                }

                if (e.Name == "." || e.Name == "..") continue;
                if (e.Name.Length == 0) continue;
                res.Add(e);
            }
            if (unparsed > 0)
                Log.Warn("有 " + unparsed + " 行目录列表无法识别（不同服务器 LIST 格式差异），已按文件名兜底。" +
                         " 样例：" + sample + "  —— 把这一行发给开发者可以改进解析");
            return res;
        }

        private static string LastToken(string line)
        {
            string[] parts = line.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4) return "";
            return parts[parts.Length - 1];
        }

        private static DateTime TryParseDosDate(string date, string time, string ampm)
        {
            string t = time + (string.IsNullOrEmpty(ampm) ? "" : ampm.ToUpperInvariant());
            string[] fmts = new string[] { "MM-dd-yy h:mmtt", "MM-dd-yyyy h:mmtt", "MM-dd-yy H:mm", "MM-dd-yyyy H:mm", "MM-dd-yy H:mm:ss", "yyyy-MM-dd H:mm" };
            DateTime dt;
            if (DateTime.TryParseExact(date + " " + t, fmts, CultureInfo.InvariantCulture, DateTimeStyles.None, out dt))
                return DateTime.SpecifyKind(dt, DateTimeKind.Utc);
            return DateTime.MinValue;
        }

        private static DateTime TryParseUnixDate(string s)
        {
            DateTime dt;
            string[] fmts = new string[] { "MMM d HH:mm", "MMM dd HH:mm", "MMM d yyyy", "MMM dd yyyy" };
            if (DateTime.TryParseExact(s.Trim(), fmts, CultureInfo.InvariantCulture, DateTimeStyles.None, out dt))
            {
                if (s.IndexOf(':') >= 0)
                {
                    DateTime now = DateTime.UtcNow;
                    dt = new DateTime(now.Year, dt.Month, dt.Day, dt.Hour, dt.Minute, 0, DateTimeKind.Utc);
                    if (dt > now.AddDays(1)) dt = dt.AddYears(-1);
                }
                return DateTime.SpecifyKind(dt, DateTimeKind.Utc);
            }
            return DateTime.MinValue;
        }

        public long Size(string absPath)
        {
            FtpReply r = SendCommand("SIZE " + absPath);
            if (r.Code != 213) return -1;
            long v;
            if (long.TryParse(r.Payload, NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) return v;
            return -1;
        }

        public bool Exists(string absPath)
        {
            FtpReply r = SendCommand("SIZE " + absPath);
            if (r.Code == 213) return true;
            FtpReply c = SendCommand("CWD " + absPath);
            return c.Code == 250;
        }

        public bool IsDir(string absPath)
        {
            FtpReply c = SendCommand("CWD " + absPath);
            return c.Code == 250;
        }

        public void DeleteFile(string absPath)
        {
            FtpReply r = SendCommand("DELE " + absPath);
            if (r.Code != 250 && r.Code != 200) throw new FtpException(r.Code, "删除文件失败 " + absPath + " -> " + r.Text);
        }

        public void Rename(string from, string to)
        {
            FtpReply r = SendCommand("RNFR " + from);
            if (r.Code != 350) throw new FtpException(r.Code, "RNFR 失败 " + from + " -> " + r.Text);
            FtpReply r2 = SendCommand("RNTO " + to);
            if (r2.Code != 250) throw new FtpException(r2.Code, "RNTO 失败 " + to + " -> " + r2.Text);
        }

        public void RemoveDir(string absPath)
        {
            FtpReply r = SendCommand("RMD " + absPath);
            if (r.Code != 250 && r.Code != 200) throw new FtpException(r.Code, "删除目录失败 " + absPath + " -> " + r.Text);
        }

        /// <summary>上传文件：先写 .synctmp，再改名，避免中断时把服务器上的旧文件写坏。</summary>
        /// <summary>下载文件到本地（覆盖），返回写入的字节数。</summary>
        public long DownloadFile(string remotePath, string localPath, Action<long, long> progress)
        {
            long total = -1;
            try { total = Size(remotePath); } catch { }
            string dir = Path.GetDirectoryName(localPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            long done = 0;
            using (Transfer t = BeginTransfer("RETR " + remotePath))
            {
                using (FileStream fs = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    byte[] buf = new byte[65536];
                    int n;
                    while ((n = t.Stream.Read(buf, 0, buf.Length)) > 0)
                    {
                        fs.Write(buf, 0, n);
                        done += n;
                        if (progress != null && total > 0) progress(done, total);
                    }
                }
                EndTransfer(t);
            }
            return done;
        }

        public void UploadFile(string localPath, string remotePath, Action<long, long> progress)
        {
            string tmp = remotePath + ".synctmp";
            long total = new FileInfo(localPath).Length;
            try
            {
                using (Transfer t = BeginTransfer("STOR " + tmp))
                {
                    using (FileStream fs = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        byte[] buf = new byte[65536];
                        long done = 0;
                        int n;
                        while ((n = fs.Read(buf, 0, buf.Length)) > 0)
                        {
                            t.Stream.Write(buf, 0, n);
                            done += n;
                            if (progress != null && total > 0) progress(done, total);
                        }
                    }
                    EndTransfer(t);
                }
                try
                {
                    Rename(tmp, remotePath);
                }
                catch (FtpException)
                {
                    try { DeleteFile(remotePath); } catch { }
                    Rename(tmp, remotePath);
                }
                try
                {
                    DateTime utc = new FileInfo(localPath).LastWriteTimeUtc;
                    TrySetMtime(remotePath, utc);
                }
                catch { }
            }
            catch
            {
                try { DeleteFile(tmp); } catch { }
                throw;
            }
        }

        public void Dispose()
        {
            try { if (_ctrl != null) SendRaw("QUIT"); } catch { }
            CloseControl();
        }
    }

    internal static class FtpPath
    {
        public static bool IsRoot(string p) { return p == "/" || string.IsNullOrEmpty(p); }
    }
}
