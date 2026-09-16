using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KokonaDownloader.Core.Engine;
using KokonaDownloader.Core.Notifications;
using KokonaDownloader.Core.Settings;

namespace KokonaDownloader.Core.Tests;

/// <summary>
/// 持久化层健壮性测试：墓碑（防任务复活）、任务元数据、设置、已通知记录。
/// 这些文件都是"用户数据"：损坏或被误清空会导致断点续传状态丢失、密钥失配、通知重复等，
/// 但既有测试只覆盖了"损坏文件不抛异常"这一条。
/// </summary>
public class StoreRobustnessTests : IDisposable
{
    private readonly string _dir = TestEnv.NewWorkDir();

    private string P(string name) => Path.Combine(_dir, name);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    // ================= 墓碑：会话文件过滤 =================

    private static string SessionEntry(string url, string dir, string? outName = null)
    {
        var sb = new StringBuilder();
        sb.Append(url).Append('\n');
        sb.Append("  dir=").Append(dir).Append('\n');
        if (outName != null) sb.Append("  out=").Append(outName).Append('\n');
        return sb.ToString();
    }

    [Fact]
    public void 墓碑_命中条目被整块剔除未命中条目保留()
    {
        var dir = @"C:\Downloads";
        var file = P("aria2.session");
        File.WriteAllText(file, SessionEntry("http://a.example/deleted.bin", dir, "deleted.bin")
                              + SessionEntry("http://b.example/keep.bin", dir, "keep.bin"));

        var store = new TombstoneStore(P("tombstones.json"));
        store.Mark(new[] { "http://a.example/deleted.bin" }, dir);

        var purged = store.PurgeSessionFile(file);

        Assert.Equal(1, purged);
        var remaining = File.ReadAllText(file);
        Assert.DoesNotContain("deleted.bin", remaining);
        Assert.Contains("keep.bin", remaining);
    }

    [Fact]
    public void 墓碑_目录不同则不误删()
    {
        var file = P("aria2.session");
        File.WriteAllText(file, SessionEntry("http://a.example/file.bin", @"D:\Other"));

        var store = new TombstoneStore(P("tombstones.json"));
        store.Mark(new[] { "http://a.example/file.bin" }, @"C:\Downloads");

        Assert.Equal(0, store.PurgeSessionFile(file));
        Assert.Contains("file.bin", File.ReadAllText(file)); // 原文件保持不动
    }

    [Fact]
    public void 墓碑_重新添加后撤销不再过滤()
    {
        var dir = @"C:\Downloads";
        var file = P("aria2.session");
        File.WriteAllText(file, SessionEntry("http://a.example/again.bin", dir));

        var store = new TombstoneStore(P("tombstones.json"));
        store.Mark(new[] { "http://a.example/again.bin" }, dir);
        Assert.Equal(1, store.PurgeSessionFile(file));

        File.WriteAllText(file, SessionEntry("http://a.example/again.bin", dir));
        store.Unmark(new[] { "http://a.example/again.bin" }, dir);
        Assert.Equal(0, store.PurgeSessionFile(file));
    }

    [Fact]
    public void 墓碑_无记录时不改动会话文件()
    {
        var file = P("aria2.session");
        var content = SessionEntry("http://a.example/x.bin", @"C:\Downloads");
        File.WriteAllText(file, content);

        var store = new TombstoneStore(P("tombstones.json"));
        Assert.Equal(0, store.PurgeSessionFile(file));
        Assert.Equal(content, File.ReadAllText(file));
    }

    [Fact]
    public void 墓碑_条目数不超过上限且保留最近()
    {
        var store = new TombstoneStore(P("tombstones.json"));
        for (var i = 0; i < 1005; i++)
            store.Mark(new[] { $"http://a.example/f{i}.bin" }, @"C:\Downloads");

        using var doc = JsonDocument.Parse(File.ReadAllText(P("tombstones.json")));
        Assert.True(doc.RootElement.GetArrayLength() <= 1000,
            $"墓碑条目应被裁剪到 1000 以内，实际 {doc.RootElement.GetArrayLength()}");

        // 最近标记的条目必须还在（否则刚删的任务会立刻复活）
        var hashes = doc.RootElement.EnumerateArray()
            .Select(e => e.GetProperty("Hash").GetString()).ToHashSet();
        Assert.Contains(HashOf("http://a.example/f1004.bin", @"C:\Downloads"), hashes);
    }

    [Fact]
    public void 墓碑_文件损坏时不抛异常且视为空()
    {
        File.WriteAllText(P("tombstones.json"), "{ this is not valid json");
        var store = new TombstoneStore(P("tombstones.json"));

        var file = P("aria2.session");
        File.WriteAllText(file, SessionEntry("http://a.example/x.bin", @"C:\Downloads"));
        Assert.Equal(0, store.PurgeSessionFile(file));
    }

