using KokonaDownloader.Core.Engine;

namespace KokonaDownloader.Core.Tests;

/// <summary>
/// 集成测试：真实启动 aria2 + 本地 HTTP 文件服务器，验证完整下载链路。
/// </summary>
public class EngineIntegrationTests : IAsyncLifetime
{
    private TestEnv.FileServer _server = null!;
    private DownloadEngine _engine = null!;
    private string _workDir = null!;
    private string _downloadDir = null!;
    private int _rpcPort;

    public async Task InitializeAsync()
    {
        _server = new TestEnv.FileServer();
        _workDir = TestEnv.NewWorkDir();
        _downloadDir = Path.Combine(_workDir, "downloads");
        Directory.CreateDirectory(_downloadDir);
        _rpcPort = TestEnv.GetFreePort();

        var config = new EngineConfig
        {
            Aria2Path = TestEnv.Aria2Path,
            WorkDir = _workDir,
            DefaultDownloadDir = _downloadDir,
            RpcPort = _rpcPort,
            RpcSecret = "test-secret-123",
            MaxConcurrentDownloads = 3,
            DefaultConnections = 4,
            PollIntervalMs = 300
        };
        _engine = new DownloadEngine(config, new TaskStore(Path.Combine(_workDir, "tasks.json")));
        await _engine.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _engine.DisposeAsync();
        _server.Dispose();
        try { Directory.Delete(_workDir, true); } catch { }
    }

    [Fact]
    public async Task 引擎启动后可Ping通()
    {
        Assert.True(_engine.IsRunning);
        var stat = await _engine.GetGlobalStatAsync();
        Assert.NotNull(stat);
    }

    [Fact]
    public async Task 单文件下载完整流程()
    {
        var content = new byte[2 * 1024 * 1024]; // 2MB
        new Random(42).NextBytes(content);
        _server.AddFile("big.bin", content);

        var task = await _engine.AddTaskAsync(new NewTaskRequest
        {
            Urls = new List<string> { _server.Url("big.bin") },
            Connections = 4
        });
        Assert.False(string.IsNullOrEmpty(task.Gid));

        // 等待完成（最多 30 秒）
        var deadline = DateTime.Now.AddSeconds(30);
        DownloadTaskInfo? info = null;
        while (DateTime.Now < deadline)
        {
            info = await _engine.GetTaskAsync(task.Gid);
            if (info?.State == TaskState.Completed) break;
            await Task.Delay(300);
        }

        Assert.NotNull(info);
        Assert.Equal(TaskState.Completed, info!.State);
        Assert.Equal(content.Length, info.TotalLength);
        Assert.NotNull(info.FilePath);
        Assert.True(File.Exists(info.FilePath));

        var downloaded = await File.ReadAllBytesAsync(info.FilePath!);
        Assert.Equal(content.Length, downloaded.Length);
        Assert.True(content.SequenceEqual(downloaded), "下载内容校验失败");
    }

    [Fact]
    public async Task 暂停与恢复()
    {
        var content = new byte[5 * 1024 * 1024];
        _server.AddFile("pause.bin", content);

        var task = await _engine.AddTaskAsync(new NewTaskRequest
        {
            Urls = new List<string> { _server.Url("pause.bin") },
            Connections = 1
        });
        await _engine.PauseAsync(task.Gid);
        await Task.Delay(500);
        var paused = await _engine.GetTaskAsync(task.Gid);
        Assert.Equal(TaskState.Paused, paused!.State);

        await _engine.ResumeAsync(task.Gid);
        await Task.Delay(500);
        var resumed = await _engine.GetTaskAsync(task.Gid);
        Assert.True(resumed!.State is TaskState.Active or TaskState.Waiting or TaskState.Completed,
            $"恢复后状态异常: {resumed.State}");
    }

