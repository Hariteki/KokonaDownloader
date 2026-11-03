using System.Diagnostics;
using KokonaDownloader.Core.Engine;
using KokonaDownloader.Core.Engine.Idm;

namespace KokonaDownloader.Core.Tests;

/// <summary>
/// 卡死与"永不失败"的验收。
///
/// 背景：HttpClient 设了 <c>Timeout = Infinite</c>（大文件本来就要下很久），只有 ConnectTimeout。
/// 于是两种现实故障都不会有任何异常传出来：
///   1) 服务器发一半就吊着不关连接 → ReadAsync 永远阻塞；
///   2) 分片连接全部被拒（签名过期/403/404/磁盘写失败）→ 监视器每 250ms 重新认领、又失败。
/// 表现都是"任务永远停在下载中"，用户既不会失败也不会收到通知。
/// 修法是监视器里的"无进展看门狗"，这两个用例钉住它。
/// </summary>
public sealed class IdmRobustnessTests
{
    private static byte[] Payload(int size)
    {
        var buf = new byte[size];
        new Random(20261102).NextBytes(buf);
        return buf;
    }

    private static string[] PartsIn(string partsDir) =>
        Directory.Exists(partsDir) ? Directory.GetFiles(partsDir, "*.kdpart") : Array.Empty<string>();