    [Fact]
    public void 墓碑_写盘为原子替换不留临时文件()
    {
        var store = new TombstoneStore(P("tombstones.json"));
        store.Mark(new[] { "http://a.example/x.bin" }, @"C:\Downloads");
        Assert.True(File.Exists(P("tombstones.json")));
        Assert.False(File.Exists(P("tombstones.json.tmp")));
    }

    private static string HashOf(string url, string? dir)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{url.Trim()}|{dir ?? string.Empty}")))
            .ToLowerInvariant();

    // ================= 任务元数据 =================

    [Fact]
    public void 任务元数据_防抖保存最终落盘()
    {
        var store = new TaskStore(P("tasks.json"));
        store.AddMeta("gid-1", new TaskMeta { Gid = "gid-1", TaskNumber = 20260101120000001, Name = "a.bin" });

        var deadline = DateTime.Now.AddSeconds(5);
        while (DateTime.Now < deadline && !File.Exists(P("tasks.json"))) Thread.Sleep(100);

        Assert.True(File.Exists(P("tasks.json")), "防抖计时器到点后必须把元数据落盘");
        Assert.Contains("gid-1", File.ReadAllText(P("tasks.json")));
    }

    [Fact]
    public void 任务元数据_重载后任务编号保留()
    {
        var store = new TaskStore(P("tasks.json"));
        store.AddMeta("gid-x", new TaskMeta { Gid = "gid-x", TaskNumber = 20260101120000042, Name = "x.bin" });
        store.SaveNow();

        var reloaded = new TaskStore(P("tasks.json"));
        var meta = reloaded.GetMeta("gid-x");
        Assert.NotNull(meta);
        Assert.Equal(20260101120000042, meta!.TaskNumber);
    }

    [Fact]
    public void 任务元数据_并发写入不丢条目且不留临时文件()
    {
        var store = new TaskStore(P("tasks.json"));
        Parallel.For(0, 200, i =>
            store.AddMeta($"gid-{i}", new TaskMeta { Gid = $"gid-{i}", Name = $"f{i}.bin" }));
        store.SaveNow();

        var reloaded = new TaskStore(P("tasks.json"));
        Assert.Equal(200, reloaded.All().Count);
        Assert.False(File.Exists(P("tasks.json.tmp")));
    }

    [Fact]
    public void 任务元数据_损坏文件时从头开始而不是崩溃()
    {
        File.WriteAllText(P("tasks.json"), "]]]not json[[[");
        var store = new TaskStore(P("tasks.json"));
        Assert.Empty(store.All());
        store.AddMeta("gid-new", new TaskMeta { Gid = "gid-new" });
        store.SaveNow();
        Assert.Single(new TaskStore(P("tasks.json")).All());
    }

    // ================= 设置 =================

    [Fact]
    public void 设置_损坏文件回退默认值()
    {
        File.WriteAllText(P("settings.json"), "{ broken");
        var store = new SettingsStore(P("settings.json"));
        Assert.Equal(16800, store.Current.ApiPort);
        Assert.Equal(8, store.Current.DefaultConnections);
    }

    /// <summary>现状记录：损坏的 settings.json 会连带重新生成 API 密钥（扩展需重新粘贴密钥才能连上）。
    /// 这不是崩溃，但属于"静默失效"类风险，故用测试把后果固定下来；若日后引入密钥备份恢复，此用例会失败并提示更新文档。</summary>
    [Fact]
    public void 设置_损坏文件后密钥被重新生成()
    {
        var path = P("settings.json");
        var first = new SettingsStore(path);
        var secretBefore = first.Current.ApiSecret;
        first.Save();

        File.WriteAllText(path, "{ broken");
        var second = new SettingsStore(path);

        Assert.NotEqual(secretBefore, second.Current.ApiSecret);
        Assert.False(string.IsNullOrWhiteSpace(second.Current.ApiSecret));
    }

    [Fact]
    public void 设置_保存为原子替换不留临时文件()
    {
        var store = new SettingsStore(P("settings.json"));
        store.Save();
        Assert.True(File.Exists(P("settings.json")));
        Assert.False(File.Exists(P("settings.json.tmp")));
    }

    [Fact]
    public void 设置_未变更时不写盘不触发事件()
    {
        var store = new SettingsStore(P("settings.json"));
        store.Save();
        var before = File.GetLastWriteTimeUtc(P("settings.json"));
        var fired = 0;
        store.Changed += (_, _) => fired++;

        Thread.Sleep(50);
        store.Update(s => false);

        Assert.Equal(0, fired);
        Assert.Equal(before, File.GetLastWriteTimeUtc(P("settings.json")));
    }

    // ================= 已通知记录 =================

