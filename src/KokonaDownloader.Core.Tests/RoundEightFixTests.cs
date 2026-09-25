using System.Text.Json.Nodes;
using KokonaDownloader.Core.Engine;

namespace KokonaDownloader.Core.Tests;

/// <summary>
/// 第八轮全链路测试（dist 包）暴露的缺陷的回归测试。对应测试报告 §14-C 的 P1-1 / P1-2 / P2-3 / P2-4：
///  1. P1-1 改默认下载目录后新任务仍落到旧目录（--dir 只是启动快照）；
///  2. P1-2 重复下载同一链接会静默覆盖已有文件（全局 --allow-overwrite=true + 自动捕获不带文件名）；
///  3. P2-3 服务器不支持 Range 时续传失败，留下"大小正确但内容损坏"的文件；
///  4. P2-4 已完成任务重启后整段消失（aria2 --save-session 实测不落已完成任务）。
/// </summary>
public class RoundEightOptionTests
{
    private static string? Opt(JsonObject options, string key) => options[key]?.GetValue<string>();

    [Fact]
    public void 新建HTTP任务必须逐条关掉allow_overwrite()
    {
        // 探针结论（.verify/fixtest/aria_probe.mjs）：allow-overwrite=false 时 aria2 自动改名
        // file.bin → file.1.bin 且旧文件完好；true（旧版全局值）则直接覆盖用户已有文件。
        var options = Aria2RpcClient.BuildOptions(new NewTaskRequest
        {
            Urls = new List<string> { "http://127.0.0.1/a.bin" },
            Directory = @"C:\Downloads",
            FileName = "a.bin",
            Connections = 4
        });
        Assert.Equal("false", Opt(options, "allow-overwrite"));
        Assert.Equal("false", Opt(options, "continue")); // 新建下载从零开始，续传只用于会话恢复
        Assert.Equal(@"C:\Downloads", Opt(options, "dir")); // 目录必须显式下发，否则回落到启动时的 --dir
        Assert.Equal("4", Opt(options, "split"));
        Assert.Equal("a.bin", Opt(options, "out"));
    }

    [Fact]
    public void BT任务保持allow_overwrite_以便复用目录中的同名文件()
    {
        var options = Aria2RpcClient.BuildOptions(new NewTaskRequest
        {
            Urls = new List<string> { "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567" },
            Directory = @"C:\Downloads",
            AllowOverwriteExisting = true
        });
        Assert.Equal("true", Opt(options, "allow-overwrite"));
        Assert.Null(options["split"]); // BT 的分片由协议决定，不该下发 split
    }

    [Fact]
    public void 不可续传失败的判定只认错误码8与相应文案()
    {
        Assert.True(DownloadEngine.IsUnresumableFailure(new DownloadTaskInfo { ErrorCode = 8 }));
        Assert.True(DownloadEngine.IsUnresumableFailure("Invalid range header"));
        Assert.True(DownloadEngine.IsUnresumableFailure("Unable to continue download 8388608"));
        // 普通网络中断必须保持可续传：这类失败残留是有效的断点，不能隔离
        Assert.False(DownloadEngine.IsUnresumableFailure(new DownloadTaskInfo { ErrorCode = 2 }));
        Assert.False(DownloadEngine.IsUnresumableFailure("HTTP error code was 404"));
        Assert.False(DownloadEngine.IsUnresumableFailure((string?)null));
    }
}

/// <summary>TaskStore 的终态留档（本地历史数据源）。</summary>
public class TaskStoreHistoryTests
{
    private static string TempStore(out string file)
    {
        var dir = TestEnv.NewWorkDir();
        file = Path.Combine(dir, "tasks.json");
        return dir;
    }

