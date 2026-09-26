using System.Collections.Concurrent;
using KokonaDownloader.Core.Engine;
using Xunit.Abstractions;

namespace KokonaDownloader.Core.Tests;

/// <summary>
/// 第十一轮回归测试，对应测试报告 §16-H 的两条遗留项：
///  1. 引擎自愈上移：暂停/恢复/删除等端点遇到 aria2 掉线不再把原始网络异常抛给 HTTP 层；
///  2. 磁力"幻影行"：同 infohash 已注册时 aria2 会**静默丢弃**新 addUri，
///     界面不能留一条永不报错的 0% 行（必须显示失败与原因）。
/// </summary>
public class RoundElevenEngineGuardTests
{
    /// <summary>构造一个"引擎永远连不上且必然重启失败"的引擎：RPC 端口无人监听（连接被拒），
    /// aria2 路径不存在（自愈重启抛 Win32Exception）。不调用 StartAsync。</summary>
    private static DownloadEngine NewDeadEngine(string workDir, List<string> logs)
    {
        var config = new EngineConfig
        {
            Aria2Path = Path.Combine(workDir, "aria2c-不存在.exe"),
            WorkDir = Path.Combine(workDir, "engine"),
            DefaultDownloadDir = workDir,
            RpcPort = TestEnv.GetFreePort(),
            RpcSecret = "dead-engine-secret",
            BtEnabled = false,
            PollIntervalMs = 200
        };
        return new DownloadEngine(config, new TaskStore(Path.Combine(workDir, "tasks.json")), logs.Add);
    }

    private const string SomeGid = "0123456789abcdef0123456789abcdef01234567";

    [Fact]
    public async Task 引擎掉线时暂停与恢复走自愈而不是抛出原始网络异常()
    {
        var work = TestEnv.NewWorkDir();
        try
        {
            var logs = new List<string>();
            var engine = NewDeadEngine(work, logs);

            // 修复前：SocketException / HttpRequestException 直接冒泡到 HTTP 层（表现为 500 且文案不可读）
            var pauseEx = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.PauseAsync(SomeGid));
            Assert.Contains("下载引擎不可用", pauseEx.Message);

            var resumeEx = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.ResumeAsync(SomeGid));
            Assert.Contains("下载引擎不可用", resumeEx.Message);

            await Assert.ThrowsAsync<InvalidOperationException>(() => engine.PauseAllAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() => engine.ResumeAllAsync());

            // 每次都要真的尝试过重启引擎，而不是直接放弃
            Assert.True(logs.Count(l => l.Contains("正在自动重启")) >= 4,
                $"四个端点都应触发引擎自愈重启，实际日志:\n{string.Join("\n", logs)}");
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }

    [Fact]
    public async Task 引擎掉线时删除任务仍完成本地清理且不抛异常()
    {
        var work = TestEnv.NewWorkDir();
        try
        {
            var logs = new List<string>();
            var engine = NewDeadEngine(work, logs);

            // 删除的语义是"这一行消失"：引擎不可用不该挡住本地元数据清理
            var ex = await Record.ExceptionAsync(() => engine.RemoveAsync(SomeGid));
            Assert.Null(ex);
            Assert.Contains(logs, l => l.Contains("删除任务时引擎未成功执行"));
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }
}

