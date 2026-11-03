using KokonaDownloader.Core.Engine;
using KokonaDownloader.Core.Engine.Idm;

namespace KokonaDownloader.Core.Tests;

/// <summary>
/// P1 验收：IDM 任务的 gid 与编号跟 aria2 路径同源，且元数据真的落进 tasks.json。
/// 这是"重启后不撞车、历史行不被覆盖"的前提。
/// </summary>
public sealed class IdmPersistenceTests
{
    [Fact]
    public async Task AddTask_WritesMetadata_AndUsesMonotonicTaskNumber()
    {
        var engine = IdmTestEnv.CreateEngine(out var store, out var workDir, out var downloadDir);
        using var server = new TestEnv.FileServer();
        server.AddFile("p1-a.bin", new byte[64 * 1024]);
        server.AddFile("p1-b.bin", new byte[64 * 1024]);

        try
        {
            await engine.StartAsync();
            string baseUrl = $"http://127.0.0.1:{server.Port}";

            var t1 = await engine.AddTaskAsync(new NewTaskRequest
            {
                Urls = new List<string> { $"{baseUrl}/p1-a.bin" },
                Directory = downloadDir,
            });
            var t2 = await engine.AddTaskAsync(new NewTaskRequest
            {
                Urls = new List<string> { $"{baseUrl}/p1-b.bin" },
                Directory = downloadDir,
            });

            // gid 形状：idm-{taskNumber}
            Assert.StartsWith("idm-", t1.Gid, StringComparison.Ordinal);
            Assert.Equal($"idm-{t1.TaskNumber}", t1.Gid);
            Assert.Equal($"idm-{t2.TaskNumber}", t2.Gid);

            // 编号单调递增（重启不撞车的前提）
            Assert.True(t2.TaskNumber > t1.TaskNumber,
                $"taskNumber 未递增：{t1.TaskNumber} → {t2.TaskNumber}");

            // 元数据落盘：防抖 500ms，直接读 store 更稳（避免测试等盘）
            var meta = store.GetMeta(t1.Gid);
            Assert.NotNull(meta);
            Assert.Equal(t1.TaskNumber, meta!.TaskNumber);
            Assert.False(meta.IsBt);
            Assert.Equal($"{baseUrl}/p1-a.bin", meta.Urls[0]);

            // 管理器自己也认这个 gid（API 的 pause/resume/remove 依赖它）
            Assert.True(engine.IsIdmTask(t1.Gid), "引擎门面不认识刚创建的 idm gid");
        }
        finally
        {
            await engine.DisposeAsync();
            try { Directory.Delete(workDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnknownIdmGid_ThrowsInsteadOfSilentlySucceeding()
    {
        var engine = IdmTestEnv.CreateEngine(out _, out var workDir, out _);
        try
        {
            await engine.StartAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => engine.PauseAsync("idm-99999999999999999"));
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => engine.RemoveAsync("idm-99999999999999999"));
        }
        finally
        {
            await engine.DisposeAsync();
            try { Directory.Delete(workDir, true); } catch { }
        }
    }
}
