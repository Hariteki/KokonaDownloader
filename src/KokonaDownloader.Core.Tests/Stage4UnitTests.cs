using KokonaDownloader.Core;
using KokonaDownloader.Core.Engine;
using KokonaDownloader.Core.Notifications;
using Xunit;

namespace KokonaDownloader.Core.Tests;

/// <summary>阶段4：托盘进度汇总与通知载荷的单元测试。</summary>
public class Stage4UnitTests
{
    private static DownloadTaskInfo Task(TaskState state, long total = 0, long done = 0, long speed = 0) => new()
    {
        Gid = Guid.NewGuid().ToString("N"),
        Name = "t",
        State = state,
        TotalLength = total,
        CompletedLength = done,
        DownloadSpeed = speed
    };

    #region TrayProgress

    [Fact]
    public void TrayProgress_EmptyList_IsIdle()
    {
        var p = TrayProgress.Compute(Array.Empty<DownloadTaskInfo>());
        Assert.False(p.IsBusy);
        Assert.Equal(0, p.Percent);
        Assert.Equal(0, p.ActiveCount);
    }

    [Fact]
    public void TrayProgress_ComputesWeightedPercent()
    {
        var p = TrayProgress.Compute(new[]
        {
            Task(TaskState.Active, total: 100, done: 50),
            Task(TaskState.Active, total: 100, done: 100),
            Task(TaskState.Waiting),
            Task(TaskState.Completed, total: 999, done: 999) // 已完成不计入
        });
        Assert.Equal(2, p.ActiveCount);
        Assert.Equal(1, p.WaitingCount);
        Assert.Equal(75, p.Percent, 3); // (50+100)/200
    }

    [Fact]
    public void TrayProgress_UnknownSizeExcludedFromPercent()
    {
        var p = TrayProgress.Compute(new[] { Task(TaskState.Active, total: 0, done: 123) });
        Assert.True(p.IsBusy);
        Assert.Equal(0, p.Percent); // 大小未知不计入分母
    }

    [Fact]
    public void TrayProgress_SumsSpeedAndClampsNegative()
    {
        var p = TrayProgress.Compute(new[]
        {
            Task(TaskState.Active, total: 10, done: 1, speed: 1000),
            Task(TaskState.Active, total: 10, done: 1, speed: -5)
        });
        Assert.Equal(1000, p.DownloadSpeed);
    }

    [Fact]
    public void TrayProgress_PercentClampedTo100()
    {
        var p = TrayProgress.Compute(new[] { Task(TaskState.Active, total: 100, done: 200) });
        Assert.Equal(100, p.Percent);
    }

    [Fact]
    public void FormatBytes_Units()
    {
        Assert.Equal("0 B", FormatUtil.FormatBytes(0));
        Assert.Equal("512 B", FormatUtil.FormatBytes(512));
        Assert.Equal("1 KB", FormatUtil.FormatBytes(1024));
        Assert.Equal("1.5 MB", FormatUtil.FormatBytes(1572864));
        Assert.Equal("2 GB", FormatUtil.FormatBytes(2L * 1024 * 1024 * 1024));
    }

    #endregion

    #region ToastPayload

    [Fact]
    public void ToastPayload_CompletedContainsButtonAndEscapedName()
    {
        var xml = ToastPayload.BuildDownloadCompleted("a<b>&\"file.zip", @"C:\DL");
        Assert.Contains("<text>下载完成</text>", xml);
        Assert.Contains("a&lt;b&gt;&amp;&quot;file.zip", xml);
        Assert.Contains("打开文件夹", xml);
        Assert.Contains("action=openFolder", xml);
    }

    [Fact]
    public void ToastPayload_CompletedWithoutDirHasNoButton()
    {
        var xml = ToastPayload.BuildDownloadCompleted("f.zip", "");
        Assert.DoesNotContain("<actions>", xml);
    }

    [Fact]
    public void ToastPayload_FailedContainsError()
    {
        var xml = ToastPayload.BuildDownloadFailed("f.zip", "连接超时");
        Assert.Contains("<text>下载失败</text>", xml);
        Assert.Contains("连接超时", xml);
        Assert.DoesNotContain("<actions>", xml);
    }

    [Fact]
    public void ToastPayload_OpenFolderArgumentRoundTrip()
    {
        var dir = @"C:\下载 & 备份\子目录";
        var file = @"C:\下载 & 备份\子目录\file name.zip";
        var arg = ToastPayload.OpenFolderArgument(dir, file);
        Assert.True(ToastPayload.TryParseOpenFolder(arg, out var parsedDir, out var parsedFile));
        Assert.Equal(dir, parsedDir);
        Assert.Equal(file, parsedFile);
    }

    [Fact]
    public void ToastPayload_ParseRejectsForeignOrEmptyArgs()
    {
        Assert.False(ToastPayload.TryParseOpenFolder(null, out _, out _));
        Assert.False(ToastPayload.TryParseOpenFolder("", out _, out _));
        Assert.False(ToastPayload.TryParseOpenFolder("action=evil&dir=C:\\", out _, out _));
        Assert.False(ToastPayload.TryParseOpenFolder("action=openFolder", out _, out _)); // 缺 dir
    }

    [Fact]
    public void ToastPayload_ParseWithoutFile()
    {
        var arg = ToastPayload.OpenFolderArgument(@"D:\x");
        Assert.True(ToastPayload.TryParseOpenFolder(arg, out var dir, out var file));
        Assert.Equal(@"D:\x", dir);
        Assert.Null(file);
    }

    #endregion

    #region NotificationRules

    [Fact]
    public void NotificationRules_FreshCompletionNotifies()
    {
        var now = DateTime.Now;
        Assert.True(NotificationRules.ShouldNotify(now.AddSeconds(-5), now));
    }

    [Fact]
    public void NotificationRules_OldCompletionSkipped()
    {
        // 重启后历史任务重新上报：完成时间久远，不应再弹通知
        var now = DateTime.Now;
        Assert.False(NotificationRules.ShouldNotify(now.AddMinutes(-10), now));
        Assert.False(NotificationRules.ShouldNotify(null, now));
    }

    [Fact]
    public void NotificationRules_CustomWindow()
    {
        var now = DateTime.Now;
        Assert.True(NotificationRules.ShouldNotify(now.AddSeconds(-50), now, TimeSpan.FromMinutes(2)));
        Assert.False(NotificationRules.ShouldNotify(now.AddSeconds(-130), now, TimeSpan.FromMinutes(2)));
    }

    [Fact]
    public void NotifiedStore_MarksAndPersists()
    {
        var path = Path.Combine(Path.GetTempPath(), "kokona_dl_test", Guid.NewGuid().ToString("N"), "notified.json");
        var store = new NotifiedStore(path);
        Assert.False(store.Contains("g1"));
        store.Mark("g1");
        Assert.True(store.Contains("g1"));
        store.Mark("g1"); // 幂等
        // 重新加载验证持久化
        var reloaded = new NotifiedStore(path);
        Assert.True(reloaded.Contains("g1"));
        Assert.False(reloaded.Contains("g2"));
    }

    #endregion
}
