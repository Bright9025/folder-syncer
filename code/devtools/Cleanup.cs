using System;
using System.Collections.Generic;
using System.IO;
using FolderSyncer;

// 开发用：按 FTP 绝对路径递归删除服务器上的目录/文件（复用主程序的 FTP 客户端，UTF-8 安全）
// 用法：Cleanup.exe <config.ini> <远程路径1> [远程路径2] ...
internal static class CleanupMain
{
    private static void Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("usage: Cleanup.exe <config.ini> <remotePath> [...]");
            return;
        }
        AppConfig cfg = AppConfig.Load(args[0]);
        if (cfg.Errors.Count > 0)
        {
            foreach (string e in cfg.Errors) Console.WriteLine("config error: " + e);
            return;
        }
        Log.Init(cfg.LogDirAbs, 1, false);
        using (FtpClient ftp = new FtpClient(cfg.Server))
        {
            ftp.Connect();
            ftp.Login();
            for (int i = 1; i < args.Length; i++)
            {
                Console.WriteLine("removing " + args[i]);
                DeleteTree(ftp, args[i]);
            }
            Console.WriteLine("done");
        }
    }

    private static void DeleteTree(FtpClient ftp, string absPath)
    {
        List<RemoteEntry> list;
        try { list = ftp.ListDir(absPath); }
        catch (FtpException ex)
        {
            // 不是目录，或者不存在：尝试当文件删
            try { ftp.DeleteFile(absPath); Console.WriteLine("  deleted file " + absPath); }
            catch (Exception e2) { Console.WriteLine("  skip (" + ex.Code + "/" + e2.Message + ") " + absPath); }
            return;
        }
        foreach (RemoteEntry e in list)
        {
            string child = Util.CombineRemote(absPath, e.Name);
            if (e.IsDir) DeleteTree(ftp, child);
            else
            {
                try { ftp.DeleteFile(child); Console.WriteLine("  deleted " + child); }
                catch (Exception ex) { Console.WriteLine("  FAILED " + child + ": " + ex.Message); }
            }
        }
        try { ftp.RemoveDir(absPath); Console.WriteLine("  removed dir " + absPath); }
        catch (Exception ex) { Console.WriteLine("  FAILED rmd " + absPath + ": " + ex.Message); }
    }
}
