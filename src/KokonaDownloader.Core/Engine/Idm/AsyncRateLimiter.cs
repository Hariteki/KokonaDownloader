using System.Diagnostics;

namespace KokonaDownloader.Core.Engine.Idm;

/// <summary>
/// 任务级令牌桶（多连接共享同一个速率）。
/// 做法是"领先就睡"：按已发送字节与允许字节的差额算出应等待的时长，
/// 相比固定 sleep 更贴近真实速率，也不会在多连接下被放大 N 倍。
/// </summary>
public sealed class AsyncRateLimiter
{
    private long _bytesPerSecond;   // 运行期可热更新（全局/单任务限速变更）
    private long _startTicks;       // 改速时重置（见 UpdateRate）
    private readonly object _lock = new();
    private long _sent;

    public AsyncRateLimiter(long bytesPerSecond)
    {
        _bytesPerSecond = Math.Max(0, bytesPerSecond);
        _startTicks = Stopwatch.GetTimestamp();
    }

    public bool Enabled => Interlocked.Read(ref _bytesPerSecond) > 0;

    /// <summary>
    /// 热更新速率（字节/秒，0 = 不限速）。
    ///
    /// 速率真的变了就重置记账基线。旧实现把 _startTicks 固定在构造时、只改速率，于是
    /// "先不限速跑一会儿再设限速"会算出 `allowed = 速率 × 从构造到现在的秒数` —— 那是一笔
    /// 巨额信用，实测把 6 MB/s 的全局限速变成了 25 MB/s（超 4 倍）。
    /// 代价是改速瞬间可能多放一个 chunk，可以接受。
    /// </summary>
    public void UpdateRate(long bytesPerSecond)
    {
        long next = Math.Max(0, bytesPerSecond);
        long prev = Interlocked.Exchange(ref _bytesPerSecond, next);
        if (prev == next) return;
        lock (_lock)
        {
            _sent = 0;
            _startTicks = Stopwatch.GetTimestamp();
        }
    }

    /// <summary>记账并按需等待。禁用时限速器是零开销的空操作。</summary>
    public async Task ThrottleAsync(int bytes, CancellationToken ct)
    {
        if (!Enabled || bytes <= 0) return;

        TimeSpan delay;
        lock (_lock)
        {
            _sent += bytes;
            double elapsed = (Stopwatch.GetTimestamp() - _startTicks) / (double)Stopwatch.Frequency;
            double allowed = Interlocked.Read(ref _bytesPerSecond) * elapsed;
            double excess = _sent - allowed;
            if (excess <= 0) return;
            long rate = Interlocked.Read(ref _bytesPerSecond);
            if (rate <= 0) return;
            delay = TimeSpan.FromSeconds(excess / rate);
        }

        // 单次等待封顶，避免刚开始时一次性睡很久导致连接提前超时
        if (delay > TimeSpan.FromMilliseconds(500)) delay = TimeSpan.FromMilliseconds(500);
        if (delay > TimeSpan.Zero)
            await Task.Delay(delay, ct).ConfigureAwait(false);
    }
}
