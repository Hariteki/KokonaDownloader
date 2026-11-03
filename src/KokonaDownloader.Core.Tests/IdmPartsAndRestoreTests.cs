using KokonaDownloader.Core.Engine;
using KokonaDownloader.Core.Engine.Idm;

namespace KokonaDownloader.Core.Tests;

/// <summary>
/// P2.5 / P2.6 验收：
///   1. 断点文件（.kdpart + 状态）落在应用数据目录，下载目录里只有完成后的目标文件；
///   2. 重启后未完成的任务仍在列表里、状态为「已暂停」、进度保留，点继续能续传成功。
/// 这两条直接决定「换了下载核心之后，用起来还像不像原来那个应用」。
/// </summary>
public sealed class IdmPartsAndRestoreTests
{
    private static byte[] Payload(int size = 2 * 1024 * 1024)
    {
        var buf = new byte[size];
        var rnd = new Random(20261030);
        rnd.NextBytes(buf);
        return buf;
    }

    [Fact]
    public async Task PartFile_LivesInPartsDir_AndDisappearsAfterCompletion()
    {
        string workDir = TestEnv.NewWorkDir();
        string dl = Path.Combine(workDir, "dl");
        string parts = Path.Combine(workDir, "parts");
        Directory.CreateDirectory(dl);

        var payload = Payload();
        using var server = new TestEnv.FileServer();
        // 放慢到秒级，才能在中途观察到断点位置
        server.AddFile("p.bin", payload, chunkBytes: 16 * 1024, chunkDelayMs: 25);

        try
        {
            await using var downloader = new IdmHttpDownloader();
            var opts = new IdmDownloadOptions
            {
                Url = $"http://127.0.0.1:{server.Port}/p.bin",
                Directory = dl,
                FileName = "p.bin",
                PartsDirectory = parts,
                PartKey = "idm-20261030120000001",
                MaxConnections = 4,
                MinSplitSize = 64 * 1024,
            };
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var task = downloader.DownloadAsync(opts, null, cts.Token);

            string part = Path.Combine(parts, "idm-20261030120000001.kdpart");
            await IdmTestEnv.WaitUntilAsync(() => File.Exists(part), 15000, "断点没有落在 parts 目录");

            // 关键断言：下载目录里不该出现任何中间文件
            Assert.False(File.Exists(Path.Combine(dl, "p.bin.kdpart")),
                "下载目录里出现了 .kdpart —— 用户会看到中间文件");
            Assert.False(File.Exists(Path.Combine(dl, "p.bin")),
                "下载未完成时不该在下载目录里出现目标文件");

            await task;

            Assert.True(File.Exists(Path.Combine(dl, "p.bin")), "完成后目标文件应出现在下载目录");
            Assert.False(File.Exists(part), "完成后断点文件必须被清理");
            Assert.False(File.Exists(part + ".state.json"), "完成后状态文件必须被清理");

            var actual = await File.ReadAllBytesAsync(Path.Combine(dl, "p.bin"));
            Assert.Equal(payload, actual);
        }
        finally
        {
            try { Directory.Delete(workDir, true); } catch { }
        }
    }

