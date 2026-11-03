using KokonaDownloader.Core.Engine;

namespace KokonaDownloader.Core.Tests;

/// <summary>
/// 「全部暂停 / 继续全部」与「最大同时下载数」对自研引擎的验收。
/// 这两条以前只发给 aria2，界面上点了没反应——正是换核心后最容易被察觉的退化。
///
/// 说明：断言统一走 GetAllTasksAsync（UI 实际读的路径，快照不新鲜时会回退实时 RPC），
/// 不直接读内部快照——那样会把测试绑死在"轮询是否刚好发布了"的时序上。
/// </summary>
public sealed class IdmControlTests
{
    private static async Task<List<DownloadTaskInfo>> WaitTasksAsync(
        DownloadEngine engine, Func<List<DownloadTaskInfo>, bool> condition,
        int timeoutMs, string because, Func<string>? detail = null)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        var last = new List<DownloadTaskInfo>();
        while (Environment.TickCount64 < deadline)
        {
            last = await engine.GetAllTasksAsync();
            if (condition(last)) return last;
            await Task.Delay(150);
        }
        throw new TimeoutException(
            $"{because}；最后状态：[{string.Join(",", last.Select(t => $"{t.Gid}:{t.State}:{t.ErrorMessage}"))}]" +
            (detail is null ? "" : "\n" + detail()));
    }

    [Fact]
    public async Task PauseAll_StopsIdmTasks_ResumeAllRestartsThem()
    {
        var logs = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var engine = IdmTestEnv.CreateEngine(out _, out var workDir, out var dl, s => logs.Enqueue(s));
        using var server = new TestEnv.FileServer();
        // 必须够慢：快的话任务在断言前就下完了，测的就不是"暂停"而是"已完成"
        server.AddFile("a.bin", new byte[2 * 1024 * 1024], null, 8 * 1024, 100);
        server.AddFile("b.bin", new byte[2 * 1024 * 1024], null, 8 * 1024, 100);

        try
        {
            await engine.StartAsync();
            await engine.SetMaxConcurrentDownloadsAsync(1);   // 只放一个跑，另一个必然排队

            await engine.AddTaskAsync(new NewTaskRequest
            {
                Urls = new List<string> { server.Url("a.bin") },
                Directory = dl,
            });
            await engine.AddTaskAsync(new NewTaskRequest
            {
                Urls = new List<string> { server.Url("b.bin") },
                Directory = dl,
            });

            await WaitTasksAsync(engine, list => list.Count(t => t.State == TaskState.Active) == 1,
                25000, "并发上限没有生效：没有出现「一个在跑」的稳态");

            // 排队项必须是 Waiting，而且不能过一会儿自己爬起来
            await Task.Delay(1500);
            var states = (await engine.GetAllTasksAsync()).Select(t => t.State).ToList();
            Assert.Equal(2, states.Count);
            Assert.Equal(1, states.Count(s => s == TaskState.Active));
            Assert.Equal(1, states.Count(s => s == TaskState.Waiting));

            // 全部暂停：跑着的和排队中的都必须停
            await engine.PauseAllAsync();
            await WaitTasksAsync(engine,
                list => list.Count == 2 && list.All(t => t.State == TaskState.Paused),
                20000, "[idm] 全部暂停无效",
                () => "日志：\n" + string.Join("\n", logs.TakeLast(20)));

            // 全部继续：受并发上限约束，只能起一个
            await engine.ResumeAllAsync();
            await WaitTasksAsync(engine, list => list.Count(t => t.State == TaskState.Active) == 1,
                25000, "继续全部之后没有恢复到「一个在跑」的状态");
        }
        finally
        {
            await engine.DisposeAsync();
            try { Directory.Delete(workDir, true); } catch { }
        }
    }

    [Fact]
    public async Task PauseSingleTask_DoesNotBecomeCompleted()
    {
        // 真机复现过的严重缺陷：取消被 worker 正常吞掉 → DownloadAsync 当"成功"返回
        // → 任务显示「已完成 2.12 MB / 6 MB」。暂停必须是暂停，不能变成完成。
        var engine = IdmTestEnv.CreateEngine(out _, out var workDir, out var dl);
        using var server = new TestEnv.FileServer();
        server.AddFile("p.bin", new byte[2 * 1024 * 1024], null, 8 * 1024, 100);

        try
        {
            await engine.StartAsync();
            var task = await engine.AddTaskAsync(new NewTaskRequest
            {
                Urls = new List<string> { server.Url("p.bin") },
                Directory = dl,
            });

            await WaitTasksAsync(engine,
                list => list.Any(t => t.Gid == task.Gid && t.CompletedLength > 0),
                25000, "下载没有开始");

            await engine.PauseAsync(task.Gid);
            var paused = (await WaitTasksAsync(engine,
                list => list.Any(t => t.Gid == task.Gid && t.State == TaskState.Paused),
                20000, "暂停没有生效")).Single(t => t.Gid == task.Gid);

            Assert.True(paused.CompletedLength < paused.TotalLength,
                $"暂停时进度异常：{paused.CompletedLength}/{paused.TotalLength}");

            // 保持 3 秒，确认不会自己滑向"已完成"
            await Task.Delay(3000);
            var after = (await engine.GetAllTasksAsync()).Single(t => t.Gid == task.Gid);
            Assert.Equal(TaskState.Paused, after.State);
            Assert.True(after.CompletedLength < after.TotalLength);
        }
        finally
        {
            await engine.DisposeAsync();
            try { Directory.Delete(workDir, true); } catch { }
        }
    }

    [Fact]
    public async Task PauseThenResume_ContinuesWithoutHandleConflict()
    {
        // 真机复现：暂停后立刻继续，旧 runner 还握着 .kdpart 句柄 → "文件正被另一个进程使用"。
        var engine = IdmTestEnv.CreateEngine(out _, out var workDir, out var dl);
        using var server = new TestEnv.FileServer();
        var payload = new byte[1024 * 1024];
        new Random(7).NextBytes(payload);
        server.AddFile("pr.bin", payload, null, 8 * 1024, 60);

        try
        {
            await engine.StartAsync();
            var task = await engine.AddTaskAsync(new NewTaskRequest
            {
                Urls = new List<string> { server.Url("pr.bin") },
                Directory = dl,
            });

            await WaitTasksAsync(engine,
                list => list.Any(t => t.Gid == task.Gid && t.CompletedLength > 0),
                25000, "下载没有开始");

            await engine.PauseAsync(task.Gid);
            await WaitTasksAsync(engine,
                list => list.Any(t => t.Gid == task.Gid && t.State == TaskState.Paused),
                20000, "暂停没有生效");

            await engine.ResumeAsync(task.Gid);
            var done = (await WaitTasksAsync(engine,
                list => list.Any(t => t.Gid == task.Gid && t.State == TaskState.Completed),
                90000, "继续之后没有完成（可能是断点句柄冲突）")).Single(t => t.Gid == task.Gid);

            Assert.Equal(payload.Length, done.TotalLength);
            Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(dl, "pr.bin")));
        }
        finally
        {
            await engine.DisposeAsync();
            try { Directory.Delete(workDir, true); } catch { }
        }
    }

    [Fact]
    public async Task BatchAdd_RoutesHttpTasksToIdmEngine()
    {
        // 真机复现：界面批量粘贴多条链接时走的是 AddTasksAsync，而它原来直接调 aria2，
        // gid 是纯 hex —— 协议等于没换。这条用例钉住"批量也走新引擎"。
        var engine = IdmTestEnv.CreateEngine(out _, out var workDir, out var dl);
        using var server = new TestEnv.FileServer();
        server.AddFile("b1.bin", new byte[256 * 1024]);
        server.AddFile("b2.bin", new byte[256 * 1024]);

        try
        {
            await engine.StartAsync();
            var results = await engine.AddTasksAsync(new[]
            {
                new NewTaskRequest { Urls = new List<string> { server.Url("b1.bin") }, Directory = dl },
                new NewTaskRequest { Urls = new List<string> { server.Url("b2.bin") }, Directory = dl },
            });

            Assert.Equal(2, results.Count);
            Assert.All(results, r => Assert.StartsWith("idm-", r.Gid, StringComparison.Ordinal));

            await WaitTasksAsync(engine,
                list => list.Count == 2 && list.All(t => t.State == TaskState.Completed),
                60000, "批量任务没有全部完成");

            Assert.True(File.Exists(Path.Combine(dl, "b1.bin")));
            Assert.True(File.Exists(Path.Combine(dl, "b2.bin")));
        }
        finally
        {
            await engine.DisposeAsync();
            try { Directory.Delete(workDir, true); } catch { }
        }
    }

    [Fact]
    public async Task QueuedTask_StartsAfterSlotFrees()
    {
        var engine = IdmTestEnv.CreateEngine(out _, out var workDir, out var dl);
        using var server = new TestEnv.FileServer();
        server.AddFile("q1.bin", new byte[256 * 1024], null, 8 * 1024, 40);
        server.AddFile("q2.bin", new byte[256 * 1024], null, 8 * 1024, 40);

        try
        {
            await engine.StartAsync();
            await engine.SetMaxConcurrentDownloadsAsync(1);

            var first = await engine.AddTaskAsync(new NewTaskRequest
            {
                Urls = new List<string> { server.Url("q1.bin") },
                Directory = dl,
            });
            await engine.AddTaskAsync(new NewTaskRequest
            {
                Urls = new List<string> { server.Url("q2.bin") },
                Directory = dl,
            });

            await WaitTasksAsync(engine, list => list.Count(t => t.State == TaskState.Active) == 1,
                25000, "第二个任务没有排队");

            // 第一个跑完 → 名额释放 → 排队的那个必须被拉起来
            await WaitTasksAsync(engine, list =>
                    list.Count == 2
                    && list.Any(t => t.Gid == first.Gid && t.State == TaskState.Completed)
                    && list.Count(t => t.State == TaskState.Active) == 1,
                90000, "第一个任务完成后，排队任务没有被拉起");
        }
        finally
        {
            await engine.DisposeAsync();
            try { Directory.Delete(workDir, true); } catch { }
        }
    }
}
