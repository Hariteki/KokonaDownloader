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

    [Fact]
    public async Task 先不限速再设限速_应当立刻生效()
    {
        // 真机复现过的 bug：先设限速、再开始下载时，全局限速对 IDM 任务完全不起作用
        // （6 MB/s 跑出 25 MB/s）。根因是限速器把"从构造到现在"的时长算成了额度：
        // 不限速跑了 2 秒后再设 50KB/s，就等于白送 100KB 信用，40KB 根本不用等。
        var limiter = new AsyncRateLimiter(0);
        await Task.Delay(2000);          // 模拟"先不限速跑一会儿"
        limiter.UpdateRate(50 * 1024);   // 再打开限速

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 4; i++) await limiter.ThrottleAsync(10 * 1024, CancellationToken.None);
        sw.Stop();

        Assert.True(sw.Elapsed.TotalMilliseconds > 250,
            $"改速前的时长被算成了额度，40KB 只用了 {sw.Elapsed.TotalMilliseconds:F0}ms");
    }
}