    [Fact]
    public async Task Restart_RestoresUnfinishedTaskAsPaused_ThenResumeCompletes()
    {
        string workDir = TestEnv.NewWorkDir();
        string dl = Path.Combine(workDir, "dl");
        string storePath = Path.Combine(workDir, "tasks.json");
        Directory.CreateDirectory(dl);

        var payload = Payload();
        using var server = new TestEnv.FileServer();
        // 单连接 + 慢速：2MB 约 5 秒，才有稳定的"下到一半"窗口可供暂停
        server.AddFile("r.bin", payload, chunkBytes: 16 * 1024, chunkDelayMs: 40);

        string gid;
        long progressAtPause;

        // ---- 第一次运行：下一部分后暂停，模拟"用户关掉应用" ----
        {
            var store = new TaskStore(storePath);
            var engine = new DownloadEngine(IdmTestEnv.Config(workDir, dl, connections: 1), store);
            try
            {
                await engine.StartAsync();
                var task = await engine.AddTaskAsync(new NewTaskRequest
                {
                    Urls = new List<string> { $"http://127.0.0.1:{server.Port}/r.bin" },
                    Directory = dl,
                });
                gid = task.Gid;
                Assert.StartsWith("idm-", gid, StringComparison.Ordinal);

                // 等到真的下了一部分，才有"续传"这回事
                var mid = await WaitTaskAsync(engine, gid,
                    t => t.CompletedLength > 256 * 1024 && t.TotalLength == payload.Length, 30000);
                progressAtPause = mid.CompletedLength;

                await engine.PauseAsync(gid);
                await WaitTaskAsync(engine, gid, t => t.State == TaskState.Paused, 20000);

                store.SaveNow();   // 防抖写盘是 500ms，退出前必须落盘
            }
            finally
            {
                await engine.DisposeAsync();
            }
        }

        // ---- 第二次运行：全新引擎 + 全新 store（真正从磁盘恢复） ----
        var store2 = new TaskStore(storePath);
        var engine2 = new DownloadEngine(IdmTestEnv.Config(workDir, dl, connections: 1), store2);
        try
        {
            await engine2.StartAsync();

            // 恢复必须是「已暂停」，不能自动开跑（与 aria2 会话恢复一致）
            var restored = await engine2.GetTaskAsync(gid);
            Assert.NotNull(restored);
            Assert.Equal(TaskState.Paused, restored!.State);
            Assert.Equal(payload.Length, restored.TotalLength);
            Assert.True(restored.CompletedLength >= progressAtPause,
                $"恢复后进度丢失：暂停时 {progressAtPause}，恢复后 {restored.CompletedLength}");

            // 继续 → 必须能续传完成，且内容逐字节一致
            await engine2.ResumeAsync(gid);
            await WaitTaskAsync(engine2, gid, t => t.State == TaskState.Completed, 60000);

            var file = Path.Combine(dl, "r.bin");
            Assert.True(File.Exists(file), "续传完成后目标文件不存在");
            Assert.Equal(payload, await File.ReadAllBytesAsync(file));
        }
        finally
        {
            await engine2.DisposeAsync();
            try { Directory.Delete(workDir, true); } catch { }
        }
    }

    [Fact]
    public async Task Restart_WithoutPartFile_DoesNotResurrectGhostTask()
    {
        string workDir = TestEnv.NewWorkDir();
        string dl = Path.Combine(workDir, "dl");
        string storePath = Path.Combine(workDir, "tasks.json");
        Directory.CreateDirectory(dl);

        try
        {
            // 造一条"元数据在、断点没了"的残留：断点被用户/清理工具删掉的情形
            var store = new TaskStore(storePath);
            store.AddMeta("idm-20261030120000009", new TaskMeta
            {
                Gid = "idm-20261030120000009",
                TaskNumber = 20261030120000009,
                Name = "ghost.bin",
                Urls = new List<string> { "http://127.0.0.1:1/ghost.bin" },
                AddedAt = DateTime.Now.AddHours(-1),
                Dir = dl,
            });
            store.SaveNow();

            var engine = new DownloadEngine(IdmTestEnv.Config(workDir, dl), new TaskStore(storePath));
            try
            {
                await engine.StartAsync();
                Assert.Null(await engine.GetTaskAsync("idm-20261030120000009"));
                Assert.Empty(await engine.GetAllTasksAsync());
            }
            finally
            {
                await engine.DisposeAsync();
            }
        }
        finally
        {
            try { Directory.Delete(workDir, true); } catch { }
        }
    }

    [Fact]
    public void ChunkTable_Snapshot_SurvivesJsonRoundTrip()
    {
        // 回归：End 曾是字段，System.Text.Json 默认不序列化字段 → 续传状态里区间全变 0，
        // 恢复后所有段被判"已完成"，直接把残缺文件改名成目标文件。这条用例钉住 [JsonInclude]。
        var table = new ChunkTable();
        table.Initialize(1000);
        table.Claim(0);
        table.Split(0, 400);   // [0,400) + [400,1000)

        var json = System.Text.Json.JsonSerializer.Serialize(table.Snapshot());
        var back = System.Text.Json.JsonSerializer.Deserialize<List<ChunkTable.Segment>>(json)!;

        Assert.Equal(2, back.Count);
        Assert.Equal(400, back.Single(s => s.Id == 0).End);
        Assert.Equal(1000, back.Single(s => s.Id == 1).End);
        Assert.Equal(600, back.Single(s => s.Id == 1).Length);
    }

