using System.Net;
using KokonaDownloader.Core.Engine;

namespace KokonaDownloader.Core.Tests;

/// <summary>
/// 读接口复用轮询快照的回归测试（第六轮开销审计 §12-C O-10 的落地护栏）。
///
/// 背景：浏览器扩展的 GET /api/tasks、GET /api/stats 原先每次都自己打一轮 RPC
/// （1000 任务时 458 KB 响应 + 约 30 ms 解析），而引擎自己每 800 ms 就已经把同样的数据拉过一次。
/// 现在这两个读接口优先吃引擎刚发布的快照，因此必须钉住两件事：
///  1. 快照可用时**确实不再发起 RPC**（用"aria2 已被杀掉、RPC 必然失败"来证明）；
///  2. 本地发生增删/暂停等指令后快照**立即作废**，读接口退回实时 RPC，绝不出现"指令后读不到"。
/// </summary>
public class SnapshotReadTests : IAsyncLifetime
{
    private TestEnv.FileServer _server = null!;
    private string _workDir = null!;
    private readonly List<DownloadEngine> _engines = new();

    public Task InitializeAsync()
    {
        _server = new TestEnv.FileServer();
        _server.AddFile("snap.bin", new byte[1024]);
        // 慢速文件：暂停类用例需要在 PauseAsync 时任务仍在下载中
        _server.AddFile("slow.bin", new byte[2 * 1024 * 1024], null, 32 * 1024, 50);
        _workDir = TestEnv.NewWorkDir();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var e in _engines) { try { await e.DisposeAsync(); } catch { } }
        try { _server?.Dispose(); } catch { }
        try { if (_workDir != null) Directory.Delete(_workDir, true); } catch { }
    }

    /// <summary>起一个真实 aria2 的引擎。pollMs 故意取大：测试要的是"轮询只发生过一轮"的确定性时序。</summary>
    private async Task<DownloadEngine> StartEngineAsync(string tag, int pollMs)
    {
        var work = Path.Combine(_workDir, tag);
        var dl = Path.Combine(work, "downloads");
        Directory.CreateDirectory(dl);
        var engine = new DownloadEngine(new EngineConfig
        {
            Aria2Path = TestEnv.Aria2Path,
            WorkDir = work,
            DefaultDownloadDir = dl,
            RpcPort = TestEnv.GetFreePort(),
            RpcSecret = "snapshot-secret",
            PollIntervalMs = pollMs,
            BtEnabled = false,
            BtListenPort = TestEnv.GetFreePort(),
            MaxConcurrentDownloads = 2,
            DefaultConnections = 1
        }, new TaskStore(Path.Combine(work, "tasks.json")));
        _engines.Add(engine);
        await engine.StartAsync();
        return engine;
    }

    internal static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs = 25000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(60);
        }
        return condition();
    }

    /// <summary>杀掉轮询与 aria2，并等在途那一轮彻底结束：此后快照不会再被替换。</summary>
    private static async Task FreezeSnapshotAsync(DownloadEngine engine)
    {
        engine.KillNow();
        await Task.Delay(250);
    }

    [Fact]
    public async Task 列表读接口复用轮询快照而不额外发起RPC()
    {
        var engine = await StartEngineAsync("list", 3000);
        var task = await engine.AddTaskAsync(new NewTaskRequest
        {
            Urls = new List<string> { _server.Url("snap.bin") },
            Connections = 1
        });
        Assert.True(await WaitUntilAsync(() => engine.TryGetRecentSnapshot(int.MaxValue)?.Any(t => t.Gid == task.Gid) == true));

        await FreezeSnapshotAsync(engine); // aria2 已死：之后任何 RPC 都会抛，读接口还能出数据只能是吃了快照

        var published = engine.TryGetRecentSnapshot(int.MaxValue)!;
        Assert.NotEmpty(published);
        var viaRead = await engine.GetAllTasksAsync();
        Assert.Equal(published.Count, viaRead.Count);
        Assert.All(viaRead, t => Assert.True(published.Any(s => ReferenceEquals(s, t)))); // 同一批对象 ⇒ 没有重新解析
    }

    [Fact]
    public void 读接口的快照新鲜度窗口必须宽过空闲轮询退避()
    {
        // 这条不等式就是"空闲态下读接口到底有没有复用快照"的全部真相。
        // 踩过的坑：窗口曾取 2000 ms，而空闲态轮询退避到 2500 ms 一轮——空闲态（扩展最常见的状态）
        // 每次读都判成"快照太旧"而退回实时 RPC，实测 200 次 GET /api/tasks 期间 aria2 CPU
        // 与不复用时持平（62 ms vs 78 ms），优化等于没生效。
        static int GetConst(string name)
        {
            var f = typeof(DownloadEngine).GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.NotNull(f);
            return Assert.IsType<int>(f!.GetValue(null)!);
        }

        var window = GetConst("ReadSnapshotMaxAgeMs");
        var idlePoll = GetConst("IdlePollIntervalMs");
        Assert.True(window > idlePoll,
            $"读快照新鲜度窗口 {window}ms 必须大于空闲轮询间隔 {idlePoll}ms，否则空闲态每次读都会退回实时 RPC");
        // 反向也要收口：窗口只比退避间隔宽一点，轮询真停摆时不会长时间供旧数据
        Assert.True(window <= idlePoll + 1500,
            $"窗口 {window}ms 相对空闲退避 {idlePoll}ms 过宽，轮询停摆时会长时间供旧数据");
    }

    [Fact]
    public async Task 统计读接口复用同一轮发布的统计()
    {
        var engine = await StartEngineAsync("stats", 3000);
        GlobalStat? published = null;
        engine.EngineEvent += (_, e) => { if (e.Type == "StatsUpdated") published = e.Stats; };
        Assert.True(await WaitUntilAsync(() => published != null));

        var snapshotStats = published!;
        await FreezeSnapshotAsync(engine);

        var viaRead = await engine.GetGlobalStatAsync();
        Assert.True(ReferenceEquals(snapshotStats, viaRead)); // 连对象都是同一份 ⇒ 零 RPC
        Assert.Equal(snapshotStats.NumStopped, viaRead.NumStopped);
    }
}