    [Fact]
    public async Task 终态快照持久化后重开仍能读出历史行()
    {
        var work = TempStore(out var file);
        try
        {
            var store = new TaskStore(file);
            store.AddMeta("g1", new TaskMeta { Gid = "g1", TaskNumber = 11, Name = "a.bin", Urls = new List<string> { "http://x/a.bin" }, AddedAt = DateTime.Now });
            store.AddMeta("g2", new TaskMeta { Gid = "g2", TaskNumber = 12, Name = "b.bin", AddedAt = DateTime.Now.AddDays(-1) });
            store.AddMeta("g3", new TaskMeta { Gid = "g3", Name = "running.bin", AddedAt = DateTime.Now }); // 未结束 → 不算历史

            store.UpdateFinished(new DownloadTaskInfo
            {
                Gid = "g1", TaskNumber = 11, Name = "a.bin", State = TaskState.Completed,
                TotalLength = 4096, CompletedLength = 4096, Dir = @"C:\D", FilePath = @"C:\D\a.bin",
                Urls = new List<string> { "http://x/a.bin" }
            });
            store.UpdateFinished(new DownloadTaskInfo
            {
                Gid = "g2", TaskNumber = 12, Name = "b.bin", State = TaskState.Failed,
                TotalLength = 8, CompletedLength = 3, ErrorCode = 8, ErrorMessage = "Invalid range header",
                Dir = @"C:\D", FilePath = @"C:\D\b.bin", Urls = new List<string> { "http://x/b.bin" }
            });
            store.SaveNow();

            var finished = store.Finished();
            Assert.Equal(2, finished.Count);
            Assert.DoesNotContain(finished, m => m.Gid == "g3"); // 未结束的不算历史
            // 按完成时间倒序（同一毫秒内完成的两条不比较先后，只验证整体不倒挂）
            for (var i = 1; i < finished.Count; i++)
                Assert.True(finished[i - 1].FinishedAt >= finished[i].FinishedAt,
                    "Finished() 必须按完成时间倒序");
            Assert.Equal("Completed", finished.First(m => m.Gid == "g1").FinalState);
            Assert.Equal(4096, finished.First(m => m.Gid == "g1").TotalLength);
            Assert.Equal(@"C:\D\a.bin", finished.First(m => m.Gid == "g1").FilePath);
            Assert.Equal(8, finished.First(m => m.Gid == "g2").ErrorCode);

            // 重开（模拟应用重启）：终态字段必须还在，本地历史才拼得回来
            var reloaded = new TaskStore(file);
            var again = reloaded.Finished();
            Assert.Equal(2, again.Count);
            Assert.Equal(@"C:\D\a.bin", again.First(m => m.Gid == "g1").FilePath);
            Assert.Equal("Invalid range header", again.First(m => m.Gid == "g2").ErrorMessage);

            Assert.Equal(2, reloaded.RemoveMany(new[] { "g1", "g2", "不存在的gid" }));
            Assert.Empty(reloaded.Finished());
            await Task.CompletedTask;
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }

    [Fact]
    public void 失败任务的完成长度按已完成写入_成功任务按总长写入()
    {
        var work = TempStore(out var file);
        try
        {
            var store = new TaskStore(file);
            store.AddMeta("ok", new TaskMeta { Gid = "ok", AddedAt = DateTime.Now });
            store.AddMeta("bad", new TaskMeta { Gid = "bad", AddedAt = DateTime.Now });
            // 长度未知（total=0）时保留已完成字节，别让历史行谎报"完整"
            store.UpdateFinished(new DownloadTaskInfo { Gid = "ok", State = TaskState.Completed, TotalLength = 100, CompletedLength = 90 });
            store.UpdateFinished(new DownloadTaskInfo { Gid = "bad", State = TaskState.Failed, TotalLength = 0, CompletedLength = 40 });
            Assert.Equal(100, store.GetMeta("ok")!.CompletedLength);
            Assert.Equal(40, store.GetMeta("bad")!.CompletedLength);
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }
}

/// <summary>需要真实 aria2 的第八轮回归用例。</summary>
public class RoundEightEngineTests : IAsyncLifetime
{
    private TestEnv.FileServer _server = null!;
    private string _workDir = null!;

    public Task InitializeAsync()
    {
        _server = new TestEnv.FileServer();
        _workDir = TestEnv.NewWorkDir();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        try { _server?.Dispose(); } catch { }
        try { if (_workDir != null) Directory.Delete(_workDir, true); } catch { }
        await Task.CompletedTask;
    }

    private DownloadEngine NewEngine(string downloadDir, string storeFile, bool btEnabled = false)
    {
        var config = new EngineConfig
        {
            Aria2Path = TestEnv.Aria2Path,
            WorkDir = _workDir,
            DefaultDownloadDir = downloadDir,
            RpcPort = TestEnv.GetFreePort(),
            RpcSecret = "r8-secret",
            MaxConcurrentDownloads = 3,
            DefaultConnections = 1,
            BtEnabled = btEnabled,
            PollIntervalMs = 200
        };
        return new DownloadEngine(config, new TaskStore(storeFile), _ => { });
    }

    private static async Task<DownloadTaskInfo> WaitTerminalAsync(DownloadEngine engine, string gid, int timeoutSeconds = 60)
    {
        var deadline = DateTime.Now.AddSeconds(timeoutSeconds);
        while (DateTime.Now < deadline)
        {
            var all = await engine.GetAllTasksAsync();
            var row = all.FirstOrDefault(t => t.Gid == gid);
            if (row is { State: TaskState.Completed or TaskState.Failed }) return row;
            await Task.Delay(150);
        }
        throw new TimeoutException($"等任务 {gid} 结束超时");
    }

    /// <summary>轮询等待某个条件成立（引擎的终态处理发生在**轮询循环**里，
    /// 而读接口可能走实时 RPC 抢先看到 Failed/Completed，所以断言前必须等引擎真正处理完）。</summary>
    private static async Task<bool> WaitUntilAsync(Func<bool> check, int timeoutMs = 20000)
    {
        var deadline = DateTime.Now.AddMilliseconds(timeoutMs);
        while (DateTime.Now < deadline)
        {
            if (check()) return true;
            await Task.Delay(150);
        }
        return check();
    }

    /// <summary>路径比较前的归一：aria2 上报的 files[0].path 用正斜杠（C:/Users/…），
    /// 而 .NET 拼出来的是反斜杠，直接比字符串会误判"路径不对"。</summary>
    private static string Full(string p) => Path.GetFullPath(p);

    [Fact]
    public async Task 改默认目录后新任务立刻落到新目录()
    {
        var dirA = Path.Combine(_workDir, "dirA");
        var dirB = Path.Combine(_workDir, "dirB");
        Directory.CreateDirectory(dirA);
        var content = new byte[256 * 1024];
        new Random(21).NextBytes(content);
        _server.AddFile("hotdir.bin", content);

        var engine = NewEngine(dirA, Path.Combine(_workDir, "tasks-hotdir.json"));
        await engine.StartAsync();
        try
        {
            // 设置里改了目录（AppHost 会转成这一次调用），无需重启引擎
            engine.UpdateRuntimeDefaults(defaultDownloadDir: dirB);
            Assert.Equal(dirB, engine.DefaultDownloadDir);

            var task = await engine.AddTaskAsync(new NewTaskRequest
            {
                Urls = new List<string> { _server.Url("hotdir.bin") }
            });
            var done = await WaitTerminalAsync(engine, task.Gid);

            Assert.Equal(TaskState.Completed, done.State);
            Assert.True(File.Exists(Path.Combine(dirB, "hotdir.bin")), $"新任务应落到改后的目录 {dirB}");
            Assert.False(File.Exists(Path.Combine(dirA, "hotdir.bin")), "旧目录不该再收到新任务");
            Assert.Equal(dirB, done.Dir);
            Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(dirB, "hotdir.bin")));
        }
        finally { await engine.DisposeAsync(); }
    }