    [Fact]
    public async Task StalledServer_FailsInsteadOfHangingForever()
    {
        string workDir = TestEnv.NewWorkDir();
        string dl = Path.Combine(workDir, "dl");
        string parts = Path.Combine(workDir, "parts");
        Directory.CreateDirectory(dl);

        using var server = new TestEnv.FileServer();
        // 发 64KB 之后吊住不关连接：客户端 ReadAsync 会一直等（没有读超时）
        server.AddFile("stall.bin", Payload(1024 * 1024),
            chunkBytes: 32 * 1024, chunkDelayMs: 0, stallAfterBytes: 64 * 1024);

        await using var downloader = new IdmHttpDownloader();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var opts = new IdmDownloadOptions
        {
            Url = server.Url("stall.bin"),
            Directory = dl,
            PartsDirectory = parts,
            PartKey = "stall-1",
            MaxConnections = 4,
            StallTimeout = TimeSpan.FromSeconds(1),
        };

        var sw = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<TimeoutException>(
            () => downloader.DownloadAsync(opts, null, cts.Token));
        sw.Stop();

        Assert.Contains("没有收到任何数据", ex.Message);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10),
            $"看门狗没有生效，等了 {sw.Elapsed.TotalSeconds:0.0}s");

        // 失败但断点要留着：用户可以重试接着下，而不是从 0 开始
        Assert.NotEmpty(PartsIn(parts));
        Assert.False(File.Exists(Path.Combine(dl, "stall.bin")),
            "未完成的下载不该产出一个正式文件");
    }

    [Fact]
    public async Task RangeRequestsAlwaysRejected_FailsInsteadOfRetryingForever()
    {
        string workDir = TestEnv.NewWorkDir();
        string dl = Path.Combine(workDir, "dl");
        string parts = Path.Combine(workDir, "parts");
        Directory.CreateDirectory(dl);

        using var server = new TestEnv.FileServer();
        // 首包（无 Range）照常给，之后所有分片请求一律 403 —— 对应"下载到一半链接签名过期"
        server.AddFile("expired.bin", Payload(4 * 1024 * 1024),
            chunkBytes: 32 * 1024, chunkDelayMs: 5, failRangeStatus: 403);

        await using var downloader = new IdmHttpDownloader();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var opts = new IdmDownloadOptions
        {
            Url = server.Url("expired.bin"),
            Directory = dl,
            PartsDirectory = parts,
            PartKey = "expired-1",
            MaxConnections = 4,
            // 让"连续失败上限"先于看门狗触发，测的就是这条出口
            MaxRetries = 3,
            StallTimeout = TimeSpan.FromSeconds(30),
        };

        var sw = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<IOException>(
            () => downloader.DownloadAsync(opts, null, cts.Token));
        sw.Stop();

        Assert.Contains("连续", ex.Message);
        Assert.Contains("连接失败", ex.Message);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10),
            $"一直在重试，没有停下：{sw.Elapsed.TotalSeconds:0.0}s");
        Assert.True(server.RangeRequestCount > 0, "应该真的发过 Range 请求");
        Assert.NotEmpty(PartsIn(parts));
    }

    [Fact]
    public async Task WindowsReservedName_IsPrefixed_SoFileCanBeCreated()
    {
        string workDir = TestEnv.NewWorkDir();
        string dl = Path.Combine(workDir, "dl");
        string parts = Path.Combine(workDir, "parts");
        Directory.CreateDirectory(dl);

        var payload = Payload(64 * 1024);
        using var server = new TestEnv.FileServer();
        // URL 末段就是 Windows 设备名：直接建文件会失败（或写进控制台设备）
        server.AddFile("CON", payload);

        await using var downloader = new IdmHttpDownloader();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var opts = new IdmDownloadOptions
        {
            Url = server.Url("CON"),
            Directory = dl,
            PartsDirectory = parts,
            PartKey = "reserved-1",
        };

        var result = await downloader.DownloadAsync(opts, null, cts.Token);
        string path = result.FilePath!;

        Assert.Equal("_CON", Path.GetFileName(path));
        Assert.Equal(payload, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task ServerWithoutContentLength_DownloadsSequentially()
    {
        string workDir = TestEnv.NewWorkDir();
        string dl = Path.Combine(workDir, "dl");
        string parts = Path.Combine(workDir, "parts");
        Directory.CreateDirectory(dl);

        var payload = Payload(512 * 1024);
        using var server = new TestEnv.FileServer();
        // 不发 Content-Length，走 chunked：导出接口/动态拼出来的文件就是这样
        server.AddFile("dynamic.bin", payload, chunked: true);

        await using var downloader = new IdmHttpDownloader();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var opts = new IdmDownloadOptions
        {
            Url = server.Url("dynamic.bin"),
            Directory = dl,
            PartsDirectory = parts,
            PartKey = "nolength-1",
            MaxConnections = 8,   // 未知长度只能单连接，这里确认它不会强行去分段
        };

        IdmProgress? last = null;
        var result = await downloader.DownloadAsync(opts, p => last = p, cts.Token);

        // 下载过程中总量确实是未知的（0），但**下完就知道了**：必须回填，
        // 否则界面永远显示 "0 B / 0 B、0%"，成功的任务看着像坏的。
        Assert.Equal(payload.Length, result.TotalLength);
        Assert.Equal(payload.Length, result.CompletedLength);
        Assert.NotNull(last);
        Assert.Equal(payload, await File.ReadAllBytesAsync(result.FilePath!));
        // 顺序写的核心证据：只有 1 条连接，且没有产生分段状态
        Assert.True(result.Connections <= 1);
    }

    [Fact]
    public void LowDiskSpace_FailsFastWithActionableMessage()
    {
        // 用可注入的探针模拟"只剩 100MB，但要下 4GB"，避免真的去填满一块盘
        var ex = Assert.Throws<IOException>(() =>
            IdmHttpDownloader.EnsureFreeSpace(@"C:/tmp", 4L * 1024 * 1024 * 1024, _ => 100L * 1024 * 1024));
        Assert.Contains("磁盘空间不足", ex.Message);
        Assert.Contains("GB", ex.Message);

        // 空间够：不抛
        IdmHttpDownloader.EnsureFreeSpace(@"C:/tmp", 1024, _ => 10L * 1024 * 1024);

        // 取不到容量（网络盘/配额/权限）时不要阻断下载
        IdmHttpDownloader.EnsureFreeSpace(@"C:/tmp", long.MaxValue / 2, _ => null);
        IdmHttpDownloader.EnsureFreeSpace(@"C:/tmp", long.MaxValue / 2, _ => throw new IOException("no access"));
    }

    [Fact]
    public async Task TargetPathLongerThanMaxPath_StillSaves()
    {
        string workDir = TestEnv.NewWorkDir();
        // 造一个 > 260 字符的目录：没有长路径前缀时，建目录/写文件都会失败
        string deep = Path.Combine(workDir, new string('a', 120), new string('b', 120));
        Directory.CreateDirectory(IdmHttpDownloader.LongSafe(deep));
        string parts = Path.Combine(workDir, "parts");

        var payload = Payload(64 * 1024);
        using var server = new TestEnv.FileServer();
        server.AddFile("deep.bin", payload);

        await using var downloader = new IdmHttpDownloader();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var opts = new IdmDownloadOptions
        {
            Url = server.Url("deep.bin"),
            Directory = deep,
            PartsDirectory = parts,
            PartKey = "longpath-1",
        };

        var result = await downloader.DownloadAsync(opts, null, cts.Token);

        Assert.True(result.FilePath!.Length > 260, $"路径不够长，测不到目标场景：{result.FilePath.Length}");
        // 对外报的是原始路径（不带前缀），IO 时由 LongSafe 补。
        // Windows 文件名里不可能出现 '?'，拿它当"没带 \\?\ 前缀"的判据足够。
        Assert.False(result.FilePath.Contains('?'), "对外路径不该带长路径前缀");
        string onDisk = IdmHttpDownloader.LongSafe(result.FilePath);
        Assert.True(File.Exists(onDisk));
        Assert.Equal(payload, await File.ReadAllBytesAsync(onDisk));
    }

    [Fact]
    public void ResolvePartsDirectory_RelocatesOnlyAcrossVolumes()
    {
        Assert.Equal(Path.Combine(@"D://dl", IdmHttpDownloader.RelocatedPartsDirName),
            IdmHttpDownloader.ResolvePartsDirectory(@"C://parts", @"D://dl"));
        Assert.Equal(@"C://parts", IdmHttpDownloader.ResolvePartsDirectory(@"C://parts", @"C://dl"));
        // 大小写不同不算跨卷
        Assert.Equal(@"c://parts", IdmHttpDownloader.ResolvePartsDirectory(@"c://parts", @"C://dl"));
        // 没给断点目录：直接用目标目录
        Assert.Equal(@"D://dl", IdmHttpDownloader.ResolvePartsDirectory(null, @"D://dl"));
    }

    /// <summary>找一块和临时目录不同卷的固定磁盘；只有一个盘时返回 null（用例跳过）。</summary>
    private static string? OtherVolumeRoot()
    {
        string tempRoot = (Path.GetPathRoot(Path.GetTempPath()) ?? "C:////").TrimEnd('\\');
        foreach (var d in DriveInfo.GetDrives())
        {
            if (!d.IsReady || d.DriveType != DriveType.Fixed) continue;
            if (string.Equals(d.RootDirectory.FullName.TrimEnd('\\'), tempRoot, StringComparison.OrdinalIgnoreCase))
                continue;
            return d.RootDirectory.FullName;
        }
        return null;
    }

    [Fact]
    public async Task CrossVolume_PartLivesOnTargetVolume_NotInAppData()
    {
        string? root = OtherVolumeRoot();
        if (root is null) return;   // 单盘机器没有跨卷场景

        string workDir = TestEnv.NewWorkDir();
        string parts = Path.Combine(workDir, "parts");                       // 与临时目录同卷
        string dl = Path.Combine(root, "kokona-cross-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dl);

        try
        {
            using var server = new TestEnv.FileServer();
            // 让它卡住：这样任务失败后断点会留着，才能验证它落在哪块盘上
            server.AddFile("cross.bin", Payload(1024 * 1024),
                chunkBytes: 32 * 1024, stallAfterBytes: 64 * 1024);

            await using var downloader = new IdmHttpDownloader();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var opts = new IdmDownloadOptions
            {
                Url = server.Url("cross.bin"),
                Directory = dl,
                PartsDirectory = parts,
                PartKey = "cross-1",
                StallTimeout = TimeSpan.FromSeconds(1),
            };

            await Assert.ThrowsAsync<TimeoutException>(() => downloader.DownloadAsync(opts, null, cts.Token));

            // 断点必须在目标盘：否则完成时的改名会退化成整份复制
            Assert.True(File.Exists(Path.Combine(dl, IdmHttpDownloader.RelocatedPartsDirName, "cross-1.kdpart")),
                "断点没有落在目标盘");
            Assert.False(File.Exists(Path.Combine(parts, "cross-1.kdpart")),
                "断点还留在应用数据目录，跨卷复制的问题没解决");
        }
        finally
        {
            try { Directory.Delete(dl, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task CrossVolume_CompletionRemovesTempDir_AndFileIsIntact()
    {
        string? root = OtherVolumeRoot();
        if (root is null) return;

        string workDir = TestEnv.NewWorkDir();
        string parts = Path.Combine(workDir, "parts");
        string dl = Path.Combine(root, "kokona-cross-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dl);

        try
        {
            var payload = Payload(512 * 1024);
            using var server = new TestEnv.FileServer();
            server.AddFile("cross2.bin", payload);

            await using var downloader = new IdmHttpDownloader();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var opts = new IdmDownloadOptions
            {
                Url = server.Url("cross2.bin"),
                Directory = dl,
                PartsDirectory = parts,
                PartKey = "cross-2",
            };

            var result = await downloader.DownloadAsync(opts, null, cts.Token);

            Assert.Equal(payload, await File.ReadAllBytesAsync(result.FilePath!));
            Assert.False(Directory.Exists(Path.Combine(dl, IdmHttpDownloader.RelocatedPartsDirName)),
                "任务完成后临时目录还留在下载目录里");
            // 下载目录里只剩目标文件，没有残留的中间文件
            var left = Directory.GetFiles(dl);
            Assert.Single(left);
            Assert.Equal(Path.GetFileName(result.FilePath), Path.GetFileName(left[0]));
        }
        finally
        {
            try { Directory.Delete(dl, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task BrowserUserAgentRejected_FallsBackToDefaultUa()
    {
        // 真实场景：清华/中科大镜像拒绝现代浏览器 UA（403），curl 与 IE11 UA 放行。
        // 扩展接管下载会透传 Edge 的 UA，所以必须有这次回退，否则"浏览器能下、接管后 403"。
        string workDir = TestEnv.NewWorkDir();
        string dl = Path.Combine(workDir, "dl");
        string parts = Path.Combine(workDir, "parts");
        Directory.CreateDirectory(dl);

        var payload = Payload(256 * 1024);
        using var server = new TestEnv.FileServer();
        server.AddFile("ua.bin", payload, forbidUaSubstring: "Chrome");

        await using var downloader = new IdmHttpDownloader();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var opts = new IdmDownloadOptions
        {
            Url = server.Url("ua.bin"),
            Directory = dl,
            PartsDirectory = parts,
            PartKey = "ua-1",
            MaxConnections = 4,
            Headers = new List<string>
            {
                "User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
                "Accept-Language: zh-CN",
            },
        };

        var result = await downloader.DownloadAsync(opts, null, cts.Token);

        Assert.Equal(payload, await File.ReadAllBytesAsync(result.FilePath!));
        Assert.True(server.ForbiddenCount >= 1, "没有撞到 403，测不到回退这条路");
    }

    [Fact]
    public async Task AllUserAgentsRejected_FailsInsteadOfLooping()
    {
        string workDir = TestEnv.NewWorkDir();
        string dl = Path.Combine(workDir, "dl");
        string parts = Path.Combine(workDir, "parts");
        Directory.CreateDirectory(dl);

        using var server = new TestEnv.FileServer();
        // 连 "Mozilla" 都不含也拒：默认 UA 也过不去，应当直接失败而不是反复重试
        server.AddFile("ua2.bin", Payload(64 * 1024), forbidUaSubstring: "Mozilla");

        await using var downloader = new IdmHttpDownloader();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var opts = new IdmDownloadOptions
        {
            Url = server.Url("ua2.bin"),
            Directory = dl,
            PartsDirectory = parts,
            PartKey = "ua-2",
            Headers = new List<string> { "User-Agent: Mozilla/5.0 Chrome/120.0.0.0" },
        };

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => downloader.DownloadAsync(opts, null, cts.Token));
        Assert.Contains("403", ex.Message);
    }

    [Fact]
    public async Task ConnectionRamp_FillsRequestedConnectionsQuickly()
    {
        // 切分只发生在监视器醒来的那一刻。原来固定 250ms 一拍 → 连接数每拍才翻一倍，
        // 8 条连接要 3 拍（约 750ms）才铺满；几秒下完的文件会被这段"爬坡"拖掉一大截。
        // 这里用服务端视角量：从第一条请求到第 8 条同时在传花了多久。
        string workDir = TestEnv.NewWorkDir();
        string dl = Path.Combine(workDir, "dl");
        string parts = Path.Combine(workDir, "parts");
        Directory.CreateDirectory(dl);

        var payload = Payload(12 * 1024 * 1024);
        using var server = new TestEnv.FileServer();
        // 每连接约 1.3 MB/s → 8 连接约 1.2 秒，足够覆盖爬坡期
        server.AddFile("ramp.bin", payload, chunkBytes: 32 * 1024, chunkDelayMs: 25);

        await using var downloader = new IdmHttpDownloader();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var opts = new IdmDownloadOptions
        {
            Url = server.Url("ramp.bin"),
            Directory = dl,
            PartsDirectory = parts,
            PartKey = "ramp-1",
            MaxConnections = 8,
        };

        var result = await downloader.DownloadAsync(opts, null, cts.Token);
        Assert.Equal(payload.Length, result.CompletedLength);

        double? ramp = server.RampMsTo(8);
        Assert.NotNull(ramp);
        Assert.True(server.MaxConcurrent >= 8, $"最多只同时开了 {server.MaxConcurrent} 条连接");
        Assert.True(ramp < 600,
            $"铺满 8 条连接用了 {ramp:0}ms（旧节拍约 750ms，说明加速没生效）");
    }

    [Fact]
    public async Task NeverOpensMoreConnectionsThanConfigured()
    {
        // 切分循环里如果只看 _connections，同一拍内可以连续切分：spawn 出来的 worker
        // 要几毫秒才自增计数，于是实际开出的连接数会超过用户设定（基准里 4 连接测到过 5 个并发）。
        // 多开连接等于偷偷给站点加压，必须钉死。
        string workDir = TestEnv.NewWorkDir();
        string dl = Path.Combine(workDir, "dl");
        string parts = Path.Combine(workDir, "parts");
        Directory.CreateDirectory(dl);

        var payload = Payload(8 * 1024 * 1024);
        using var server = new TestEnv.FileServer();
        server.AddFile("cap.bin", payload, chunkBytes: 32 * 1024, chunkDelayMs: 20);

        await using var downloader = new IdmHttpDownloader();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var opts = new IdmDownloadOptions
        {
            Url = server.Url("cap.bin"),
            Directory = dl,
            PartsDirectory = parts,
            PartKey = "cap-1",
            MaxConnections = 3,
        };

        var result = await downloader.DownloadAsync(opts, null, cts.Token);
        Assert.Equal(payload.Length, result.CompletedLength);
        Assert.True(server.MaxConcurrent <= 3,
            $"设定 3 条连接，实际同时开到 {server.MaxConcurrent} 条");
    }
}
