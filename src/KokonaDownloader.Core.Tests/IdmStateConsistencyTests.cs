using System.Diagnostics;
using KokonaDownloader.Core.Engine;
using KokonaDownloader.Core.Engine.Idm;

namespace KokonaDownloader.Core.Tests;

/// <summary>
/// 任务状态与进度必须自洽。
///
/// 背景（真实站点实测撞到）：界面/接口读的是 <see cref="DownloadTaskInfo"/> 那一份，
/// 而进度原先只写进内部 Entry，靠下一轮轮询（250–400ms）才同步过去。收尾时
/// <c>Info.State</c> 立刻变成 Completed，<c>Info.CompletedLength</c> 却还停在上一次同步的数字：
///
/// - 10MB 文件：接口报「已完成 98.31%」，差 177KB；
/// - 比第一轮轮询还快的小文件：接口报「已完成 0 B / 0 B」。
///
/// 这类"看着像坏了"的状态比真坏更麻烦：用户会去点重下、去翻日志。
/// </summary>
public class IdmStateConsistencyTests
{
    private static byte[] Payload(int size)
    {
        var data = new byte[size];
        for (int i = 0; i < size; i++) data[i] = (byte)(i * 31 + 7);
        return data;
    }

    [Fact]
    public async Task CompletingTask_ReportsFullLength_AtTheMomentStateFlips()
    {
        string workDir = TestEnv.NewWorkDir();
        string dl = Path.Combine(workDir, "dl");
        string parts = Path.Combine(workDir, "parts");
        Directory.CreateDirectory(dl);

        var payload = Payload(3 * 1024 * 1024);
        using var server = new TestEnv.FileServer();
        // 快：3MB / 4 连接，通常一轮轮询（250ms）内就结束——正是"状态先变、进度没跟上"的窗口
        server.AddFile("quick.bin", payload);

        await using var manager = new IdmTaskManager(dl, 4, null, 3, parts);
        // 注意：这里**不能**用 manager.Get(gid)——它会把 Entry 上的数字同步进 Info，
        // 顺手把要验的 bug 盖掉。接口/界面拿到的就是 Add 返回的这个对象。
        var info = manager.Add("idm-1", 1, new NewTaskRequest
        {
            Urls = new List<string> { server.Url("quick.bin") },
            Directory = dl,
            Connections = 4,
        });

        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(30) && info.State is TaskState.Waiting or TaskState.Active)
            await Task.Delay(5);

        Assert.Equal(TaskState.Completed, info.State);
        // 状态变成"已完成"的那一刻，长度就必须已经是完整的
        Assert.Equal(payload.Length, info.TotalLength);
        Assert.Equal(payload.Length, info.CompletedLength);
        // 进度是长度算出来的：长度对了它才是 100%，否则界面会显示"已完成 98.31%"
        Assert.Equal(1.0, info.Progress, 3);

        string? path = info.FilePath;
        Assert.NotNull(path);
        Assert.Equal(payload.Length, new FileInfo(path!).Length);
    }
}
