using KokonaDownloader.Core.Engine;

namespace KokonaDownloader.Core.Tests;

/// <summary>
/// 轮询诊断指纹的回归测试（L-8；第四轮审计 N-2 补护栏）。
/// 该指纹决定"这一轮要不要再写一条轮询诊断日志"：
/// - 必须稳定：同样的内容反复计算得到同一个值（否则空闲时每轮都写日志，垃圾回来了）；
/// - 必须敏感：任务增减、状态变化、停止任务数变化都要翻转（否则故障现场静默丢日志）。
/// 指纹只影响诊断日志，不参与业务判定。
/// </summary>
public class PollFingerprintTests
{
    private static DownloadTaskInfo Task(string gid, TaskState state) => new() { Gid = gid, State = state };
    private static GlobalStat Stats(int stopped) => new() { NumActive = 1, NumStopped = stopped };

    [Fact]
    public void 相同内容反复计算指纹一致()
    {
        var a = new[] { Task("gid-a", TaskState.Active), Task("gid-b", TaskState.Waiting) };
        var b = new[] { Task("gid-a", TaskState.Active), Task("gid-b", TaskState.Waiting) };

        var first = DownloadEngine.ComputePollDiagFingerprint(a, Stats(3));
        var second = DownloadEngine.ComputePollDiagFingerprint(b, Stats(3));

        Assert.Equal(first, second);
        Assert.Equal(first, DownloadEngine.ComputePollDiagFingerprint(a, Stats(3)));
    }

    [Fact]
    public void 空闲轮询指纹稳定()
    {
        var empty = Array.Empty<DownloadTaskInfo>();
        Assert.Equal(
            DownloadEngine.ComputePollDiagFingerprint(empty, Stats(0)),
            DownloadEngine.ComputePollDiagFingerprint(empty, Stats(0)));
    }

    [Fact]
    public void 任务状态变化时指纹翻转()
    {
        var before = new[] { Task("gid-a", TaskState.Active) };
        var after = new[] { Task("gid-a", TaskState.Completed) };

        Assert.NotEqual(
            DownloadEngine.ComputePollDiagFingerprint(before, Stats(0)),
            DownloadEngine.ComputePollDiagFingerprint(after, Stats(0)));
    }

    [Fact]
    public void 任务增减时指纹翻转()
    {
        var one = new[] { Task("gid-a", TaskState.Active) };
        var two = new[] { Task("gid-a", TaskState.Active), Task("gid-b", TaskState.Waiting) };

        Assert.NotEqual(
            DownloadEngine.ComputePollDiagFingerprint(one, Stats(0)),
            DownloadEngine.ComputePollDiagFingerprint(two, Stats(0)));
        Assert.NotEqual(
            DownloadEngine.ComputePollDiagFingerprint(one, Stats(0)),
            DownloadEngine.ComputePollDiagFingerprint(Array.Empty<DownloadTaskInfo>(), Stats(0)));
    }

    [Fact]
    public void 停止任务数变化时指纹翻转()
    {
        // 历史任务数（stopped）变化时 aria2 会话内容变了，但活动任务列表可能一个都没变，
        // 早先的实现会把这种情况误判为"无变化"而静默丢日志
        var tasks = new[] { Task("gid-a", TaskState.Active) };

        Assert.NotEqual(
            DownloadEngine.ComputePollDiagFingerprint(tasks, Stats(7)),
            DownloadEngine.ComputePollDiagFingerprint(tasks, Stats(8)));
    }

    [Fact]
    public void 不同任务集合大概率不同()
    {
        // 碰撞只影响一条诊断日志，但基本分布不应出现大面积同值
        var distinct = Enumerable.Range(0, 200)
            .Select(i => DownloadEngine.ComputePollDiagFingerprint(new[] { Task($"gid-{i}", TaskState.Active) }, Stats(0)))
            .Distinct()
            .Count();
        Assert.Equal(200, distinct);
    }
}
