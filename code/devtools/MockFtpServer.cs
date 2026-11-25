using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;

namespace MockFtp
{
    /// <summary>
    /// 仅用于开发自测的最小 IIS 风格 FTP 服务器：PASV + DOS 格式 LIST + 可选 AUTH TLS。
    /// 不作为交付物运行。
    /// </summary>
    internal static class Program
    {
        private static string _root;
        private static int _port = 2121;
        private static bool _tlsEnabled;
        private static bool _mlsdEnabled = true;
        private static X509Certificate2 _cert;
        private static readonly List<string> Users = new List<string>();

        private static void Main(string[] args)
        {
            foreach (string a in args)
            {
                if (a.StartsWith("--root=")) _root = a.Substring(7);
                else if (a.StartsWith("--port=")) _port = int.Parse(a.Substring(7), CultureInfo.InvariantCulture);
                else if (a == "--tls") _tlsEnabled = true;
                else if (a == "--no-mlsd") _mlsdEnabled = false;
            }
            if (string.IsNullOrEmpty(_root)) _root = Path.Combine(Path.GetTempPath(), "mockftp-root");
            Directory.CreateDirectory(_root);

            if (_tlsEnabled)
            {
                _cert = CreateSelfSigned();
                Console.WriteLine("TLS enabled, cert SHA256=" + Hex(_cert.GetCertHash(HashAlgorithmName.SHA256)));
            }

            TcpListener listener = new TcpListener(IPAddress.Loopback, _port);
            listener.Start();
            Console.WriteLine("mock ftp listening on 127.0.0.1:" + _port + " root=" + _root);
            while (true)
            {
                TcpClient c = listener.AcceptTcpClient();
                Thread t = new Thread(new ParameterizedThreadStart(Handle));
                t.IsBackground = true;
                t.Start(c);
            }
        }

        private static X509Certificate2 CreateSelfSigned()
        {
            RSACryptoServiceProvider rsa = new RSACryptoServiceProvider(2048);
            CertificateRequest req = new CertificateRequest("CN=mockftp.local", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            OidCollection oids = new OidCollection();
            oids.Add(new Oid("1.3.6.1.5.5.7.3.1"));
            req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(oids, false));
            X509Certificate2 tmp = req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(2));
            byte[] pfx = tmp.Export(X509ContentType.Pfx, "mockftp");
            return new X509Certificate2(pfx, "mockftp",
                X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
        }

