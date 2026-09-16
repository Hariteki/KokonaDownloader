using KokonaDownloader.Core.Engine;

namespace KokonaDownloader.Core.Tests;

/// <summary>
/// 断点续传与 HTTP Range 专项测试（补齐历史空白）。
///
/// 背景：旧版 TestEnv.FileServer 不实现 Range/206，aria2 恢复下载时发出的
/// `Range: bytes=a-b` 会拿到"整文件 200"，被判为 errorCode=8 Invalid range header，
/// 导致 <see cref="EngineIntegrationTests.暂停与恢复"/> 偶发失败。旧用例也只是"加任务后立刻暂停"
/// （往往在下载真正开始之前就暂停了），实际并未验证续传。
///
/// 本文件用"慢速下发 + 已下载一部分后再暂停"的方式，确定性地覆盖：
///  1. 在途暂停 → 恢复 → 服务端返回 206 → 续传至完成，且文件内容与源文件逐字节一致；
///  2. 多连接分片下载确实使用 Range（206）且内容完整。
/// </summary>
public class ResumeRangeTests : IAsyncLifetime
{
    private TestEnv.FileServer _server = null!;
    private DownloadEngine _engine = null!;
    private string _workDir = null!;
    private string _downloadDir = null!;

    public async Task InitializeAsync()
    {
        _server = new TestEnv.FileServer();
        _workDir = TestEnv.NewWorkDir();
        _downloadDir = Path.Combine(_workDir, "downloads");
        Directory.CreateDirectory(_downloadDir);

        var config = new EngineConfig
        {
            Aria2Path = TestEnv.Aria2Path,
            WorkDir = _workDir,
            DefaultDownloadDir = _downloadDir,
            RpcPort = TestEnv.GetFreePort(),
            RpcSecret = "resume-secret",
            MaxConcurrentDownloads = 3,
            DefaultConnections = 4,
            BtEnabled = false,          // 本文件只测 HTTP 链路，关掉 BT 减少无关开销
            PollIntervalMs = 200
        };
        _engine = new DownloadEngine(config, new TaskStore(Path.Combine(_workDir, "tasks.json")));
        await _engine.StartAsync();
    }

    public async Task DisposeAsync()
    {
        // InitializeAsync 中途失败时字段可能为 null：逐项判空清理，避免 NRE 中断清理导致 aria2 泄漏。
        if (_engine != null) { try { await _engine.DisposeAsync(); } catch { } }
        try { _server?.Dispose(); } catch { }
        try { if (_workDir != null) Directory.Delete(_workDir, true); } catch { }
    }

    [Fact]
    public async Task 在途暂停后恢复_服务端支持206时续传至完成且内容一致()
    {
        const int total = 4 * 1024 * 1024;
        var content = new byte[total];
        new Random(7).NextBytes(content);
        // 64KB / 100ms ≈ 640KB/s：4MB 约 6.25s，足够稳定地在"下载中"暂停；
        // 体积刻意压小——慢速下发虽已改为 async（不占线程池），但长时间挂着的请求仍会拖慢同进程其它测试。
        _server.AddFile("drip.bin", content, chunkBytes: 64 * 1024, chunkDelayMs: 100);

        var task = await _engine.AddTaskAsync(new NewTaskRequest
        {
            Urls = new List<string> { _server.Url("drip.bin") },
            Connections = 1
        });

        // 等到确实已写入一部分数据再暂停（此时暂停才有"续传"语义）
        DownloadTaskInfo? info = null;
        var deadline = DateTime.Now.AddSeconds(30);
        while (DateTime.Now < deadline)
        {
            info = await _engine.GetTaskAsync(task.Gid);
            if (info is null) break;
            if (info.State is TaskState.Completed or TaskState.Failed) break;
            if (info.CompletedLength > 0) break;
            await Task.Delay(100);
        }

        Assert.NotNull(info);
        Assert.NotEqual(TaskState.Failed, info!.State);
        Assert.True(info.CompletedLength > 0,
            $"暂停前应已有部分进度（state={info.State} completed={info.CompletedLength}）");
        Assert.True(info.CompletedLength < total,
            $"暂停前不应已完成（completed={info.CompletedLength}/{total}）——若完成说明慢速下发未生效");

        await _engine.PauseAsync(task.Gid);
        await Task.Delay(800);
        var paused = await _engine.GetTaskAsync(task.Gid);
        Assert.Equal(TaskState.Paused, paused!.State);
        var pausedBytes = paused.CompletedLength;
        Assert.True(pausedBytes > 0, "暂停时应保留已下载数据");

        var rangesBeforeResume = _server.RangeRequestCount;
        await _engine.ResumeAsync(task.Gid);

        var deadline2 = DateTime.Now.AddSeconds(90);
        while (DateTime.Now < deadline2)
        {
            info = await _engine.GetTaskAsync(task.Gid);
            if (info is null) break;
            if (info.State == TaskState.Completed) break;
            if (info.State == TaskState.Failed)
                Assert.Fail($"恢复后续传失败: errorCode={info.ErrorCode} {info.ErrorMessage}");
            await Task.Delay(200);
        }

        Assert.NotNull(info);
        Assert.Equal(TaskState.Completed, info!.State);
        Assert.Equal(total, info.TotalLength);
        Assert.True(_server.RangeRequestCount > rangesBeforeResume,
            $"恢复后应发出带 Range 的续传请求（恢复前={rangesBeforeResume} 恢复后={_server.RangeRequestCount}）");

        var bytes = await File.ReadAllBytesAsync(info.FilePath!);
        Assert.Equal(total, bytes.Length);
        Assert.True(content.SequenceEqual(bytes), "续传拼接后的文件内容必须与源文件逐字节一致");
    }

    [Fact]
    public async Task 多连接分片下载走206且内容完整()
    {
        const int total = 4 * 1024 * 1024;
        var content = new byte[total];
        new Random(11).NextBytes(content);
        // 每块之间 15ms：让多个连接有机会并行取不同区间
        _server.AddFile("split.bin", content, chunkBytes: 256 * 1024, chunkDelayMs: 15);

        var task = await _engine.AddTaskAsync(new NewTaskRequest
        {
            Urls = new List<string> { _server.Url("split.bin") },
            Connections = 4
        });

        DownloadTaskInfo? info = null;
        var deadline = DateTime.Now.AddSeconds(60);
        while (DateTime.Now < deadline)
        {
            info = await _engine.GetTaskAsync(task.Gid);
            if (info?.State == TaskState.Completed) break;
            if (info?.State == TaskState.Failed)
                Assert.Fail($"分片下载失败: errorCode={info.ErrorCode} {info.ErrorMessage}");
            await Task.Delay(200);
        }

        Assert.Equal(TaskState.Completed, info!.State);
        Assert.True(_server.RangeRequestCount > 0,
            "多连接下载应发出带 Range 的分片请求（服务端需返回 206）");
        var bytes = await File.ReadAllBytesAsync(info.FilePath!);
        Assert.True(content.SequenceEqual(bytes), "分片并行下载的内容必须完整且顺序正确");
    }
}
