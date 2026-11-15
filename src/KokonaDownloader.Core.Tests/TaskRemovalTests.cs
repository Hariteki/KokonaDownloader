using KokonaDownloader.Core.Engine;
using KokonaDownloader.Core.Engine.Idm;

namespace KokonaDownloader.Core.Tests;

/// <summary>
/// 删除任务 / 清空列表。
///
/// 背景（用户报的现象）："任务列表很大概率删不了"。
/// 根因：<c>IdmTaskManager.Restore</c> 只重建**未完成**的任务，重启后已完成/失败的
/// `idm-*` 任务只剩本地元数据与历史行，不在内存任务池里；而 <c>RemoveAsync</c> 的 IDM 分支
/// 一旦 `_idm.Remove` 返回 false 就抛"任务不存在"，于是这些行永远删不掉。
///
/// 这里用**引擎不可用**的配置（不连 aria2）覆盖 IDM 分支：删除 IDM 记录本来就不需要 aria2，
/// 只有这种构造方式才能稳定复现"只剩记录"的状态。
/// </summary>
public class TaskRemovalTests
{
    private static (DownloadEngine Engine, TaskStore Store, string WorkDir) NewEngineWithStore()
    {
        var work = TestEnv.NewWorkDir();
        var store = new TaskStore(Path.Combine(work, "tasks.json"));
        var config = new EngineConfig
        {
            Aria2Path = Path.Combine(work, "aria2c-不存在.exe"),
            WorkDir = Path.Combine(work, "engine"),
            DefaultDownloadDir = work,
            RpcPort = TestEnv.GetFreePort(),
            RpcSecret = "dead-engine-secret",
            BtEnabled = false,
            PollIntervalMs = 200,
        };
        return (new DownloadEngine(config, store), store, work);
    }

    private static TaskMeta FinishedIdmMeta(string gid, string dir, string name = "payload.bin") => new()
    {
        Gid = gid,
        Name = name,
        Urls = new List<string> { $"https://example.invalid/{name}" },
        Dir = dir,
        FilePath = Path.Combine(dir, name),
        TotalLength = 2048,
        CompletedLength = 2048,
        AddedAt = DateTime.Now.AddMinutes(-10),
        FinishedAt = DateTime.Now.AddMinutes(-9),
        FinalState = "Completed",
        IsBt = false,
    };

