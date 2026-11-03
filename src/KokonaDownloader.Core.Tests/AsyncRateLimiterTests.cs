using System.Diagnostics;
using KokonaDownloader.Core.Engine.Idm;

namespace KokonaDownloader.Core.Tests;

public sealed class AsyncRateLimiterTests
{
    [Fact]
    public async Task ThrottlesRoughlyToConfiguredRate()
    {
        // 100KB/s，喂 50KB 应耗时约 0.5s（容忍 0.3~2.0s 的调度抖动）
        var limiter = new AsyncRateLimiter(100 * 1024);
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 5; i++)
            await limiter.ThrottleAsync(10 * 1024, CancellationToken.None);
        sw.Stop();

        Assert.True(sw.Elapsed.TotalSeconds > 0.30,
            $"限速未生效，50KB 只用了 {sw.Elapsed.TotalSeconds:F2}s");
        Assert.True(sw.Elapsed.TotalSeconds < 2.5,
            $"限速过度，50KB 用了 {sw.Elapsed.TotalSeconds:F2}s");
    }

    [Fact]
    public async Task DisabledLimiterIsNoOp()
    {
        var limiter = new AsyncRateLimiter(0);
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 50; i++) await limiter.ThrottleAsync(65536, CancellationToken.None);
        sw.Stop();
        Assert.True(sw.Elapsed.TotalMilliseconds < 100, "限速为 0 时不应产生等待");
    }

    [Fact]
    public async Task UpdateRateTakesEffectAtRuntime()
    {
        var limiter = new AsyncRateLimiter(0);
        limiter.UpdateRate(50 * 1024);
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 4; i++) await limiter.ThrottleAsync(10 * 1024, CancellationToken.None);
        sw.Stop();
        Assert.True(sw.Elapsed.TotalMilliseconds > 250,
            $"运行期改速未生效，40KB 只用了 {sw.Elapsed.TotalMilliseconds:F0}ms");
    }
}