    [Fact]
    public async Task 同名文件已存在时新下载自动改名而不是覆盖()
    {
        var dir = Path.Combine(_workDir, "dirOverwrite");
        Directory.CreateDirectory(dir);
        var content = new byte[300 * 1024];
        new Random(22).NextBytes(content);
        _server.AddFile("same.bin", content);
        // 已存在的同名文件是别的内容：P1-2 的实质就是"重复下载不能碰它"
        var existing = new byte[10];
        await File.WriteAllBytesAsync(Path.Combine(dir, "same.bin"), existing);

        var engine = NewEngine(dir, Path.Combine(_workDir, "tasks-overwrite.json"));
        await engine.StartAsync();
        try
        {
            var task = await engine.AddTaskAsync(new NewTaskRequest
            {
                Urls = new List<string> { _server.Url("same.bin") }
            });
            var done = await WaitTerminalAsync(engine, task.Gid);

            Assert.Equal(TaskState.Completed, done.State);
            Assert.True(existing.SequenceEqual(await File.ReadAllBytesAsync(Path.Combine(dir, "same.bin"))),
                "已有文件必须原样保留（allow-overwrite=false）");
            Assert.NotEqual(Full(Path.Combine(dir, "same.bin")), Full(done.FilePath!));
            Assert.True(File.Exists(done.FilePath!), $"aria2 应把新下载改名存放（实际 {done.FilePath}）");
            Assert.Equal(content, await File.ReadAllBytesAsync(done.FilePath!));
        }
        finally { await engine.DisposeAsync(); }
    }