    [Fact]
    public async Task CorruptStateFile_IsRejected_AndFileIsReDownloaded()
    {
        string workDir = TestEnv.NewWorkDir();
        string dl = Path.Combine(workDir, "dl");
        string parts = Path.Combine(workDir, "parts");
        Directory.CreateDirectory(dl);
        Directory.CreateDirectory(parts);

        var payload = Payload(512 * 1024);
        using var server = new TestEnv.FileServer();
        server.AddFile("c.bin", payload);

        try
        {
            // 造一份"区间丢失"的坏状态（End 全是 0）——正是序列化缺陷会造成的样子
            string part = Path.Combine(parts, "idm-bad.kdpart");
            await File.WriteAllBytesAsync(part, new byte[payload.Length]);
            await File.WriteAllTextAsync(part + ".state.json",
                $"{{\"Url\":\"http://127.0.0.1:{server.Port}/c.bin\",\"FilePath\":\"\",\"TotalLength\":{payload.Length}," +
                "\"RangesSupported\":true,\"Segments\":[{\"Id\":0,\"Start\":0,\"End\":0,\"Written\":0,\"NextId\":-1}]}");

            await using var downloader = new IdmHttpDownloader();
            var opts = new IdmDownloadOptions
            {
                Url = $"http://127.0.0.1:{server.Port}/c.bin",
                Directory = dl,
                FileName = "c.bin",
                PartsDirectory = parts,
                PartKey = "idm-bad",
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var result = await downloader.DownloadAsync(opts, null, cts.Token);

            Assert.Equal(IdmTaskState.Completed, result.State);
            // 坏状态被丢弃 → 只能整份重下，所以服务端必须收到过请求，且内容正确
            Assert.True(server.RequestCountFor("c.bin") >= 1, "坏状态没有被丢弃，根本没有重新请求");
            Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(dl, "c.bin")));
        }
        finally
        {
            try { Directory.Delete(workDir, true); } catch { }
        }
    }

    [Fact]
    public async Task Checkpoint_IsOnDiskWhileDownloading_SoHardKillKeepsProgress()
    {
        // 被杀进程不会执行 catch 里的保存，"崩溃后还能续传"只能靠周期检查点。
        // 这条用例证明：下载进行中（远早于结束）状态文件已经在盘上，且带真实进度。
        string workDir = TestEnv.NewWorkDir();
        string dl = Path.Combine(workDir, "dl");
        string parts = Path.Combine(workDir, "parts");
        Directory.CreateDirectory(dl);

        var payload = Payload(3 * 1024 * 1024);
        using var server = new TestEnv.FileServer();
        server.AddFile("k.bin", payload, chunkBytes: 16 * 1024, chunkDelayMs: 40);

        try
        {
            await using var downloader = new IdmHttpDownloader();
            var opts = new IdmDownloadOptions
            {
                Url = $"http://127.0.0.1:{server.Port}/k.bin",
                Directory = dl,
                FileName = "k.bin",
                PartsDirectory = parts,
                PartKey = "idm-ckpt",
                MaxConnections = 1,   // 单连接确保下载够久，观察窗口稳定
            };
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var running = downloader.DownloadAsync(opts, null, cts.Token);

            string statePath = Path.Combine(parts, "idm-ckpt.kdpart.state.json");
            await IdmTestEnv.WaitUntilAsync(() => File.Exists(statePath), 20000, "检查点没有落盘");

            // 等到检查点里带上真实进度（不止是 0）
            long written = 0;
            await IdmTestEnv.WaitUntilAsync(() =>
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(statePath));
                    var root = doc.RootElement;
                    if (root.GetProperty("TotalLength").GetInt64() != payload.Length) return false;
                    written = root.GetProperty("Segments").EnumerateArray()
                        .Sum(s => s.GetProperty("Written").GetInt64());
                    return written > 0;
                }
                catch { return false; }
            }, 20000, "检查点里始终没有进度");

            Assert.True(written > 0, "检查点缺少已下载字节");

            cts.Cancel();
            try { await running; } catch (OperationCanceledException) { }
        }
        finally
        {
            try { Directory.Delete(workDir, true); } catch { }
        }
    }

    private static async Task<DownloadTaskInfo> WaitTaskAsync(DownloadEngine engine, string gid,
        Func<DownloadTaskInfo, bool> condition, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        DownloadTaskInfo? last = null;
        while (Environment.TickCount64 < deadline)
        {
            last = await engine.GetTaskAsync(gid);
            if (last is not null && condition(last)) return last;
            await Task.Delay(120);
        }
        throw new TimeoutException(
            $"任务 {gid} 未达到预期状态（最后：{last?.State} {last?.CompletedLength}/{last?.TotalLength}）");
    }
}