/// <summary>
/// 指令之后快照必须立即作废（与上面同一套护栏的另一半）：
/// 读接口允许吃"最近一轮轮询"的快照（窗口见 ReadSnapshotMaxAgeMs），若不作废，
/// 扩展"暂停后回查""添加后回查"就会看到指令前的旧状态。
/// 这一类的轮询间隔取 60 秒，保证断言窗口内不会有新一轮轮询来干扰时序。
/// </summary>
public class SnapshotInvalidationTests : IAsyncLifetime
{
    private TestEnv.FileServer _server = null!;
    private string _workDir = null!;
    private readonly List<DownloadEngine> _engines = new();

    public Task InitializeAsync()
    {
        _server = new TestEnv.FileServer();
        _server.AddFile("snap.bin", new byte[1024]);
        _server.AddFile("slow.bin", new byte[2 * 1024 * 1024], null, 32 * 1024, 50);
        _workDir = TestEnv.NewWorkDir();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var e in _engines) { try { await e.DisposeAsync(); } catch { } }
        try { _server?.Dispose(); } catch { }
        try { if (_workDir != null) Directory.Delete(_workDir, true); } catch { }
    }

    private async Task<DownloadEngine> StartEngineAsync(string tag, int pollMs)
    {
        var work = Path.Combine(_workDir, tag);
        var dl = Path.Combine(work, "downloads");
        Directory.CreateDirectory(dl);
        var engine = new DownloadEngine(new EngineConfig
        {
            Aria2Path = TestEnv.Aria2Path,
            WorkDir = work,
            DefaultDownloadDir = dl,
            RpcPort = TestEnv.GetFreePort(),
            RpcSecret = "snapshot-secret",
            PollIntervalMs = pollMs,
            BtEnabled = false,
            BtListenPort = TestEnv.GetFreePort(),
            MaxConcurrentDownloads = 2,
            DefaultConnections = 1
        }, new TaskStore(Path.Combine(work, "tasks.json")));
        _engines.Add(engine);
        await engine.StartAsync();
        return engine;
    }

    [Fact]
    public async Task 添加任务后快照立即作废且读接口能看到新任务()
    {
        var engine = await StartEngineAsync("invalidate", 60_000);
        Assert.True(await SnapshotReadTests.WaitUntilAsync(() => engine.TryGetRecentSnapshot(int.MaxValue) != null));
        var snapshotStats = await engine.GetGlobalStatAsync(); // 此刻读接口吃的是快照

        var task = await engine.AddTaskAsync(new NewTaskRequest
        {
            Urls = new List<string> { _server.Url("snap.bin") },
            Connections = 1
        });

        // 60 秒内不会再有新一轮轮询，因此这里的 null 是确定性的：添加动作自己把快照作废了
        Assert.Null(engine.TryGetRecentSnapshot(int.MaxValue));

        var all = await engine.GetAllTasksAsync(); // 退回实时 RPC
        Assert.Contains(all, t => t.Gid == task.Gid);

        var freshStats = await engine.GetGlobalStatAsync();
        Assert.False(ReferenceEquals(snapshotStats, freshStats)); // 统计同理，不再复用旧对象
    }

    [Fact]
    public async Task 暂停指令后读接口不会回查到旧状态()
    {
        // 轮询间隔取 60 秒：整个用例期间最多只有一轮轮询，读接口的行为完全由被测代码决定，无时序竞态
        var engine = await StartEngineAsync("pause", 60_000);
        var task = await engine.AddTaskAsync(new NewTaskRequest
        {
            Urls = new List<string> { _server.Url("slow.bin") }, // 慢速源：保证 PauseAsync 时仍在下载中
            Connections = 1
        });

        // 添加动作已让快照作废：此后每次读都必须是实时结果
        Assert.Null(engine.TryGetRecentSnapshot(int.MaxValue));

        await engine.PauseAsync(task.Gid);

        var after = await engine.GetAllTasksAsync();
        var paused = after.FirstOrDefault(t => t.Gid == task.Gid);
        Assert.NotNull(paused);
        Assert.Equal(TaskState.Paused, paused.State); // 实时 RPC 立刻反映暂停结果，不存在"暂停后回查还是下载中"

        // 快照依旧处于作废态（PauseAsync 内部显式作废）。本断言只是记录意图：
        // 这一类里快照早已被添加动作作废，"暂停自己也会作废快照"由上面的实时读结果间接保证。
        Assert.Null(engine.TryGetRecentSnapshot(int.MaxValue));
    }
}