    [Fact]
    public async Task 已完成任务在引擎重启后仍以历史行出现在列表里()
    {
        var dir = Path.Combine(_workDir, "dirHistory");
        Directory.CreateDirectory(dir);
        var content = new byte[128 * 1024];
        new Random(23).NextBytes(content);
        _server.AddFile("hist.bin", content);

        var storeFile = Path.Combine(_workDir, "tasks-history.json");
        var engine = NewEngine(dir, storeFile);
        string gid;
        long taskNumber;
        await engine.StartAsync();
        try
        {
            var task = await engine.AddTaskAsync(new NewTaskRequest { Urls = new List<string> { _server.Url("hist.bin") } });
            gid = task.Gid;
            var done = await WaitTerminalAsync(engine, gid);
            Assert.Equal(TaskState.Completed, done.State);
            taskNumber = done.TaskNumber;
            // 终态留档发生在引擎的轮询循环里：等它真的写出来，再谈"重启后还能看到"
            Assert.True(await WaitUntilAsync(() => new TaskStore(storeFile).GetMeta(gid)?.FinalState == "Completed"),
                "完成任务必须由引擎写成终态快照（本地历史的数据源）");
        }
        finally { await engine.DisposeAsync(); } // Dispose 会同步落盘 tasks.json，之后才能从文件里读回

        // aria2 的 --save-session 不会写已完成任务（实测 0 字节），客户端必须自己留档
        var meta = new TaskStore(storeFile).GetMeta(gid);
        Assert.NotNull(meta);
        Assert.Equal("Completed", meta!.FinalState);
        Assert.Equal(Full(Path.Combine(dir, "hist.bin")), Full(meta.FilePath!));

        // 换一个 aria2 端口重开引擎（等价于关掉应用再打开）：列表里仍应看到这条已完成任务
        var engine2 = NewEngine(dir, storeFile);
        await engine2.StartAsync();
        try
        {
            var rows = await engine2.GetAllTasksAsync();
            var row = rows.FirstOrDefault(t => t.Gid == gid);
            Assert.NotNull(row);
            Assert.Equal(TaskState.Completed, row!.State);
            Assert.Equal(taskNumber, row.TaskNumber);
            Assert.Equal(Full(Path.Combine(dir, "hist.bin")), Full(row.FilePath!));
            Assert.Equal(128 * 1024, row.TotalLength);
            Assert.Equal(0, row.DownloadSpeed); // 历史行不再"下载中"
            Assert.Contains(row.Urls, u => u.StartsWith("http://")); // 能解释"这是哪来的"，也支撑"重新下载"
        }
        finally { await engine2.DisposeAsync(); }
    }

