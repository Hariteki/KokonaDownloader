using KokonaDownloader.Core.Engine;
using KokonaDownloader.Core.Engine.Idm;

namespace KokonaDownloader.Core.Tests;

/// <summary>
/// 防覆盖验收。aria2 路径靠 allow-overwrite=false 保证"重复下载不抹掉用户已有文件"，
/// 换成自研引擎后这条保证必须由我们自己实现，否则是数据丢失级别的回归。
/// </summary>
public sealed class IdmOverwriteTests
{
    private static byte[] Payload(int size = 256 * 1024)
    {
        var buf = new byte[size];
        new Random(20261031).NextBytes(buf);
        return buf;
    }

    private static IdmDownloadOptions MakeOptions(TestEnv.FileServer server, string dir, string partsDir,
        string fileName, string partKey, bool overwrite = false) => new()
    {
        Url = $"http://127.0.0.1:{server.Port}/o.bin",
        Directory = dir,
        FileName = fileName,
        PartsDirectory = partsDir,
        PartKey = partKey,
        OverwriteTarget = overwrite,
    };

    [Fact]
    public async Task ExistingTarget_IsKept_AndNewFileGetsNumberedName()
    {
        string workDir = TestEnv.NewWorkDir();
        string dl = Path.Combine(workDir, "dl");
        string parts = Path.Combine(workDir, "parts");
        Directory.CreateDirectory(dl);

        const string original = "用户原有的文件，绝不能被覆盖";
        string target = Path.Combine(dl, "o.bin");
        await File.WriteAllTextAsync(target, original);

        var payload = Payload();
        using var server = new TestEnv.FileServer();
        server.AddFile("o.bin", payload);

        try
        {
            await using var downloader = new IdmHttpDownloader();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var result = await downloader.DownloadAsync(
                MakeOptions(server, dl, parts, "o.bin", "idm-ow1"), null, cts.Token);

            Assert.Equal(IdmTaskState.Completed, result.State);

            // ① 原文件一个字节都没动
            Assert.Equal(original, await File.ReadAllTextAsync(target));

            // ② 新内容落在 "o (1).bin"
            string renamed = Path.Combine(dl, "o (1).bin");
            Assert.True(File.Exists(renamed), "同名冲突时应生成 o (1).bin");
            Assert.Equal(payload, await File.ReadAllBytesAsync(renamed));

            // ③ 上报的落盘路径必须是改名后的真实路径
            Assert.Equal(renamed, result.FilePath);
        }
        finally
        {
            try { Directory.Delete(workDir, true); } catch { }
        }
    }

    [Fact]
    public async Task OverwriteTarget_ReplacesExistingFile()
    {
        string workDir = TestEnv.NewWorkDir();
        string dl = Path.Combine(workDir, "dl");
        string parts = Path.Combine(workDir, "parts");
        Directory.CreateDirectory(dl);

        string target = Path.Combine(dl, "o.bin");
        await File.WriteAllTextAsync(target, "旧内容");

        var payload = Payload();
        using var server = new TestEnv.FileServer();
        server.AddFile("o.bin", payload);

        try
        {
            await using var downloader = new IdmHttpDownloader();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var result = await downloader.DownloadAsync(
                MakeOptions(server, dl, parts, "o.bin", "idm-ow2", overwrite: true), null, cts.Token);

            Assert.Equal(IdmTaskState.Completed, result.State);
            Assert.Equal(target, result.FilePath);
            Assert.Equal(payload, await File.ReadAllBytesAsync(target));
            Assert.False(File.Exists(Path.Combine(dl, "o (1).bin")), "显式覆盖时不该再生成编号副本");
        }
        finally
        {
            try { Directory.Delete(workDir, true); } catch { }
        }
    }

    [Fact]
    public async Task TwoTasksFinishingTogether_DoNotOverwriteEachOther()
    {
        // 真机复现：批量下同一文件名，两个任务都判断"目标不存在"→ 互相覆盖，最后只剩一个文件。
        // 落盘必须靠 Move 的原子占位，而不是"先 File.Exists 再 Move"。
        string workDir = TestEnv.NewWorkDir();
        string dl = Path.Combine(workDir, "dl");
        string parts = Path.Combine(workDir, "parts");
        Directory.CreateDirectory(dl);

        var payload = Payload(512 * 1024);
        using var server = new TestEnv.FileServer();
        server.AddFile("o.bin", payload);   // MakeOptions 固定请求 /o.bin

        try
        {
            var optsA = MakeOptions(server, dl, parts, "same.bin", "idm-race-a");
            var optsB = MakeOptions(server, dl, parts, "same.bin", "idm-race-b");

            var a = new IdmHttpDownloader();
            var b = new IdmHttpDownloader();
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                var ta = a.DownloadAsync(optsA, null, cts.Token);
                var tb = b.DownloadAsync(optsB, null, cts.Token);
                var results = await Task.WhenAll(ta, tb);

                Assert.All(results, r => Assert.Equal(IdmTaskState.Completed, r.State));
                Assert.NotEqual(results[0].FilePath, results[1].FilePath);
                foreach (var r in results)
                {
                    Assert.True(File.Exists(r.FilePath), $"任务上报的路径不存在: {r.FilePath}");
                    Assert.Equal(payload, await File.ReadAllBytesAsync(r.FilePath!));
                }
            }
            finally
            {
                await a.DisposeAsync();
                await b.DisposeAsync();
            }
        }
        finally
        {
            try { Directory.Delete(workDir, true); } catch { }
        }
    }

    [Fact]
    public async Task Engine_RenamesBeforeStart_SoListShowsRealFileName()
    {
        var engine = IdmTestEnv.CreateEngine(out _, out var workDir, out var dl);
        using var server = new TestEnv.FileServer();
        server.AddFile("e.bin", Payload());

        try
        {
            await File.WriteAllTextAsync(Path.Combine(dl, "e.bin"), "已有文件");
            await engine.StartAsync();

            var task = await engine.AddTaskAsync(new NewTaskRequest
            {
                Urls = new List<string> { $"http://127.0.0.1:{server.Port}/e.bin" },
                Directory = dl,
                FileName = "e.bin",
            });

            // 列表从一开始就该显示真实落盘名，而不是完成时才悄悄改名
            Assert.Equal("e (1).bin", task.Name);
            Assert.Equal("已有文件", await File.ReadAllTextAsync(Path.Combine(dl, "e.bin")));
        }
        finally
        {
            await engine.DisposeAsync();
            try { Directory.Delete(workDir, true); } catch { }
        }
    }
}