    [Fact]
    public async Task 重启后的已完成IDM任务能从列表删除()
    {
        var (engine, store, work) = NewEngineWithStore();
        try
        {
            const string gid = "idm-20261103114601183";
            store.AddMeta(gid, FinishedIdmMeta(gid, work));
            Assert.NotNull(store.GetMeta(gid));

            // 修复前这里抛 InvalidOperationException("任务 ... 不存在")，界面表现为"点了没反应"
            var ex = await Record.ExceptionAsync(() => engine.RemoveAsync(gid));
            Assert.Null(ex);

            Assert.Null(store.GetMeta(gid));
            // 这里不查 GetAllTasksAsync：本用例刻意用"引擎不可用"的配置，列表读取会走 RPC 直接失败。
            // "这一行真的从列表消失"由 ApiServiceTests.清空任务列表接口 用真引擎端到端覆盖，
            // 以及连删两次_第二次才报不存在（第二次报"不存在"证明记录确实没了）。
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }

    [Fact]
    public async Task 连删两次_第二次才报不存在()
    {
        var (engine, store, work) = NewEngineWithStore();
        try
        {
            const string gid = "idm-20261103114601184";
            store.AddMeta(gid, FinishedIdmMeta(gid, work));

            await engine.RemoveAsync(gid);
            // 真的不存在的 gid 仍要报错：否则 API 会把"删了个空气"当成成功。
            // 但必须是 TaskNotFoundException 这个具体类型——界面靠它把"连点两次"当成成功，
            // 不能靠比字符串（文案一改就废）。
            await Assert.ThrowsAsync<TaskNotFoundException>(() => engine.RemoveAsync(gid));
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }

    [Fact]
    public async Task 清空列表移除全部记录且不删文件()
    {
        var (engine, store, work) = NewEngineWithStore();
        try
        {
            string gid1 = "idm-20261103114601185";
            string gid2 = "idm-20261103114601186";
            store.AddMeta(gid1, FinishedIdmMeta(gid1, work, "a.bin"));
            store.AddMeta(gid2, FinishedIdmMeta(gid2, work, "b.bin"));

            // 文件必须留着：清空列表只清记录
            string file = Path.Combine(work, "a.bin");
            await File.WriteAllBytesAsync(file, new byte[16]);

            int removed = await engine.ClearAllAsync();

            Assert.Equal(2, removed);
            Assert.Null(store.GetMeta(gid1));
            Assert.Null(store.GetMeta(gid2));
            Assert.True(File.Exists(file), "清空列表不应该删除已下载的文件");
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }

    [Fact]
    public async Task 清空空列表返回0()
    {
        var (engine, _, work) = NewEngineWithStore();
        try
        {
            Assert.Equal(0, await engine.ClearAllAsync());
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }

    [Fact]
    public async Task 删除下载中的任务_断点不会留在盘上()
    {
        // 实测过：删除/清空一个正在下载的任务，列表里没了，但 .kdpart 因为句柄未释放而删不掉，
        // 一个 16MB 的任务就漏 16MB。这里钉住"runner 退出后必须清干净"。
        string work = TestEnv.NewWorkDir();
        string parts = Path.Combine(work, "parts");
        Directory.CreateDirectory(parts);
        try
        {
            var payload = new byte[4 * 1024 * 1024];
            using var server = new TestEnv.FileServer();
            server.AddFile("slow.bin", payload, chunkBytes: 32 * 1024, chunkDelayMs: 30);  // 约 1MB/s

            await using var manager = new IdmTaskManager(work, 4, null, 3, parts);
            manager.Add("idm-1", 1, new NewTaskRequest
            {
                Urls = new List<string> { server.Url("slow.bin") },
                Directory = work,
                Connections = 4,
            });

            string part = Path.Combine(parts, "idm-1.kdpart");
            for (int i = 0; i < 40 && !File.Exists(part); i++) await Task.Delay(50);
            Assert.True(File.Exists(part), "任务没开始写断点，测不到目标场景");

            Assert.True(manager.Remove("idm-1", deleteFile: false));

            for (int i = 0; i < 60 && File.Exists(part); i++) await Task.Delay(100);
            Assert.False(File.Exists(part), "断点没清理，会一直占着磁盘");
            Assert.False(File.Exists(part + ".state.json"));
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }

    [Fact]
    public async Task 启动时清掉孤儿断点_但保留有任务的()
    {
        string work = TestEnv.NewWorkDir();
        string parts = Path.Combine(work, "parts");
        Directory.CreateDirectory(parts);
        try
        {
            File.WriteAllBytes(Path.Combine(parts, "idm-1.kdpart"), new byte[1024]);
            File.WriteAllText(Path.Combine(parts, "idm-1.kdpart.state.json"), "{}");
            File.WriteAllBytes(Path.Combine(parts, "idm-2.kdpart"), new byte[2048]);

            await using var manager = new IdmTaskManager(work, 4, null, 3, parts);
            var known = new List<TaskMeta>
            {
                new() { Gid = "idm-1", Urls = new List<string> { "https://example.invalid/a" }, Dir = work },
            };

            int removed = manager.SweepOrphanParts(known, TimeSpan.Zero);

            Assert.Equal(1, removed);
            Assert.True(File.Exists(Path.Combine(parts, "idm-1.kdpart")), "有任务引用的断点不能删");
            Assert.False(File.Exists(Path.Combine(parts, "idm-2.kdpart")), "孤儿断点应当被清掉");
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }

    [Fact]
    public async Task 孤儿断点清理默认不动当天的文件()
    {
        // 保护条款：元数据可能因为崩溃没落盘，当天刚下的断点要留给用户续传。
        string work = TestEnv.NewWorkDir();
        string parts = Path.Combine(work, "parts");
        Directory.CreateDirectory(parts);
        try
        {
            string fresh = Path.Combine(parts, "idm-2.kdpart");
            File.WriteAllBytes(fresh, new byte[1024]);

            await using var manager = new IdmTaskManager(work, 4, null, 3, parts);
            int removed = manager.SweepOrphanParts(new List<TaskMeta>());   // 默认 24 小时

            Assert.Equal(0, removed);
            Assert.True(File.Exists(fresh), "当天的断点不能当成孤儿删掉");
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }
}