    [Fact]
    public async Task 服务器不支持Range时续传失败_残留被隔离为partial且仍可重新下载()
    {
        var dir = Path.Combine(_workDir, "dirNoRange");
        Directory.CreateDirectory(dir);
        const int total = 1024 * 1024;
        var content = new byte[total];
        new Random(24).NextBytes(content);
        // ignoreRange：收到 Range 也回整份 200 → aria2 报 errorCode=8 Invalid range header
        _server.AddFile("noresume.bin", content, chunkBytes: 32 * 1024, chunkDelayMs: 60, ignoreRange: true);

        var engine = NewEngine(dir, Path.Combine(_workDir, "tasks-norange.json"));
        await engine.StartAsync();
        try
        {
            var task = await engine.AddTaskAsync(new NewTaskRequest
            {
                Urls = new List<string> { _server.Url("noresume.bin") },
                Connections = 1
            });

            // 先下到一半暂停，制造"有断点可续"的前提
            var deadline = DateTime.Now.AddSeconds(30);
            DownloadTaskInfo? mid = null;
            while (DateTime.Now < deadline)
            {
                mid = await engine.GetTaskAsync(task.Gid);
                if (mid is { CompletedLength: > 0 }) break;
                await Task.Delay(100);
            }
            Assert.True(mid is { CompletedLength: > 0 }, "应已下载到一部分数据再暂停");
            await engine.PauseAsync(task.Gid);
            await Task.Delay(500);

            var rangesBefore = _server.RangeRequestCount;
            await engine.ResumeAsync(task.Gid);

            // 续传请求确实发出了，但服务器不懂 Range → aria2 失败
            var failed = await WaitTerminalAsync(engine, task.Gid);
            Assert.True(_server.RangeRequestCount > rangesBefore, "恢复后应发出带 Range 的续传请求");
            Assert.Equal(TaskState.Failed, failed.State);

            // 关键断言（第八轮 D2）：不能留下一个"大小正确看起来像成功"的文件
            var original = Path.Combine(dir, "noresume.bin");
            Assert.True(
                await WaitUntilAsync(() => !File.Exists(original) && Directory.GetFiles(dir, "noresume.bin.partial*").Length > 0),
                $"引擎未把失败残留隔离为 *.partial（aria2 errorCode={failed.ErrorCode} errorMessage={failed.ErrorMessage}）");
            var partials = Directory.GetFiles(dir, "noresume.bin.partial*");
            Assert.False(File.Exists(original + ".aria2"), "原名下的 .aria2 断点控制文件必须跟着挪走");
            // 控制文件跟着残留一起改名为 *.partial.aria2（用户想手工抢救时还有断点可用），
            // 但绝不能再留下一个挂在原名下的、会被 --continue 误当断点用的控制文件
            foreach (var sidecar in Directory.GetFiles(dir, "*.aria2"))
                Assert.EndsWith(".partial.aria2", sidecar);
            Assert.True(File.Exists(partials[0]), "隔离只是改名，不该删数据（用户可能还想手工抢救）");

            // 结果清掉后 aria2 不认识它了，列表里的这一行来自本地历史，且记住隔离后的路径
            var rows = await engine.GetAllTasksAsync();
            var row = rows.FirstOrDefault(t => t.Gid == task.Gid);
            Assert.NotNull(row);
            Assert.Equal(TaskState.Failed, row!.State);
            Assert.Equal(Full(partials[0]), Full(row.FilePath!));
            Assert.False(string.IsNullOrEmpty(row.ErrorMessage), "历史行要能解释为什么失败");

            // 点"重新下载"必须还能用：此时 aria2 已经没有这个 gid，只能靠本地留档
            var re = await engine.RedownloadAsync(task.Gid);
            Assert.NotEqual(task.Gid, re.Gid);
            var again = await WaitTerminalAsync(engine, re.Gid);
            Assert.Equal(TaskState.Completed, again.State);
            Assert.NotEqual(partials[0], again.FilePath);
            Assert.Equal(content, await File.ReadAllBytesAsync(again.FilePath!));
        }
        finally { await engine.DisposeAsync(); }
    }

