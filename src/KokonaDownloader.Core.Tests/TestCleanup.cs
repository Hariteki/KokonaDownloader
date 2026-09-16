using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace KokonaDownloader.Core.Tests;

/// <summary>
/// 测试进程的兜底清理：回收遗留在测试临时目录里的 aria2c 进程，并清掉残留工作目录。
///
/// 为什么需要它：每个用例都用独立的 GUID 工作目录（<see cref="TestEnv.NewWorkDir"/>），
/// 而产品侧的 <c>Aria2Process.CleanupOrphan()</c> 只认**自己工作目录**里的 aria2.pid，
/// 因此某个用例的 aria2 一旦没被正常收掉就会永久残留——实测一次**通过**的全量运行后仍留下 5 个。
/// 后果有两层：
///   1. 残留进程一直占着内存与端口；
///   2. 它们继承了测试运行器的 stdout 管道，导致 `dotnet test … | Select-String …` 这类管道**永不返回**
///      （表现为"测试卡死十几分钟"，实际测试早就跑完了）。
///
/// 判据：只处理 %TEMP%\kokona_dl_test\ 下工作目录里的 aria2.pid，并用进程名二次确认，
/// 绝不触碰用户 %APPDATA%\KokonaDownloader\engine 下的真实引擎进程。
/// </summary>
internal static class TestCleanup
{
    private static readonly string TempRoot =
        Path.Combine(Path.GetTempPath(), "kokona_dl_test") + Path.DirectorySeparatorChar;

    [ModuleInitializer]
    internal static void Register()
    {
        try
        {
            // 上一轮崩溃/中断留下的：本次开跑前先收掉
            KillLeftoverAria2();
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                KillLeftoverAria2();
                CleanupWorkDirs();
            };
        }
        catch { /* 兜底逻辑本身不应让测试失败 */ }
    }

    /// <summary>按测试工作目录里的 aria2.pid 终止残留的 aria2c 进程，返回清理数量。</summary>
    internal static int KillLeftoverAria2()
    {
        var killed = 0;
        try
        {
            if (!Directory.Exists(TempRoot)) return 0;
            foreach (var pidFile in Directory.EnumerateFiles(TempRoot, "aria2.pid", SearchOption.AllDirectories))
            {
                try
                {
                    // 必须先读后删：先删的话 ReadAllText 必然抛异常，清理就永远不生效
                    if (!int.TryParse(File.ReadAllText(pidFile).Trim(), out var pid)) continue;
                    File.Delete(pidFile); // 读完再删，避免重复处理
                    using var p = Process.GetProcessById(pid); // 进程不在会抛 ArgumentException
                    if (p.HasExited) continue;
                    // 二次确认是 aria2，避免 PID 复用误杀
                    if (!p.ProcessName.StartsWith("aria2", StringComparison.OrdinalIgnoreCase)) continue;
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(2000);
                    killed++;
                }
                catch { /* 进程已退出 / pid 文件被删：忽略 */ }
            }
        }
        catch { }
        return killed;
    }

    /// <summary>清掉残留的测试工作目录（aria2 仍持有文件时删除会失败，故先杀进程再删）。</summary>
    internal static void CleanupWorkDirs()
    {
        try
        {
            if (!Directory.Exists(TempRoot)) return;
            foreach (var dir in Directory.EnumerateDirectories(TempRoot))
            {
                try { Directory.Delete(dir, true); } catch { /* 仍被占用则留给下次启动清理 */ }
            }
        }
        catch { }
    }
}