/// <summary>需要真实 aria2 的磁力存活校验用例。</summary>
public class RoundElevenMagnetLivenessTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    /// <summary>引擎日志（轮询线程写入、测试线程读，必须是并发集合）。</summary>
    private readonly ConcurrentQueue<string> _logs = new();
    private string _workDir = null!;
    private string _downloadDir = null!;
    private DownloadEngine _engine = null!;

    public RoundElevenMagnetLivenessTests(ITestOutputHelper output) => _output = output;

    public Task InitializeAsync()
    {
        _workDir = TestEnv.NewWorkDir();
        _downloadDir = Path.Combine(_workDir, "downloads");
        Directory.CreateDirectory(_downloadDir);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        try { await _engine.DisposeAsync(); } catch { }
        try { Directory.Delete(_workDir, true); } catch { }
    }

    /// <summary>
    /// 同一批里塞两条相同磁力：第一条会被 aria2 注册，第二条因 infohash 已注册而**没有被注册**
    /// （要么在 tellStopped 里留下一个 "already registered" 错误任务，要么被直接静默丢弃）。
    /// 修复前：错误任务被整个删掉（用户只看到"行没了"），静默丢弃的那条连行都没有 —— 界面上就是
    /// 一条永不报错的 0% 幻影行。修复后两条路径都必须收敛成一条带中文原因的失败行，并且写进本地历史。
    /// </summary>
    [Fact]
    public async Task 同批次重复磁力未被引擎注册时必须标失败并写明原因()
    {
        var infoHash = new string('3', 40);
        var magnet = $"magnet:?xt=urn:btih:{infoHash}&dn=kokona-dup-magnet-test";
        await StartEngineAsync();

        var addedAt = DateTime.Now;
        var results = await _engine.AddTasksAsync(new[]
        {
            new NewTaskRequest { Urls = new List<string> { magnet }, Directory = _downloadDir },
            new NewTaskRequest { Urls = new List<string> { magnet }, Directory = _downloadDir }
        });
        Assert.Equal(2, results.Count);
        var gids = results.Select(r => r.Gid).ToList();
        Assert.Equal(2, gids.Distinct().Count());

        // 等待引擎把它标成失败（校验期 6 s + 轮询若干轮，超时给足余量）
        var deadline = DateTime.Now.AddSeconds(25);
        DownloadTaskInfo? dropped = null;
        while (DateTime.Now < deadline)
        {
            var all = await _engine.GetAllTasksAsync();
            dropped = all.FirstOrDefault(t => gids.Contains(t.Gid)
                && t.State == TaskState.Failed
                && (t.ErrorMessage ?? "").Contains("同一磁力"));
            if (dropped != null) break;
            await Task.Delay(200);
        }

        Assert.NotNull(dropped);
        Assert.True(dropped!.IsBt);
        Assert.Contains("已在下载或做种中", dropped.ErrorMessage!);

        // 取证：本机的重复磁力到底以哪种形态出现 —— 1=aria2 生成 already registered 错误任务（很快），
        // 2=aria2 静默丢弃、由 6 s 存活校验兜底。两条路径汇到同一个用户可见结果，但报告里要如实写清是哪一条。
        var elapsedMs = (DateTime.Now - addedAt).TotalMilliseconds;
        var byEngineError = _logs.Any(l => l.Contains("already registered 错误拒绝"));
        var byLiveness = _logs.Any(l => l.Contains("引擎静默丢弃"));
        _output.WriteLine($"分支取证: elapsed={elapsedMs:F0}ms alreadyRegisteredErrorTask={byEngineError} livenessFallback={byLiveness}");
        Assert.True(byEngineError || byLiveness,
            $"失败行必须来自两条已知路径之一（不能是别处冒出来的）: alreadyRegistered={byEngineError} liveness={byLiveness}");
        if (byLiveness)
            Assert.True(elapsedMs >= 5500, $"存活校验只能在 {6000}ms 超时后判定，实际耗时 {elapsedMs:F0}ms");

        // 被 aria2 真正注册的那条必须仍然活着，不能被误判成失败
        var rows = await _engine.GetAllTasksAsync();
        Assert.Contains(rows, t => gids.Contains(t.Gid) &&
            t.State is TaskState.Active or TaskState.Waiting or TaskState.Paused
                     or TaskState.Seeding or TaskState.Completed);

        // 失败行必须进本地历史：aria2 里已经没有它，重启后也只有本地留档能把它带回来
        // （TaskStore 是 500 ms 防抖落盘，这里等到磁盘上真出现终态为止）
        var storeFile = Path.Combine(_workDir, "tasks.json");
        Assert.True(await SnapshotReadTests.WaitUntilAsync(() =>
                new TaskStore(storeFile).GetMeta(dropped.Gid)?.FinalState == "Failed", timeoutMs: 10_000),
            "重复磁力的失败行必须写进本地历史（aria2 里没有它，重启只能靠本地留档）");
    }

    private async Task StartEngineAsync()
    {
        var config = new EngineConfig
        {
            Aria2Path = TestEnv.Aria2Path,
            WorkDir = Path.Combine(_workDir, "engine"),
            DefaultDownloadDir = _downloadDir,
            RpcPort = TestEnv.GetFreePort(),
            RpcSecret = "r11-magnet-secret",
            BtEnabled = true,
            BtListenPort = TestEnv.GetFreePort(),
            BtSeedEnabled = false,
            PollIntervalMs = 300,
            DefaultConnections = 4
        };
        _engine = new DownloadEngine(config, new TaskStore(Path.Combine(_workDir, "tasks.json")), l => _logs.Enqueue(l));
        await _engine.StartAsync();
    }
}
