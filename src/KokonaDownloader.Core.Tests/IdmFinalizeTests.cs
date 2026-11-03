using System.Diagnostics;
using KokonaDownloader.Core.Engine;
using KokonaDownloader.Core.Engine.Idm;

namespace KokonaDownloader.Core.Tests;

/// <summary>
/// 收尾改名（断点 → 目标文件）。
///
/// 真实站点/性能基准里出现过"进度 100% 但还要等两三秒"：定位到改名这一步。
/// 原来的实现把**任何** IOException 都当成"跨卷"，退回"复制 + 删除"——于是"刚写完的
/// 文件被杀毒软件短暂独占"会把 128MB 的改名变成整份复制（实测 3.3 秒）。
/// </summary>
public class IdmFinalizeTests
{
    [Fact]
    public void SameVolume_IgnoresLongPathPrefix()
    {
        // LongSafe 只给超过 240 字符的路径加 \\?\，于是同一块盘的两个路径可能一个带前缀一个不带。
        // 不先去前缀就会判成"跨卷"，进而走整份复制。
        Assert.True(IdmHttpDownloader.SameVolume(@"\\?\C:\a\b", @"C:\c"));
        Assert.True(IdmHttpDownloader.SameVolume(@"c:\a", @"C:\b"));
        if (Directory.Exists(@"D:\")) Assert.False(IdmHttpDownloader.SameVolume(@"C:\a", @"D:\b"));
    }

    [Fact]
    public async Task MoveFile_SameVolume_WaitsForTransientLock_InsteadOfCopying()
    {
        string dir = TestEnv.NewWorkDir();
        string src = Path.Combine(dir, "locked.kdpart");
        string dst = Path.Combine(dir, "locked.bin");

        var payload = new byte[2 * 1024 * 1024];
        Random.Shared.NextBytes(payload);
        await File.WriteAllBytesAsync(src, payload);

        // 独占打开源文件 ≈ 杀毒扫描/预览占用：此时改名会失败。
        // 旧实现会立刻转成"复制+删除"，而复制同样被拒 → 任务在 100% 处失败；
        // 新实现应等占用释放后完成改名（毫秒级，不是复制整份）。
        var hold = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.None);
        var release = Task.Run(async () =>
        {
            await Task.Delay(150);
            hold.Dispose();
        });

        var sw = Stopwatch.StartNew();
        try
        {
            IdmHttpDownloader.MoveFile(src, dst, overwrite: false);
        }
        finally
        {
            await release;
            hold.Dispose();   // 幂等；release 任务已经放过一次
        }
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds >= 100, $"没有等占用释放（{sw.ElapsedMilliseconds}ms），说明走的不是重试路径");
        Assert.True(sw.ElapsedMilliseconds < 1500, $"等太久（{sw.ElapsedMilliseconds}ms），可能退化成复制了");
        Assert.True(File.Exists(dst), "目标文件没有生成");
        Assert.False(File.Exists(src), "源断点文件没有删掉");
        Assert.Equal(payload.Length, new FileInfo(dst).Length);
        Assert.Equal(payload, await File.ReadAllBytesAsync(dst));
    }

    [Fact]
    public void MoveFile_DoesNotOverwrite_WhenNameTaken()
    {
        string dir = TestEnv.NewWorkDir();
        string src = Path.Combine(dir, "dup.kdpart");
        string dst = Path.Combine(dir, "dup.bin");
        File.WriteAllText(src, "new");
        File.WriteAllText(dst, "old");

        // "目标已存在"必须原样抛给上层换名（变成 name (1).bin），不能在这里覆盖
        Assert.ThrowsAny<IOException>(() => IdmHttpDownloader.MoveFile(src, dst, overwrite: false));
        Assert.Equal("old", File.ReadAllText(dst));
        Assert.True(File.Exists(src));
    }
}