        private static string Hex(byte[] b)
        {
            StringBuilder sb = new StringBuilder();
            foreach (byte x in b) sb.Append(x.ToString("X2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        private sealed class DataCtx
        {
            public TcpListener Listener;
            public TcpClient Client;
            public Stream Stream;
            public readonly ManualResetEvent Done = new ManualResetEvent(false);
            public bool ProtP;
            public Exception Error;
        }

        private sealed class Session
        {
            public TcpClient Control;
            public Stream CtrlStream;
            public StreamReader Reader;
            public StreamWriter Writer;
            public string Cwd = "/";
            public bool Authenticated;
            public bool ProtP;
            public bool TlsActive;
            public string RenameFrom;
            public DataCtx Pending;
            public bool UseUtf8 = true;
        }

        private static void Handle(object state)
        {
            TcpClient client = (TcpClient)state;
            Session s = new Session();
            s.Control = client;
            s.CtrlStream = client.GetStream();
            StartReaders(s);
            try
            {
                Reply(s, "220 Mock FTP Service");
                while (true)
                {
                    string line = s.Reader.ReadLine();
                    if (line == null) break;
                    Console.WriteLine(">> " + line);
                    if (!Dispatch(s, line)) break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("session error: " + ex.Message);
            }
            finally
            {
                try { if (s.Pending != null) s.Pending.Listener.Stop(); } catch { }
                try { client.Close(); } catch { }
            }
        }

        private static void StartReaders(Session s)
        {
            s.Reader = new StreamReader(s.CtrlStream, new UTF8Encoding(false), false, 4096);
            s.Writer = new StreamWriter(s.CtrlStream, new UTF8Encoding(false), 4096);
            s.Writer.NewLine = "\r\n";
            s.Writer.AutoFlush = true;
        }

        private static void Reply(Session s, string text)
        {
            s.Writer.WriteLine(text.Replace("\r", " ").Replace("\n", " "));
        }

        private static bool Dispatch(Session s, string line)
        {
            int sp = line.IndexOf(' ');
            string cmd = (sp < 0 ? line : line.Substring(0, sp)).ToUpperInvariant();
            string arg = sp < 0 ? "" : line.Substring(sp + 1).Trim();

            switch (cmd)
            {
                case "USER":
                    Reply(s, "331 Password required");
                    return true;
                case "PASS":
                    s.Authenticated = true;
                    Reply(s, "230 Logged in");
                    return true;
                case "SYST": Reply(s, "215 Windows_NT"); return true;
                case "FEAT":
                    Reply(s, "211-Extended features supported:");
                    Reply(s, " LANG EN*");
                    Reply(s, " UTF8");
                    if (_tlsEnabled)
                    {
                        Reply(s, " AUTH TLS;TLS-C;SSL;TLS-P;");
                        Reply(s, " PBSZ");
                        Reply(s, " PROT C;P;");
                    }
                    if (_mlsdEnabled) { Reply(s, " MLST type*;size*;modify*;"); Reply(s, " MLSD"); }
                    Reply(s, " SIZE");
                    Reply(s, " MDTM");
                    Reply(s, " MFMT");
                    Reply(s, " REST STREAM");
                    Reply(s, "211 END");
                    return true;
                case "OPTS":
                    Reply(s, "200 OK");
                    return true;
                case "TYPE":
                    Reply(s, "200 Type set");
                    return true;
                case "PWD":
                    Reply(s, "257 \"" + s.Cwd + "\" is current directory");
                    return true;
                case "CWD":
                    {
                        string p = Map(s, arg);
                        if (Directory.Exists(p)) { s.Cwd = NormFtp(s, arg); Reply(s, "250 Directory changed"); }
                        else Reply(s, "550 Directory not found");
                        return true;
                    }
                case "MKD":
                    {
                        string p = Map(s, arg);
                        if (Directory.Exists(p)) { Reply(s, "550 already exists"); return true; }
                        try { Directory.CreateDirectory(p); Reply(s, "257 \"" + NormFtp(s, arg) + "\" created"); }
                        catch (Exception ex) { Reply(s, "550 " + ex.Message); }
                        return true;
                    }
                case "RMD":
                    {
                        string p = Map(s, arg);
                        try { Directory.Delete(p); Reply(s, "250 removed"); }
                        catch (Exception ex) { Reply(s, "550 " + ex.Message); }
                        return true;
                    }
                case "DELE":
                    {
                        string p = Map(s, arg);
                        try { File.Delete(p); Reply(s, "250 deleted"); }
                        catch (Exception ex) { Reply(s, "550 " + ex.Message); }
                        return true;
                    }
                case "SIZE":
                    {
                        string p = Map(s, arg);
                        if (File.Exists(p)) Reply(s, "213 " + new FileInfo(p).Length.ToString(CultureInfo.InvariantCulture));
                        else Reply(s, "550 not found");
                        return true;
                    }
                case "MDTM":
                    {
                        string p = Map(s, arg);
                        if (File.Exists(p)) Reply(s, "213 " + new FileInfo(p).LastWriteTimeUtc.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture));
                        else Reply(s, "550 not found");
                        return true;
                    }
                case "RNFR":
                    {
                        string p = Map(s, arg);
                        if (File.Exists(p) || Directory.Exists(p)) { s.RenameFrom = p; Reply(s, "350 Ready for RNTO"); }
                        else Reply(s, "550 not found");
                        return true;
                    }
                case "RNTO":
                    {
                        if (string.IsNullOrEmpty(s.RenameFrom)) { Reply(s, "503 RNFR first"); return true; }
                        string p = Map(s, arg);
                        try
                        {
                            if (File.Exists(s.RenameFrom)) File.Move(s.RenameFrom, p);
                            else Directory.Move(s.RenameFrom, p);
                            Reply(s, "250 renamed");
                        }
                        catch (Exception ex) { Reply(s, "550 " + ex.Message); }
                        s.RenameFrom = null;
                        return true;
                    }
                case "PASV":
                    {
                        if (s.Pending != null) { try { s.Pending.Listener.Stop(); } catch { } }
                        TcpListener l = new TcpListener(IPAddress.Loopback, 0);
                        l.Start();
                        int port = ((IPEndPoint)l.LocalEndpoint).Port;
                        DataCtx ctx = new DataCtx();
                        ctx.Listener = l;
                        ctx.ProtP = s.ProtP;
                        Thread th = new Thread(new ParameterizedThreadStart(AcceptData));
                        th.IsBackground = true;
                        th.Start(ctx);
                        s.Pending = ctx;
                        Reply(s, string.Format(CultureInfo.InvariantCulture, "227 Entering Passive Mode (127,0,0,1,{0},{1})", port / 256, port % 256));
                        return true;
                    }
                case "AUTH":
                    if (!_tlsEnabled) { Reply(s, "534 Local policy does not allow secure connections"); return true; }
                    Reply(s, "234 AUTH TLS successful");
                    {
                        SslStream ssl = new SslStream(s.CtrlStream, false);
                        ssl.AuthenticateAsServer(_cert, false, System.Security.Authentication.SslProtocols.Tls12, false);
                        s.CtrlStream = ssl;
                        s.TlsActive = true;
                        StartReaders(s);
                    }
                    return true;
                case "PBSZ": Reply(s, "200 PBSZ=0"); return true;
                case "PROT":
                    if (arg.ToUpperInvariant() == "P") s.ProtP = true;
                    Reply(s, "200 Protection set");
                    return true;
                case "NOOP": Reply(s, "200 OK"); return true;
                case "QUIT": Reply(s, "221 Bye"); return false;
                case "LIST":
                case "NLST":
                case "MLSD":
                    if (cmd == "MLSD" && !_mlsdEnabled) { Reply(s, "500 Unknown command"); return true; }
                    return DoList(s, cmd, arg);
                case "MLST":
                    { Reply(s, "250-Listing " + arg); Reply(s, " type=file; " + arg); Reply(s, "250 End"); return true; }
                case "MFMT":
                    return DoMfmt(s, arg);
                case "STOR":
                case "RETR":
                    return DoTransfer(s, cmd, arg);
                default:
                    Reply(s, "502 Command not implemented: " + cmd);
                    return true;
            }
        }

        private static void AcceptData(object o)
        {
            DataCtx ctx = (DataCtx)o;
            try
            {
                TcpClient c = ctx.Listener.AcceptTcpClient();
                ctx.Client = c;
                Stream st = c.GetStream();
                if (ctx.ProtP)
                {
                    SslStream ssl = new SslStream(st, false);
                    ssl.AuthenticateAsServer(_cert, false, System.Security.Authentication.SslProtocols.Tls12, false);
                    st = ssl;
                }
                ctx.Stream = st;
            }
            catch (Exception ex) { ctx.Error = ex; }
            finally { ctx.Done.Set(); }
        }

        private static bool DoList(Session s, string cmd, string arg)
        {
            string p = Map(s, string.IsNullOrEmpty(arg) ? s.Cwd : arg);
            if (!Directory.Exists(p)) { Reply(s, "550 Directory not found"); return true; }
            Reply(s, "150 Opening data connection");
            DataCtx ctx = s.Pending;
            if (!ctx.Done.WaitOne(20000) || ctx.Stream == null) { Reply(s, "425 Can't open data connection"); return true; }
            try
            {
                StringBuilder sb = new StringBuilder();
                List<string> entries = new List<string>(Directory.GetFileSystemEntries(p));
                entries.Sort(StringComparer.OrdinalIgnoreCase);
                foreach (string e in entries)
                {
                    string name = Path.GetFileName(e);
                    if (cmd == "NLST") { sb.Append(name).Append("\r\n"); continue; }
                    bool isDir = Directory.Exists(e);
                    DateTime mtime = isDir ? new DirectoryInfo(e).LastWriteTimeUtc : new FileInfo(e).LastWriteTimeUtc;
                    if (cmd == "MLSD")
                    {
                        sb.Append("type=").Append(isDir ? "dir" : "file");
                        if (!isDir) sb.Append(";size=").Append(new FileInfo(e).Length.ToString(CultureInfo.InvariantCulture));
                        sb.Append(";modify=").Append(mtime.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture));
                        sb.Append("; ").Append(name).Append("\r\n");
                        continue;
                    }
                    if (isDir)
                        sb.Append(mtime.ToLocalTime().ToString("MM-dd-yy  h:mmtt", CultureInfo.InvariantCulture))
                          .Append("       <DIR>          ").Append(name).Append("\r\n");
                    else
                    {
                        FileInfo fi = new FileInfo(e);
                        sb.Append(fi.LastWriteTime.ToString("MM-dd-yy  h:mmtt", CultureInfo.InvariantCulture))
                          .Append(" ").Append(fi.Length.ToString(CultureInfo.InvariantCulture).PadLeft(18))
                          .Append(" ").Append(name).Append("\r\n");
                    }
                }
                byte[] data = new UTF8Encoding(false).GetBytes(sb.ToString());
                ctx.Stream.Write(data, 0, data.Length);
                ctx.Stream.Flush();
                ctx.Stream.Close();
                Reply(s, "226 Transfer complete");
            }
            catch (Exception ex) { Reply(s, "426 " + ex.Message); }
            finally { try { ctx.Listener.Stop(); } catch { } s.Pending = null; }
            return true;
        }

        private static bool DoTransfer(Session s, string cmd, string arg)
        {
            string p = Map(s, arg);
            if (cmd == "RETR" && !File.Exists(p)) { Reply(s, "550 not found"); return true; }
            Reply(s, "150 Opening data connection");
            DataCtx ctx = s.Pending;
            if (!ctx.Done.WaitOne(20000) || ctx.Stream == null) { Reply(s, "425 Can't open data connection"); return true; }
            try
            {
                if (cmd == "STOR")
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(p));
                    using (FileStream fs = new FileStream(p, FileMode.Create, FileAccess.Write))
                    {
                        byte[] buf = new byte[65536];
                        int n;
                        while ((n = ctx.Stream.Read(buf, 0, buf.Length)) > 0) fs.Write(buf, 0, n);
                    }
                }
                else
                {
                    using (FileStream fs = new FileStream(p, FileMode.Open, FileAccess.Read))
                    {
                        byte[] buf = new byte[65536];
                        int n;
                        while ((n = fs.Read(buf, 0, buf.Length)) > 0) ctx.Stream.Write(buf, 0, n);
                    }
                }
                ctx.Stream.Flush();
                ctx.Stream.Close();
                Reply(s, "226 Transfer complete");
            }
            catch (Exception ex) { Reply(s, "426 " + ex.Message); }
            finally { try { ctx.Listener.Stop(); } catch { } s.Pending = null; }
            return true;
        }

