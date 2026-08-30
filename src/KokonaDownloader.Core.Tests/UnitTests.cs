using KokonaDownloader.Core.Engine;

namespace KokonaDownloader.Core.Tests;

public class ModelTests
{
    [Fact]
    public void Progress_计算正确()
    {
        var t = new DownloadTaskInfo { TotalLength = 1000, CompletedLength = 250 };
        Assert.Equal(0.25, t.Progress, 5);
    }

    [Fact]
    public void Progress_总长为零时为零()
    {
        var t = new DownloadTaskInfo { TotalLength = 0, CompletedLength = 0 };
        Assert.Equal(0, t.Progress);
    }

    [Fact]
    public void Eta_有速度时正确()
    {
        var t = new DownloadTaskInfo { TotalLength = 2000, CompletedLength = 1000, DownloadSpeed = 500 };
        Assert.NotNull(t.Eta);
        Assert.Equal(2, t.Eta!.Value.TotalSeconds, 1);
    }

    [Fact]
    public void Eta_无速度时为null()
    {
        var t = new DownloadTaskInfo { TotalLength = 2000, CompletedLength = 1000, DownloadSpeed = 0 };
        Assert.Null(t.Eta);
    }

    [Fact]
    public void GenerateSecret_长度与随机性()
    {
        var s1 = EngineConfig.GenerateSecret();
        var s2 = EngineConfig.GenerateSecret();
        Assert.Equal(32, s1.Length);
        Assert.NotEqual(s1, s2);
    }
}

public class TaskStoreTests : IDisposable
{
    private readonly string _dir = TestEnv.NewWorkDir();

    [Fact]
    public void 添加与读取元数据()
    {
        var store = new TaskStore(Path.Combine(_dir, "tasks.json"));
        store.AddMeta("abc", new TaskMeta { Gid = "abc", Name = "test.zip", Urls = new() { "http://x/y" } });
        store.SaveNow();

        var store2 = new TaskStore(Path.Combine(_dir, "tasks.json"));
        var meta = store2.GetMeta("abc");
        Assert.NotNull(meta);
        Assert.Equal("test.zip", meta!.Name);
        Assert.Single(meta.Urls);
    }

    [Fact]
    public void 删除元数据并持久化()
    {
        var path = Path.Combine(_dir, "tasks2.json");
        var store = new TaskStore(path);
        store.AddMeta("g1", new TaskMeta { Gid = "g1", Name = "a" });
        store.AddMeta("g2", new TaskMeta { Gid = "g2", Name = "b" });
        store.RemoveMeta("g1");
        store.SaveNow();

        var store2 = new TaskStore(path);
        Assert.Null(store2.GetMeta("g1"));
        Assert.NotNull(store2.GetMeta("g2"));
    }

    [Fact]
    public void 损坏文件不抛异常()
    {
        var path = Path.Combine(_dir, "bad.json");
        File.WriteAllText(path, "{not valid json!!!");
        var store = new TaskStore(path); // 不应抛异常
        Assert.Empty(store.All());
    }

    [Fact]
    public void UpdateFinished_记录完成状态()
    {
        var store = new TaskStore(Path.Combine(_dir, "tasks3.json"));
        store.AddMeta("g9", new TaskMeta { Gid = "g9", Name = "" });
        store.UpdateFinished(new DownloadTaskInfo { Gid = "g9", Name = "final.bin", State = TaskState.Completed });
        var meta = store.GetMeta("g9");
        Assert.Equal("final.bin", meta!.Name);
        Assert.Equal("Completed", meta.FinalState);
        Assert.NotNull(meta.FinishedAt);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }
}