    [Fact]
    public void 已通知记录_去重并限制上限()
    {
        var store = new NotifiedStore(P("notified.json"));
        for (var i = 0; i < 1005; i++) store.Mark($"gid-{i}");
        store.Mark("gid-1004"); // 重复标记不应增长

        using var doc = JsonDocument.Parse(File.ReadAllText(P("notified.json")));
        var list = doc.RootElement.EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.True(list.Count <= 1000, $"已通知记录应限制在 1000 条内，实际 {list.Count}");
        Assert.Contains("gid-1004", list);
        Assert.True(store.Contains("gid-1004"));
        Assert.False(store.Contains("gid-0")); // 最旧的已被裁剪
    }

    [Fact]
    public void 已通知记录_损坏文件时从头积累()
    {
        File.WriteAllText(P("notified.json"), "not json");
        var store = new NotifiedStore(P("notified.json"));
        Assert.False(store.Contains("anything"));
        store.Mark("gid-a");
        Assert.True(store.Contains("gid-a"));
    }
}

/// <summary>
/// 墓碑与实际 aria2 行为的契约测试：RemoveAsync 记下的哈希，必须能被"按会话文件过滤"这一步命中。
/// 若两边对目录字符串的处理不一致（如 aria2 上报正斜杠、应用写入反斜杠），
/// 墓碑会静默失效 —— 用户删除的任务在下次启动时又会复活。
/// </summary>
public class TombstoneEngineContractTests : IAsyncLifetime
{
    private TestEnv.FileServer _server = null!;
    private DownloadEngine _engine = null!;
    private string _workDir = null!;
    private string _downloadDir = null!;

    public async Task InitializeAsync()
    {
        _server = new TestEnv.FileServer();
        _workDir = TestEnv.NewWorkDir();
        _downloadDir = Path.Combine(_workDir, "downloads");
        Directory.CreateDirectory(_downloadDir);

        var config = new EngineConfig
        {
            Aria2Path = TestEnv.Aria2Path,
            WorkDir = Path.Combine(_workDir, "engine"),
            DefaultDownloadDir = _downloadDir,
            RpcPort = TestEnv.GetFreePort(),
            RpcSecret = "tombstone-secret",
            BtEnabled = false,
            PollIntervalMs = 250
        };
        _engine = new DownloadEngine(config, new TaskStore(Path.Combine(_workDir, "tasks.json")));
        await _engine.StartAsync();
    }

    public async Task DisposeAsync()
    {
        // InitializeAsync 中途失败时字段可能为 null：逐项判空清理，避免 NRE 中断清理导致 aria2 泄漏。
        if (_engine != null) { try { await _engine.DisposeAsync(); } catch { } }
        try { _server?.Dispose(); } catch { }
        try { if (_workDir != null) Directory.Delete(_workDir, true); } catch { }
    }

    [Fact]
    public async Task 删除任务后墓碑哈希与会话文件过滤口径一致()
    {
        var content = new byte[8 * 1024 * 1024];
        _server.AddFile("tomb.bin", content, chunkBytes: 128 * 1024, chunkDelayMs: 40);
        var url = _server.Url("tomb.bin");

        var task = await _engine.AddTaskAsync(new NewTaskRequest
        {
            Urls = new List<string> { url },
            Connections = 1
        });
        await Task.Delay(1500); // 让它真正开始下载（会话文件里才会有这条未完成任务）

        var info = await _engine.GetTaskAsync(task.Gid);
        Assert.NotNull(info);
        var reportedDir = info!.Dir;

        await _engine.RemoveAsync(task.Gid);

        // RemoveAsync 记录的哈希：以 aria2 上报的目录为准
        var hash = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes($"{url}|{reportedDir ?? string.Empty}"))).ToLowerInvariant();
        var tombFile = Path.Combine(_workDir, "engine", "tombstones.json");
        Assert.True(File.Exists(tombFile), "删除任务后应写入墓碑文件");
        Assert.Contains(hash, File.ReadAllText(tombFile));

        // 用同一目录构造"崩溃后残留的会话文件"，墓碑必须能把它过滤掉（否则任务会复活）
        var sessionFile = Path.Combine(_workDir, "engine", "aria2.session");
        File.WriteAllText(sessionFile,
            $"{url}\n  dir={reportedDir}\n  out=tomb.bin\n" +
            $"http://127.0.0.1:1/keep.bin\n  dir={reportedDir}\n  out=keep.bin\n");

        var store = new TombstoneStore(tombFile);
        var purged = store.PurgeSessionFile(sessionFile);

        Assert.Equal(1, purged);
        var remaining = File.ReadAllText(sessionFile);
        Assert.DoesNotContain("tomb.bin", remaining);
        Assert.Contains("keep.bin", remaining);
    }
}
