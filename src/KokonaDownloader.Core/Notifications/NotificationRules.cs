using KokonaDownloader.Core;
using KokonaDownloader.Core.Engine;
using KokonaDownloader.Core.Notifications;
using KokonaDownloader.Core.Settings;
using Microsoft.Win32;

namespace KokonaDownloader.Core;

/// <summary>通知触发规则（纯函数，便于测试）。</summary>
public static class NotificationRules
{
    /// <summary>
    /// 仅当完成时间距现在不超过窗口期时才通知。
    /// 进程重启后 aria2 会重新上报历史已完成任务，其 FinishedAt 为旧值，据此过滤，避免通知回放。
    /// </summary>
    public static bool ShouldNotify(DateTime? finishedAt, DateTime now, TimeSpan? window = null)
        => finishedAt.HasValue && now - finishedAt.Value <= (window ?? TimeSpan.FromSeconds(30));
}