    [Fact]
    public async Task 会话文件里早已完成的条目不会在重启后被重新排队()
    {
        var dir = Path.Combine(_workDir, "dirGhost");
        Directory.CreateDirectory(dir);
        var content = new byte[64 * 1024];
        new Random(25).NextBytes(content);
        _server.AddFile("ghost.bin", content);

        var storeFile = Path.Combine(_workDir, "tasks-ghost.json");
        var engine = NewEngine(dir, storeFile);
        string url, gid;
        await engine.StartAsync();
        try
        {
            var task = await engine.AddTaskAsync(new NewTaskRequest
            {
                Urls = new List<string> { _server.Url("ghost.bin") }
            });
            gid = task.Gid;
            var done = await WaitTerminalAsync(engine, task.Gid);
            url = done.Urls.First();
            Assert.True(await WaitUntilAsync(() => new TaskStore(storeFile).GetMeta(gid)?.FinalState == "Completed"));
        }
        finally { await engine.DisposeAsync(); }

        // 模拟"aria2 在这个任务还没下完时被强杀"：会话文件里留下了一条其实已经完成的下载。
        // aria2 的 --input-file 不认这是完成过的下载，会把它当新任务重新加入（新 gid、0 B、排队中）。
        var meta = new TaskStore(storeFile).GetMeta(gid)!;
        var sessionFile = Path.Combine(_workDir, "aria2.session");
        File.AppendAllText(sessionFile, $"{url}\n dir={meta.Dir}\n out=ghost.bin\n");

        var engine2 = NewEngine(dir, storeFile);
        await engine2.StartAsync();
        try
        {
            // 给轮询留几轮：若这条被重新加入，它一定会以"非完成态"出现在列表里
            var ghostSeen = false;
            var deadline = DateTime.Now.AddSeconds(4);
            while (DateTime.Now < deadline && !ghostSeen)
            {
                var rows = await engine2.GetAllTasksAsync();
                ghostSeen = rows.Any(t => t.Gid != gid && t.Urls.Contains(url));
                if (!ghostSeen) await Task.Delay(200);
            }
            Assert.False(ghostSeen, "已完成任务的会话条目被重新加成了新任务（应在启动前被过滤掉）");

            var list = await engine2.GetAllTasksAsync();
            Assert.Single(list.Where(t => t.Urls.Contains(url)));
            Assert.Equal(TaskState.Completed, list.Single(t => t.Urls.Contains(url)).State);
            Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(dir, "ghost.bin")));
        }
        finally { await engine2.DisposeAsync(); }
    }
}

/// <summary>会话文件过滤（纯单测，不启动 aria2）。</summary>
public class SessionPurgeTests
{
    [Fact]
    public async Task 启动前过滤会话文件_已删除与早已完成的条目都不再复活()
    {
        var work = TestEnv.NewWorkDir();
        try
        {
            var session = Path.Combine(work, "aria2.session");
            await File.WriteAllTextAsync(session,
                "http://127.0.0.1:8080/gone.bin\n dir=C:\\dl\\a\n out=gone.bin\n" +
                "http://127.0.0.1:8080/finished.bin\n dir=C:\\dl\\b\n out=finished.bin\n" +
                "http://127.0.0.1:8080/keep.bin\n dir=C:\\dl\\c\n out=keep.bin\n");

            var store = new TombstoneStore(Path.Combine(work, "tombstones.json"));
            store.Mark(new[] { "http://127.0.0.1:8080/gone.bin" }, @"C:\dl\a");

            var purged = store.PurgeSessionFile(session, new (string Url, string? Dir)[]
            {
                ("http://127.0.0.1:8080/finished.bin", @"C:\dl\b")
            });

            Assert.Equal(2, purged);
            var rest = await File.ReadAllTextAsync(session);
            Assert.DoesNotContain("gone.bin", rest);      // 墓碑：用户删过的不许复活
            Assert.DoesNotContain("finished.bin", rest);  // 客户端记着已完成的也不该重新排队
            Assert.Contains("keep.bin", rest);            // 其它条目一个都不能动
            // 追加过滤不落墓碑：下次没传 extra 时它必须还能被会话恢复（不误伤正常续传）
            Assert.Equal(0, store.PurgeSessionFile(session));
            Assert.Contains("keep.bin", await File.ReadAllTextAsync(session));
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }
}
