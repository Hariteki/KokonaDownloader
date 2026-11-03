using KokonaDownloader.Core.Engine.Idm;

namespace KokonaDownloader.Core.Tests;

/// <summary>
/// 续传前校验远端是否变化。
/// 危险场景：文件在服务器上被替换成"同长度、不同内容"的版本，继续拿旧断点拼下去，
/// 得到的文件大小完全正确、也不会报任何错，但内容是两代混杂的 —— 静默损坏。
/// </summary>
public sealed class IdmRemoteChangeTests
{
    private static byte[] Payload(int size, int seed)
    {
        var buf = new byte[size];
        new Random(seed).NextBytes(buf);
        return buf;
    }

    private static IdmDownloadOptions Options(TestEnv.FileServer server, string dir, string parts, string key) => new()
    {
        Url = server.Url("v.bin"),
        Directory = dir,
        FileName = "v.bin",
        PartsDirectory = parts,
        PartKey = key,
        MaxConnections = 1,
    };

    /// <summary>下一小段就取消，制造断点。</summary>
    private static async Task PartialDownloadAsync(IdmDownloadOptions opts, long stopAt)
    {
        await using var d = new IdmHttpDownloader();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var running = d.DownloadAsync(opts, p => { if (p.CompletedLength > stopAt) cts.Cancel(); }, cts.Token);
        try { await running; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task SameLengthDifferentEtag_RestartsInsteadOfSplicing()
    {
        string workDir = TestEnv.NewWorkDir();
        string dl = Path.Combine(workDir, "dl");
        string parts = Path.Combine(workDir, "parts");
        Directory.CreateDirectory(dl);

        const int size = 512 * 1024;
        var oldContent = Payload(size, 1);
        var newContent = Payload(size, 2);   // 长度相同、内容不同

        using var server = new TestEnv.FileServer();
        server.AddFile("v.bin", oldContent, new Dictionary<string, string> { ["ETag"] = "\"v1\"" },
            8 * 1024, 40);

        try
        {
            var opts = Options(server, dl, parts, "idm-remote1");
            await PartialDownloadAsync(opts, 128 * 1024);

            // 服务器换了一份内容（长度不变，只有 ETag 变了）
            server.AddFile("v.bin", newContent, new Dictionary<string, string> { ["ETag"] = "\"v2\"" },
                8 * 1024, 40);

            await using var d = new IdmHttpDownloader();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            var result = await d.DownloadAsync(opts, null, cts.Token);

            Assert.Equal(IdmTaskState.Completed, result.State);
            Assert.Equal(newContent, await File.ReadAllBytesAsync(Path.Combine(dl, "v.bin")));
        }
        finally
        {
            try { Directory.Delete(workDir, true); } catch { }
        }
    }

    [Fact]
    public async Task ChangedLength_IsDetectedEvenWithoutEtag()
    {
        string workDir = TestEnv.NewWorkDir();
        string dl = Path.Combine(workDir, "dl");
        string parts = Path.Combine(workDir, "parts");
        Directory.CreateDirectory(dl);

        var oldContent = Payload(512 * 1024, 3);
        var newContent = Payload(768 * 1024, 4);   // 长度变了，且服务器不给任何校验器

        using var server = new TestEnv.FileServer();
        server.AddFile("v.bin", oldContent, null, 8 * 1024, 40);

        try
        {
            var opts = Options(server, dl, parts, "idm-remote2");
            await PartialDownloadAsync(opts, 128 * 1024);

            server.AddFile("v.bin", newContent, null, 8 * 1024, 40);

            await using var d = new IdmHttpDownloader();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            var result = await d.DownloadAsync(opts, null, cts.Token);

            Assert.Equal(IdmTaskState.Completed, result.State);
            var actual = await File.ReadAllBytesAsync(Path.Combine(dl, "v.bin"));
            Assert.Equal(newContent.Length, actual.Length);
            Assert.Equal(newContent, actual);
        }
        finally
        {
            try { Directory.Delete(workDir, true); } catch { }
        }
    }

    [Fact]
    public async Task UnchangedRemote_StillResumesFromBreakpoint()
    {
        string workDir = TestEnv.NewWorkDir();
        string dl = Path.Combine(workDir, "dl");
        string parts = Path.Combine(workDir, "parts");
        Directory.CreateDirectory(dl);

        var content = Payload(1024 * 1024, 5);
        using var server = new TestEnv.FileServer();
        server.AddFile("v.bin", content, new Dictionary<string, string> { ["ETag"] = "\"same\"" },
            8 * 1024, 40);

        try
        {
            var opts = Options(server, dl, parts, "idm-remote3");
            await PartialDownloadAsync(opts, 256 * 1024);
            int requestsBefore = server.RequestCountFor("v.bin");

            await using var d = new IdmHttpDownloader();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            var result = await d.DownloadAsync(opts, null, cts.Token);

            Assert.Equal(IdmTaskState.Completed, result.State);
            Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(dl, "v.bin")));

            // 校验只多发一次 Range 0-0 探测，不能把已下内容作废重来
            Assert.True(server.RequestCountFor("v.bin") - requestsBefore <= 4,
                $"续传发了 {server.RequestCountFor("v.bin") - requestsBefore} 次请求，像是在整份重下");
        }
        finally
        {
            try { Directory.Delete(workDir, true); } catch { }
        }
    }
}
