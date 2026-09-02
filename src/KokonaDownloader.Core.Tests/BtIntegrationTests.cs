using System.Collections.Concurrent;
using KokonaDownloader.Core.Engine;

namespace KokonaDownloader.Core.Tests;

/// <summary>
/// BT/磁力离线回环集成测试：本机 MiniTracker + 独立 aria2c 做种进程 + 被测引擎，全程不依赖外网 DHT/tracker。
/// 关键语义：做种开启时 aria2 下载完成后保持 active(seeder=true) 即 Seeding 状态 —— 测试以 Seeding 作为“下载完成”信号；
/// 关闭做种（seed-time=0）时才会出现 Completed，用于断言终态映射。
/// </summary>
public class BtIntegrationTests : IAsyncLifetime
{
    private string _workDir = null!;
    private string _downloadDir = null!;
    private MiniTracker _tracker = null!;
    private readonly List<Aria2Seeder> _seeders = new();
    private readonly List<DownloadEngine> _engines = new();

    public Task InitializeAsync()
    {
        _workDir = TestEnv.NewWorkDir();
        _downloadDir = Path.Combine(_workDir, "downloads");
        Directory.CreateDirectory(_downloadDir);
        _tracker = new MiniTracker();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var engine in _engines) { try { await engine.DisposeAsync(); } catch { } }
        foreach (var seeder in _seeders) { try { seeder.Dispose(); } catch { } }
        _tracker.Dispose();
        try { Directory.Delete(_workDir, true); } catch { }
    }

    // ---- 测试 ----

    [Fact]
    public async Task 磁力链接两阶段下载并进入做种()
    {
        var content = new byte[512 * 1024];
        Random.Shared.NextBytes(content);
        var fx = await StartLoopbackAsync("kokona-magnet-test.bin", content);
        var engine = await StartEngineAsync(seedEnabled: true);

        var events = new ConcurrentQueue<EngineEventArgs>();
        engine.EngineEvent += (_, e) => events.Enqueue(e);

        // 阶段一：添加磁力链接 → aria2 创建 [METADATA] 元数据任务
        var magnet = BuildMagnet(fx.Torrent, _tracker.AnnounceUrl);
        var added = await engine.AddTaskAsync(new NewTaskRequest { Urls = [magnet], Directory = _downloadDir });
        Assert.True(added.IsBt);

        // 阶段二：元数据就绪后 followedBy 派生真实任务；做种开启时 complete 永不出现，Seeding 即下载完成
        var final = await PollBtTaskAsync(engine, added.Gid, fx.Torrent.Name,
            t => t.State == TaskState.Seeding, timeoutMs: 90_000);

        Assert.Equal(TaskState.Seeding, final.State);
        Assert.True(final.IsBt);
        Assert.NotEqual(added.Gid, final.Gid);          // 真实任务由 followedBy 派生，gid 不同
        Assert.Equal(fx.Torrent.NumPieces, final.NumPieces);
        Assert.Equal(content.Length, final.TotalLength);
        Assert.Equal(FullBitField(fx.Torrent.NumPieces), final.BitField);  // 分片位图全 1：方块矩阵数据源正确

        // 元数据任务已自动清理并通知 UI；真实任务按“新建任务”推送
        Assert.Contains(events, e => e.Type == "TaskRemoved" && e.Task?.Gid == added.Gid);
        Assert.Contains(events, e => e.Type == "TaskChanged" && e.IsNewTask && e.Task != null && e.Task.Gid == final.Gid);

        // 下载内容与源数据逐字节一致
        var downloaded = await File.ReadAllBytesAsync(Path.Combine(_downloadDir, fx.Torrent.Name));
        Assert.True(content.AsSpan().SequenceEqual(downloaded));
    }

    [Fact]
    public async Task 种子文件直接添加_关闭做种_完成后状态为Completed()
    {
        var content = new byte[256 * 1024 + 777];  // 非整分片长度，覆盖尾片校验
        Random.Shared.NextBytes(content);
        var fx = await StartLoopbackAsync("kokona-torrent-test.bin", content);
        var engine = await StartEngineAsync(seedEnabled: false);

        var added = await engine.AddTorrentAsync(fx.Torrent.TorrentBytes,
            new NewTaskRequest { Urls = [fx.TorrentPath], Directory = _downloadDir });
        Assert.True(added.IsBt);

        var bitFieldSeen = false;
        var final = await PollBtTaskAsync(engine, added.Gid, fx.Torrent.Name,
            t => t.State == TaskState.Completed,
            t => { if (t.BitField != null) bitFieldSeen = true; },
            timeoutMs: 90_000);

        Assert.Equal(TaskState.Completed, final.State);   // seed-time=0 → 完成即 Completed，不进入 Seeding
        Assert.True(final.IsBt);
        Assert.Equal(fx.Torrent.NumPieces, final.NumPieces);
        Assert.Equal(content.Length, final.TotalLength);
        Assert.True(bitFieldSeen);

        var downloaded = await File.ReadAllBytesAsync(Path.Combine(_downloadDir, fx.Torrent.Name));
        Assert.True(content.AsSpan().SequenceEqual(downloaded));
    }

    [Fact]
    public async Task 限速下载中分片位图渐进填充_方块矩阵数据源()
    {
        // 8MB = 16 片 × 512KB；512KB/s 限速拉长下载窗口至约 16s，轮询可稳定采到中间位图
        var pieceLength = 512 * 1024;
        var content = new byte[pieceLength * 16];
        Random.Shared.NextBytes(content);
        var fx = await StartLoopbackAsync("kokona-bitfield-test.bin", content, pieceLength);
        var engine = await StartEngineAsync(seedEnabled: false);

        var magnet = BuildMagnet(fx.Torrent, _tracker.AnnounceUrl);
        var added = await engine.AddTaskAsync(new NewTaskRequest
        {
            Urls = [magnet],
            Directory = _downloadDir,
            SpeedLimit = 512 * 1024   // max-download-limit 对 BT 同样生效
        });

        // 仅记录真实任务的位图（元数据任务的 numPieces 不同，据此过滤）
        var samples = new List<string>();
        var final = await PollBtTaskAsync(engine, added.Gid, fx.Torrent.Name,
            t => t.State == TaskState.Completed,
            t => { if (t.BitField != null && t.NumPieces == 16) samples.Add(t.BitField); },
            timeoutMs: 150_000);

        Assert.Equal(TaskState.Completed, final.State);
        Assert.Equal(16, final.NumPieces);

        // 中途确实存在“部分完成”的位图：长度正确且非全 1 → 方块矩阵可渲染渐进过程
        Assert.NotEmpty(samples);
        Assert.Contains(samples, bf => bf.Length == 4 && bf != FullBitField(16));
        // 位图按时间单调不减（已完成分片不会回退）→ 矩阵只会向前点亮
        var counts = samples.Select(BitCount).ToList();
        Assert.Equal(counts, counts.OrderBy(x => x));

        var downloaded = await File.ReadAllBytesAsync(Path.Combine(_downloadDir, fx.Torrent.Name));
        Assert.True(content.AsSpan().SequenceEqual(downloaded));
    }

    [Fact]
    public async Task SetBtTrackersAsync_热更新不抛异常()
    {
        var engine = await StartEngineAsync(seedEnabled: false);
        var ex = await Record.ExceptionAsync(() => engine.SetBtTrackersAsync("http://127.0.0.1:9/announce"));
        Assert.Null(ex);
    }

    // ---- 夹具与辅助 ----

    private sealed record LoopbackFixture(TorrentBuilder Torrent, string TorrentPath, string DataDir, Aria2Seeder Seeder);

    /// <summary>搭建回环做种源：构造 .torrent → 落盘源数据 → 启动做种进程 → 等 tracker 注册。</summary>
    private async Task<LoopbackFixture> StartLoopbackAsync(string name, byte[] content, int pieceLength = 256 * 1024)
    {
        var dataDir = Path.Combine(_workDir, "seeder");
        Directory.CreateDirectory(dataDir);
        var torrent = new TorrentBuilder(name, content, pieceLength, announceUrl: _tracker.AnnounceUrl);
        var torrentPath = Path.Combine(_workDir, $"{name}.torrent");
        await File.WriteAllBytesAsync(torrentPath, torrent.TorrentBytes);
        await File.WriteAllBytesAsync(Path.Combine(dataDir, name), content);

        var seeder = Aria2Seeder.Start(TestEnv.Aria2Path, torrentPath, dataDir, _tracker.AnnounceUrl,
            TestEnv.GetFreePort(), Path.Combine(_workDir, "seeder.log"));
        _seeders.Add(seeder);

        if (!await _tracker.WaitForPeerAsync(torrent.InfoHashHex, 20_000))
            throw new TimeoutException($"做种进程 20s 内未注册到 tracker。做种进程日志:\n{seeder.ReadLog()}");
        return new LoopbackFixture(torrent, torrentPath, dataDir, seeder);
    }

    private async Task<DownloadEngine> StartEngineAsync(bool seedEnabled)
    {
        var workDir = Path.Combine(_workDir, "engine");
        Directory.CreateDirectory(workDir);
        var config = new EngineConfig
        {
            Aria2Path = TestEnv.Aria2Path,
            WorkDir = workDir,
            DefaultDownloadDir = _downloadDir,
            RpcPort = TestEnv.GetFreePort(),
            RpcSecret = "test-secret-123",
            MaxConcurrentDownloads = 3,
            DefaultConnections = 4,
            PollIntervalMs = 300,
            BtListenPort = TestEnv.GetFreePort(),
            BtSeedEnabled = seedEnabled,
            // 做种开启时给一个测试期内永远达不到的分享率，保证 Seeding 状态稳定
            SeedRatio = seedEnabled ? 9999 : 1.0
        };
        var engine = new DownloadEngine(config, new TaskStore(Path.Combine(workDir, "tasks.json")));
        await engine.StartAsync();
        _engines.Add(engine);
        return engine;
    }

    private static string BuildMagnet(TorrentBuilder torrent, string announceUrl) =>
        $"magnet:?xt=urn:btih:{torrent.InfoHashHex}&dn={Uri.EscapeDataString(torrent.Name)}&tr={Uri.EscapeDataString(announceUrl)}";

    /// <summary>numPieces 个分片全部完成时的位图（每片 1 bit，高位在前，按字节对齐）。</summary>
    private static string FullBitField(int numPieces)
    {
        var bytes = new byte[(numPieces + 7) / 8];
        for (var i = 0; i < numPieces; i++) bytes[i / 8] |= (byte)(0x80 >> (i % 8));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static int BitCount(string hex)
    {
        var count = 0;
        foreach (var c in hex)
        {
            count += c switch
            {
                '1' or '2' or '4' or '8' => 1,
                '3' or '5' or '6' or '9' or 'a' or 'c' => 2,
                '7' or 'b' or 'd' or 'e' => 3,
                'f' => 4,
                _ => 0
            };
        }
        return count;
    }

    /// <summary>
    /// 按谓词轮询 BT 任务：优先匹配真实任务（磁力两阶段派生的新 gid），元数据阶段回退按初始 gid 匹配。
    /// 元数据任务完成后会短暂以 Completed 状态留在 tellStopped（引擎随后才移除），若优先匹配它
    /// 会与 followedBy 处理产生竞态而误判完成，故真实任务永远优先。
    /// </summary>
    private static async Task<DownloadTaskInfo> PollBtTaskAsync(
        DownloadEngine engine, string metaGid, string torrentName,
        Func<DownloadTaskInfo, bool> done, Action<DownloadTaskInfo>? observe = null, int timeoutMs = 60_000)
    {
        var deadline = DateTime.Now.AddMilliseconds(timeoutMs);
        DownloadTaskInfo? last = null;
        while (DateTime.Now < deadline)
        {
            var tasks = await engine.GetAllTasksAsync();
            last = tasks.FirstOrDefault(t => t.Gid != metaGid && t.IsBt && t.Name == torrentName)
                   ?? tasks.FirstOrDefault(t => t.Gid == metaGid);
            if (last != null) observe?.Invoke(last);
            if (last != null && done(last)) return last;
            await Task.Delay(300);
        }
        throw new TimeoutException($"BT 任务未在 {timeoutMs}ms 内达到预期状态。当前快照: {Describe(last)}");
    }

    private static string Describe(DownloadTaskInfo? t) =>
        t == null ? "(未发现任务)" : $"Gid={t.Gid} Name={t.Name} State={t.State} 进度={t.Progress:P0} 分片={t.NumPieces} BitField={t.BitField} 错误={t.ErrorMessage}";
}
