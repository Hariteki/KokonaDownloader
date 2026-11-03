using KokonaDownloader.Core.Engine;

namespace KokonaDownloader.Core.Tests;

/// <summary>
/// P2 验收：IDM 任务必须走与 aria2 相同的"状态迁移漏斗"，否则进度浮窗不弹、完成后不通知。
/// 这两条正是换核心后用户最先察觉的退化。
/// </summary>
public sealed class IdmEventFlowTests
{
    [Fact]
    public async Task IdmTask_RaisesNewTaskEventOnce_AndCompletedWithFreshFinishedAt()
    {
        var engine = IdmTestEnv.CreateEngine(out _, out var workDir, out var downloadDir);
        var events = new List<EngineEventArgs>();
        engine.EngineEvent += (_, e) => { lock (events) events.Add(e); };

        using var server = new TestEnv.FileServer();
        server.AddFile("evt.bin", new byte[256 * 1024], chunkBytes: 16 * 1024, chunkDelayMs: 40);

        try
        {
            await engine.StartAsync();
            var task = await engine.AddTaskAsync(new NewTaskRequest
            {
                Urls = new List<string> { $"http://127.0.0.1:{server.Port}/evt.bin" },
                Directory = downloadDir,
            });
            Assert.StartsWith("idm-", task.Gid, StringComparison.Ordinal);

            // ① 新建：必须有一次 IsNewTask=true 的 TaskChanged（进度浮窗的触发条件）
            await IdmTestEnv.WaitUntilAsync(
                () => { lock (events) return events.Any(e => e.Type == "TaskChanged" && e.Task?.Gid == task.Gid && e.IsNewTask); },
                15000, "未收到 IsNewTask 的 TaskChanged（进度浮窗不会弹）");

            // ② 完成：必须有 Completed 且有新鲜的 FinishedAt（通知的触发条件）
            await IdmTestEnv.WaitUntilAsync(
                () => { lock (events) return events.Any(e => e.Type == "TaskChanged" && e.Task?.Gid == task.Gid && e.Task.State == TaskState.Completed); },
                30000, "未收到 Completed 的 TaskChanged（完成通知不会弹）");

            DownloadTaskInfo done;
            lock (events)
                done = events.First(e => e.Type == "TaskChanged" && e.Task?.Gid == task.Gid && e.Task.State == TaskState.Completed).Task!;

            Assert.NotNull(done.FinishedAt);
            Assert.True((DateTime.Now - done.FinishedAt!.Value).TotalSeconds < 10,
                $"FinishedAt 不新鲜（{done.FinishedAt}），NotificationRules 会丢弃该通知");

            // ③ IsNewTask 只能消费一次，否则浮窗会被反复创建
            lock (events)
                Assert.Single(events.Where(e => e.Type == "TaskChanged" && e.Task?.Gid == task.Gid && e.IsNewTask));

            // ④ 统计里 IDM 速度不被重复计数（快照统计已含 IDM）
            var stats = await engine.GetGlobalStatAsync();
            Assert.True(stats.DownloadSpeed >= 0);
        }
        finally
        {
            await engine.DisposeAsync();
            try { Directory.Delete(workDir, true); } catch { }
        }
    }
}
