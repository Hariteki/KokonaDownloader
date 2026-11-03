using KokonaDownloader.Core.Engine;
using KokonaDownloader.Core.Engine.Idm;

namespace KokonaDownloader.Core.Tests;

/// <summary>
/// 请求保真验收：换核心后必须仍能把浏览器扩展传来的请求头原样发出去，
/// 并且像 aria2 一样按 Content-Disposition 决定落盘名。
/// 这两条不做，需要登录/防盗链的站点会直接 403 或存成 "download"。
/// </summary>
public sealed class IdmRequestFidelityTests
{
    private static byte[] Payload(int size = 256 * 1024)
    {
        var buf = new byte[size];
        new Random(20261101).NextBytes(buf);
        return buf;
    }

    [Fact]
    public async Task CustomHeaders_AreSentOnEveryRequest_IncludingRange()
    {
        string workDir = TestEnv.NewWorkDir();
        string dl = Path.Combine(workDir, "dl");
        string parts = Path.Combine(workDir, "parts");
        Directory.CreateDirectory(dl);

        var payload = Payload(1024 * 1024);
        using var server = new TestEnv.FileServer();
        // 慢一点，保证 250ms 的切分 tick 一定发生（否则只有一条裸 GET，断言 Range 会偶发失败）
        server.AddFile("h.bin", payload, null, 16 * 1024, 30);

        try
        {
            await using var downloader = new IdmHttpDownloader();
            var opts = new IdmDownloadOptions
            {
                Url = server.Url("h.bin"),
                Directory = dl,
                FileName = "h.bin",
                PartsDirectory = parts,
                PartKey = "idm-h1",
                MaxConnections = 3,
                Headers = new List<string>
                {
                    "Cookie: session=abc123",
                    "Authorization: Bearer token-xyz",
                    "User-Agent: MyAgent/1.0",
                    "Accept-Encoding: gzip",   // 必须被忽略：压缩会破坏按偏移写盘
                },
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var result = await downloader.DownloadAsync(opts, null, cts.Token);
            Assert.Equal(IdmTaskState.Completed, result.State);
            Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(dl, "h.bin")));

            var seen = server.RequestHeaders.ToList();
            Assert.NotEmpty(seen);

            foreach (var h in seen)
            {
                Assert.Equal("session=abc123", h.GetValueOrDefault("Cookie"));
                Assert.Equal("Bearer token-xyz", h.GetValueOrDefault("Authorization"));
                Assert.Equal("MyAgent/1.0", h.GetValueOrDefault("User-Agent"));
                // 我们自己强制 identity，不能被扩展传的 gzip 顶掉
                Assert.Equal("identity", h.GetValueOrDefault("Accept-Encoding"));
            }

            // 分片仍然正常：至少有一条带 Range 的请求
            Assert.Contains(seen, h => h.ContainsKey("Range"));
        }
        finally
        {
            try { Directory.Delete(workDir, true); } catch { }
        }
    }

    [Fact]
    public async Task ContentDisposition_DecidesName_WhenUrlHasNoFileName()
    {
        string workDir = TestEnv.NewWorkDir();
        string dl = Path.Combine(workDir, "dl");
        string parts = Path.Combine(workDir, "parts");
        Directory.CreateDirectory(dl);

        var payload = Payload();
        using var server = new TestEnv.FileServer();
        // URL 只是 /download，真名在响应头里——下载站点的常见形态
        server.AddFile("download", payload, new Dictionary<string, string>
        {
            ["Content-Disposition"] = "attachment; filename*=UTF-8''%E5%AD%A3%E5%BA%A6%E6%8A%A5%E5%91%8A.pdf",
        });

        try
        {
            await using var downloader = new IdmHttpDownloader();
            var opts = new IdmDownloadOptions
            {
                Url = server.Url("download"),
                Directory = dl,
                PartsDirectory = parts,
                PartKey = "idm-cd1",
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var result = await downloader.DownloadAsync(opts, null, cts.Token);

            Assert.Equal(IdmTaskState.Completed, result.State);
            Assert.EndsWith("季度报告.pdf", result.FilePath!);
            Assert.True(File.Exists(Path.Combine(dl, "季度报告.pdf")), "落盘名没有按 Content-Disposition 取");
            Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(dl, "季度报告.pdf")));
        }
        finally
        {
            try { Directory.Delete(workDir, true); } catch { }
        }
    }

    [Fact]
    public async Task ContentDisposition_PathTraversal_IsStripped()
    {
        string workDir = TestEnv.NewWorkDir();
        string dl = Path.Combine(workDir, "dl");
        string parts = Path.Combine(workDir, "parts");
        Directory.CreateDirectory(dl);

        using var server = new TestEnv.FileServer();
        server.AddFile("evil", Payload(64 * 1024), new Dictionary<string, string>
        {
            ["Content-Disposition"] = "attachment; filename=\"../../evil.txt\"",
        });

        try
        {
            await using var downloader = new IdmHttpDownloader();
            var opts = new IdmDownloadOptions
            {
                Url = server.Url("evil"),
                Directory = dl,
                PartsDirectory = parts,
                PartKey = "idm-cd2",
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var result = await downloader.DownloadAsync(opts, null, cts.Token);

            Assert.Equal(IdmTaskState.Completed, result.State);
            // 必须落在下载目录里，文件名只剩最后一段
            Assert.Equal(Path.Combine(dl, "evil.txt"), result.FilePath);
            Assert.True(File.Exists(Path.Combine(dl, "evil.txt")));
            Assert.False(File.Exists(Path.Combine(workDir, "evil.txt")), "文件被写到下载目录之外了");
        }
        finally
        {
            try { Directory.Delete(workDir, true); } catch { }
        }
    }

    [Fact]
    public async Task Resume_KeepsResolvedName_InsteadOfFallingBackToUrl()
    {
        string workDir = TestEnv.NewWorkDir();
        string dl = Path.Combine(workDir, "dl");
        string parts = Path.Combine(workDir, "parts");
        Directory.CreateDirectory(dl);

        var payload = Payload(1024 * 1024);
        using var server = new TestEnv.FileServer();
        server.AddFile("dl", payload, new Dictionary<string, string>
        {
            ["Content-Disposition"] = "attachment; filename=\"movie.bin\"",
        }, chunkBytes: 8 * 1024, chunkDelayMs: 40);

        try
        {
            var opts = new IdmDownloadOptions
            {
                Url = server.Url("dl"),
                Directory = dl,
                PartsDirectory = parts,
                PartKey = "idm-resume-name",
                MaxConnections = 1,
            };

            // 第一轮：下一点就取消，制造断点
            await using (var d1 = new IdmHttpDownloader())
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var running = d1.DownloadAsync(opts, p => { if (p.CompletedLength > 128 * 1024) cts.Cancel(); }, cts.Token);
                try { await running; } catch (OperationCanceledException) { }
            }

            // 第二轮：续传，名字必须沿用第一轮解析出的 movie.bin
            await using var d2 = new IdmHttpDownloader();
            using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var result = await d2.DownloadAsync(opts, null, cts2.Token);

            Assert.Equal(IdmTaskState.Completed, result.State);
            Assert.Equal(Path.Combine(dl, "movie.bin"), result.FilePath);
            Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(dl, "movie.bin")));
        }
        finally
        {
            try { Directory.Delete(workDir, true); } catch { }
        }
    }

    [Fact]
    public async Task Headers_SurviveRestart_OnResume()
    {
        string workDir = TestEnv.NewWorkDir();
        string dl = Path.Combine(workDir, "dl");
        string storePath = Path.Combine(workDir, "tasks.json");
        Directory.CreateDirectory(dl);

        using var server = new TestEnv.FileServer();
        server.AddFile("auth.bin", Payload(1024 * 1024), null, 8 * 1024, 60);

        var headers = new List<string> { "Cookie: sid=keepme" };
        string gid;

        try
        {
            // ---- 第一轮：带 Cookie 下载，下到一半暂停 ----
            var store = new TaskStore(storePath);
            var engine = new DownloadEngine(IdmTestEnv.Config(workDir, dl, connections: 1), store);
            try
            {
                await engine.StartAsync();
                var task = await engine.AddTaskAsync(new NewTaskRequest
                {
                    Urls = new List<string> { server.Url("auth.bin") },
                    Directory = dl,
                    Headers = headers,
                });
                gid = task.Gid;

                await WaitAsync(engine, gid, t => t.CompletedLength > 0, 25000, "下载没有开始");
                await engine.PauseAsync(gid);
                await WaitAsync(engine, gid, t => t.State == TaskState.Paused, 20000, "暂停没有生效");
                store.SaveNow();
            }
            finally { await engine.DisposeAsync(); }

            // 元数据里必须留下 headers，否则重启后没法带 Cookie 续传
            var meta = new TaskStore(storePath).GetMeta(gid);
            Assert.NotNull(meta);
            Assert.Equal(headers, meta!.Headers);

            int before = server.RequestHeaders.Count;

            // ---- 第二轮：重启后继续 ----
            var engine2 = new DownloadEngine(IdmTestEnv.Config(workDir, dl, connections: 1),
                new TaskStore(storePath));
            try
            {
                await engine2.StartAsync();
                var restored = await engine2.GetTaskAsync(gid);
                Assert.Equal(TaskState.Paused, restored!.State);

                await engine2.ResumeAsync(gid);
                await WaitAsync(engine2, gid, t => t.State == TaskState.Completed, 90000, "续传没有完成");

                var after = server.RequestHeaders.Skip(before).ToList();
                Assert.NotEmpty(after);
                Assert.All(after, h => Assert.Equal("sid=keepme", h.GetValueOrDefault("Cookie")));
            }
            finally
            {
                await engine2.DisposeAsync();
            }
        }
        finally
        {
            try { Directory.Delete(workDir, true); } catch { }
        }
    }

    private static async Task<DownloadTaskInfo> WaitAsync(DownloadEngine engine, string gid,
        Func<DownloadTaskInfo, bool> condition, int timeoutMs, string because)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        DownloadTaskInfo? last = null;
        while (Environment.TickCount64 < deadline)
        {
            last = await engine.GetTaskAsync(gid);
            if (last is not null && condition(last)) return last;
            await Task.Delay(120);
        }
        throw new TimeoutException($"{because}；最后：{last?.State} {last?.CompletedLength}/{last?.TotalLength} {last?.ErrorMessage}");
    }
}