    [Fact]
    public async Task 删除任务()
    {
        _server.AddFile("del.bin", new byte[1024]);
        var task = await _engine.AddTaskAsync(new NewTaskRequest
        {
            Urls = new List<string> { _server.Url("del.bin") }
        });
        await Task.Delay(800);
        await _engine.RemoveAsync(task.Gid, deleteFile: true);
        await Task.Delay(500);

        var all = await _engine.GetAllTasksAsync();
        Assert.DoesNotContain(all, t => t.Gid == task.Gid);
    }

    [Fact]
    public async Task 失败任务状态为Failed()
    {
        var task = await _engine.AddTaskAsync(new NewTaskRequest
        {
            Urls = new List<string> { _server.Url("not-exist-404.bin") }
        });
        var deadline = DateTime.Now.AddSeconds(20);
        DownloadTaskInfo? info = null;
        while (DateTime.Now < deadline)
        {
            info = await _engine.GetTaskAsync(task.Gid);
            if (info?.State == TaskState.Failed) break;
            await Task.Delay(300);
        }
        Assert.Equal(TaskState.Failed, info!.State);
        Assert.True(info.ErrorCode != 0);
    }

    [Fact]
    public async Task 批量添加与全局限速()
    {
        _server.AddFile("batch1.bin", new byte[64 * 1024]);
        _server.AddFile("batch2.bin", new byte[64 * 1024]);

        var tasks = await _engine.AddTasksAsync(new[]
        {
            new NewTaskRequest { Urls = new List<string> { _server.Url("batch1.bin") } },
            new NewTaskRequest { Urls = new List<string> { _server.Url("batch2.bin") } }
        });
        Assert.Equal(2, tasks.Count);

        // 全局限速设置不应抛异常
        await _engine.SetGlobalSpeedLimitAsync(10 * 1024 * 1024);
        await _engine.SetGlobalSpeedLimitAsync(0);

        var deadline = DateTime.Now.AddSeconds(30);
        while (DateTime.Now < deadline)
        {
            var all = await _engine.GetAllTasksAsync();
            var mine = all.Where(t => tasks.Any(x => x.Gid == t.Gid)).ToList();
            if (mine.Count == 2 && mine.All(t => t.State == TaskState.Completed)) return;
            await Task.Delay(300);
        }
        Assert.Fail("批量任务未在时限内完成");
    }

    [Fact]
    public async Task 重新下载()
    {
        _server.AddFile("redl.bin", new byte[32 * 1024]);
        var task = await _engine.AddTaskAsync(new NewTaskRequest
        {
            Urls = new List<string> { _server.Url("redl.bin") }
        });
        var deadline = DateTime.Now.AddSeconds(20);
        while (DateTime.Now < deadline)
        {
            var s = await _engine.GetTaskAsync(task.Gid);
            if (s?.State == TaskState.Completed) break;
            await Task.Delay(300);
        }

        var newTask = await _engine.RedownloadAsync(task.Gid);
        Assert.NotEqual(task.Gid, newTask.Gid);
        deadline = DateTime.Now.AddSeconds(20);
        while (DateTime.Now < deadline)
        {
            var s = await _engine.GetTaskAsync(newTask.Gid);
            if (s?.State == TaskState.Completed) return;
            await Task.Delay(300);
        }
        Assert.Fail("重新下载未完成");
    }

    [Fact]
    public async Task 事件推送任务状态变化()
    {
        var events = new List<EngineEventArgs>();
        _engine.EngineEvent += (_, e) => { lock (events) events.Add(e); };

        _server.AddFile("evt.bin", new byte[128 * 1024]);
        await _engine.AddTaskAsync(new NewTaskRequest
        {
            Urls = new List<string> { _server.Url("evt.bin") }
        });

        var deadline = DateTime.Now.AddSeconds(20);
        while (DateTime.Now < deadline)
        {
            lock (events)
            {
                if (events.Any(e => e.Type == "TaskChanged" && e.Task?.State == TaskState.Completed))
                    return;
            }
            await Task.Delay(200);
        }
        Assert.Fail("未收到任务完成事件");
    }
}
