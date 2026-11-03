using KokonaDownloader.Core.Engine;
using KokonaDownloader.Core.Engine.Idm;

namespace KokonaDownloader.Core.Tests;

/// <summary>
/// 多地址（镜像）验收：一个任务给多个 URL 时不能"只用第一个"。
/// 主地址 404 或半路挂掉都要能切到备用地址，且落盘内容正确。
/// </summary>
public sealed class IdmMirrorTests
{
    private static byte[] Payload(int size = 512 * 1024)
    {
        var buf = new byte[size];
        new Random(20261102).NextBytes(buf);
        return buf;
    }

    [Fact]
    public async Task PrimaryFails_ProbeFallsBackToMirror()
    {
        string workDir = TestEnv.NewWorkDir();
        string dl = Path.Combine(workDir, "dl");
        string parts = Path.Combine(workDir, "parts");
        Directory.CreateDirectory(dl);

        var payload = Payload();
        using var server = new TestEnv.FileServer();
        server.AddFile("real.bin", payload);

        try
        {
            await using var downloader = new IdmHttpDownloader();
            var opts = new IdmDownloadOptions
            {
                Url = server.Url("missing.bin"),          // 主地址 404
                Mirrors = new[] { server.Url("real.bin") },
                Directory = dl,
                FileName = "real.bin",
                PartsDirectory = parts,
                PartKey = "idm-mir1",
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var result = await downloader.DownloadAsync(opts, null, cts.Token);

            Assert.Equal(IdmTaskState.Completed, result.State);
            Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(dl, "real.bin")));
            Assert.True(server.RequestCountFor("missing.bin") >= 1, "主地址根本没被尝试");
        }
        finally
        {
            try { Directory.Delete(workDir, true); } catch { }
        }
    }

    [Fact]
    public async Task PrimaryIgnoresRange_RangeWorkerSwitchesToMirror()
    {
        string workDir = TestEnv.NewWorkDir();
        string dl = Path.Combine(workDir, "dl");
        string parts = Path.Combine(workDir, "parts");
        Directory.CreateDirectory(dl);

        var payload = Payload(1024 * 1024);
        using var server = new TestEnv.FileServer();
        // 主地址：收到 Range 也回整份 200（等于不支持分段）
        server.AddFile("primary.bin", payload, null, 16 * 1024, 20, ignoreRange: true);
        // 镜像：正常支持 Range
        server.AddFile("mirror.bin", payload, null, 16 * 1024, 20);

        try
        {
            await using var downloader = new IdmHttpDownloader();
            var opts = new IdmDownloadOptions
            {
                Url = server.Url("primary.bin"),
                Mirrors = new[] { server.Url("mirror.bin") },
                Directory = dl,
                FileName = "p.bin",
                PartsDirectory = parts,
                PartKey = "idm-mir2",
                MaxConnections = 4,
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var result = await downloader.DownloadAsync(opts, null, cts.Token);

            Assert.Equal(IdmTaskState.Completed, result.State);
            var actual = await File.ReadAllBytesAsync(Path.Combine(dl, "p.bin"));
            Assert.Equal(payload, actual);

            // 镜像必须被真正用过（不是只探测了一下）
            Assert.True(server.RequestCountFor("mirror.bin") >= 2,
                $"镜像只被请求 {server.RequestCountFor("mirror.bin")} 次，分段没有切过去");
        }
        finally
        {
            try { Directory.Delete(workDir, true); } catch { }
        }
    }

    [Fact]
    public async Task Engine_PassesAllUrlsAsMirrors()
    {
        var engine = IdmTestEnv.CreateEngine(out _, out var workDir, out var dl);
        using var server = new TestEnv.FileServer();
        var payload = Payload(256 * 1024);
        server.AddFile("m.bin", payload);

        try
        {
            await engine.StartAsync();
            var task = await engine.AddTaskAsync(new NewTaskRequest
            {
                Urls = new List<string> { server.Url("nope.bin"), server.Url("m.bin") },
                Directory = dl,
            });

            var deadline = Environment.TickCount64 + 30000;
            DownloadTaskInfo? done = null;
            while (Environment.TickCount64 < deadline)
            {
                var t = await engine.GetTaskAsync(task.Gid);
                if (t?.State == TaskState.Completed) { done = t; break; }
                if (t?.State == TaskState.Failed) Assert.Fail($"任务失败：{t.ErrorMessage}");
                await Task.Delay(150);
            }
            Assert.NotNull(done);
            Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(dl, "m.bin")));
        }
        finally
        {
            await engine.DisposeAsync();
            try { Directory.Delete(workDir, true); } catch { }
        }
    }
}
