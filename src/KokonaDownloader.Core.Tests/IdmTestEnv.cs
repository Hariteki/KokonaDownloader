using KokonaDownloader.Core.Engine;

namespace KokonaDownloader.Core.Tests;

/// <summary>
/// 自研 IDM 协议引擎的测试脚手架。
/// 与既有测试的区别只有一处：<see cref="EngineConfig.UseIdmProtocol"/> = true，
/// 让 HTTP/HTTPS/FTP 走 IDM 分段引擎（BT/磁力仍由 aria2 负责）。
/// 既有 23 个测试文件保持默认 false，继续锁住 aria2 路径的语义不被本次改造带偏。
/// </summary>
internal static class IdmTestEnv
{
    /// <summary>构造一份启用 IDM 协议的引擎配置（aria2 仍会启动，轮询/事件链依赖它）。</summary>
    public static EngineConfig Config(string workDir, string downloadDir, int maxConcurrent = 3, int connections = 4)
        => new()
        {
            UseIdmProtocol = true,
            Aria2Path = TestEnv.Aria2Path,
            WorkDir = workDir,
            DefaultDownloadDir = downloadDir,
            RpcPort = TestEnv.GetFreePort(),
            RpcSecret = "idm-test-secret",
            MaxConcurrentDownloads = maxConcurrent,
            DefaultConnections = connections,
            BtEnabled = false,
            // 断点目录指到临时工作区，避免测试污染真实的 %APPDATA%\KokonaDownloader\parts
            IdmPartsDir = Path.Combine(workDir, "parts"),
        };

    /// <summary>建一个启用 IDM 协议的引擎（调用方负责 DisposeAsync）。</summary>
    public static DownloadEngine CreateEngine(out TaskStore store, out string workDir, out string downloadDir,
        Action<string>? log = null)
    {
        workDir = TestEnv.NewWorkDir();
        downloadDir = Path.Combine(workDir, "dl");
        Directory.CreateDirectory(downloadDir);
        store = new TaskStore(Path.Combine(workDir, "tasks.json"));
        return new DownloadEngine(Config(workDir, downloadDir), store, log);
    }

    /// <summary>轮询等待条件成立，超时抛 TimeoutException（测试里比 Task.Delay 更稳）。</summary>
    public static async Task WaitUntilAsync(Func<bool> predicate, int timeoutMs = 15000, string? because = null)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (predicate()) return;
            await Task.Delay(80).ConfigureAwait(false);
        }
        throw new TimeoutException($"等待超时（{timeoutMs}ms）：{because ?? "条件未成立"}");
    }
}
