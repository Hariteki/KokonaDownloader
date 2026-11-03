using System.Runtime.InteropServices;
using KokonaDownloader.Core.Engine.Idm;

namespace KokonaDownloader.Core.Tests;

/// <summary>
/// 磁盘预分配验收。原先直接 SetLength 到总长：下一部 10GB 的电影，磁盘立刻少 10GB。
/// 现在先给断点文件打稀疏标记再设长度——逻辑长度仍等于远端大小（任意偏移可写），
/// 物理占用只随实际数据增长。
/// </summary>
public sealed class IdmPreallocationTests
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetCompressedFileSizeW(string path, out uint high);

    private static long AllocatedBytes(string path)
    {
        uint low = GetCompressedFileSizeW(path, out uint high);
        return ((long)high << 32) | low;
    }

    private static byte[] Payload(int size)
    {
        var buf = new byte[size];
        new Random(99).NextBytes(buf);
        return buf;
    }

    /// <summary>探针：本机文件系统是否支持稀疏文件。</summary>
    private static bool SparseSupported()
    {
        string probe = Path.Combine(Path.GetTempPath(), "kokona-sparse-probe-" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            using var h = File.OpenHandle(probe, FileMode.Create, FileAccess.Write, FileShare.Read);
            // 与生产代码同一套判断：能标记稀疏且长度可设即视为支持
            var mi = typeof(IdmHttpDownloader).GetMethod("TryMarkSparse",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            return mi is not null && (bool)mi.Invoke(null, new object[] { h })!;
        }
        catch { return false; }
        finally { try { File.Delete(probe); } catch { } }
    }

    [Fact]
    public async Task SparsePreallocation_KeepsLogicalSizeButNotPhysicalUsage()
    {
        Assert.True(SparseSupported(), "本机文件系统不支持稀疏文件，这条验收无意义");

        string workDir = TestEnv.NewWorkDir();
        string dl = Path.Combine(workDir, "dl");
        string parts = Path.Combine(workDir, "parts");
        Directory.CreateDirectory(dl);

        const int total = 24 * 1024 * 1024;
        var payload = Payload(total);
        using var server = new TestEnv.FileServer();
        server.AddFile("big.bin", payload, null, 8 * 1024, 20);

        try
        {
            await using var downloader = new IdmHttpDownloader();
            long written = 0;
            var opts = new IdmDownloadOptions
            {
                Url = server.Url("big.bin"),
                Directory = dl,
                FileName = "big.bin",
                PartsDirectory = parts,
                PartKey = "idm-sparse",
                MaxConnections = 2,
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            var running = downloader.DownloadAsync(opts, p => Volatile.Write(ref written, p.CompletedLength), cts.Token);

            string part = Path.Combine(parts, "idm-sparse.kdpart");
            await IdmTestEnv.WaitUntilAsync(
                () => Volatile.Read(ref written) > 1024 * 1024, 30000, "下载没有推进到 1MB");

            long logical = new FileInfo(part).Length;
            long allocated = AllocatedBytes(part);
            Console.WriteLine($"[sparse] logical={logical} allocated={allocated} written={Volatile.Read(ref written)}");

            Assert.Equal(total, logical);                       // 逻辑长度立即到位
            Assert.True(allocated < logical / 2,                // 物理占用只跟已下数据走
                $"物理占用 {allocated} 接近逻辑长度 {logical}，稀疏预分配没有生效");

            cts.Cancel();
            try { await running; } catch (OperationCanceledException) { }
        }
        finally
        {
            try { Directory.Delete(workDir, true); } catch { }
        }
    }

    [Fact]
    public async Task SparseDisabled_DoesNotTouchLogicalSizeUpFront()
    {
        string workDir = TestEnv.NewWorkDir();
        string dl = Path.Combine(workDir, "dl");
        string parts = Path.Combine(workDir, "parts");
        Directory.CreateDirectory(dl);

        const int total = 8 * 1024 * 1024;
        var payload = Payload(total);
        using var server = new TestEnv.FileServer();
        server.AddFile("plain.bin", payload, null, 8 * 1024, 30);

        try
        {
            await using var downloader = new IdmHttpDownloader();
            long written = 0;
            var opts = new IdmDownloadOptions
            {
                Url = server.Url("plain.bin"),
                Directory = dl,
                FileName = "plain.bin",
                PartsDirectory = parts,
                PartKey = "idm-nosparse",
                MaxConnections = 1,
                SparsePreallocation = false,
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            var running = downloader.DownloadAsync(opts, p => Volatile.Write(ref written, p.CompletedLength), cts.Token);

            string part = Path.Combine(parts, "idm-nosparse.kdpart");
            await IdmTestEnv.WaitUntilAsync(
                () => Volatile.Read(ref written) > 256 * 1024, 30000, "下载没有推进");

            long logical = new FileInfo(part).Length;
            Assert.True(logical < total,
                $"关掉稀疏后仍然是预期长度直接预分配（logical={logical}, total={total}）");

            cts.Cancel();
            try { await running; } catch (OperationCanceledException) { }
        }
        finally
        {
            try { Directory.Delete(workDir, true); } catch { }
        }
    }
}