        private static bool DoMfmt(Session s, string arg)
        {
            int sp = arg.IndexOf(' ');
            if (sp <= 0) { Reply(s, "501 Syntax error in parameters"); return true; }
            string stamp = arg.Substring(0, sp);
            string rest = arg.Substring(sp + 1).TrimStart();
            string p = Map(s, rest);
            try
            {
                DateTime dt = DateTime.ParseExact(stamp, "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
                if (!File.Exists(p)) { Reply(s, "550 File not found"); return true; }
                File.SetLastWriteTimeUtc(p, dt);
                Reply(s, "213 Modify=" + stamp + "; " + rest);
            }
            catch (Exception ex)
            {
                Reply(s, "550 " + ex.Message);
            }
            return true;
        }

        private static string NormFtp(Session s, string arg)
        {
            string p = arg.Replace('\\', '/');
            if (!p.StartsWith("/")) p = s.Cwd.TrimEnd('/') + "/" + p;
            List<string> parts = new List<string>();
            foreach (string seg in p.Split('/'))
            {
                if (seg.Length == 0 || seg == ".") continue;
                if (seg == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); continue; }
                parts.Add(seg);
            }
            return "/" + string.Join("/", parts.ToArray());
        }

        private static string Map(Session s, string arg)
        {
            string norm = NormFtp(s, arg);
            string rel = norm.Trim('/').Replace('/', Path.DirectorySeparatorChar);
            return rel.Length == 0 ? _root : Path.Combine(_root, rel);
        }
    }
}
