using KokonaDownloader.Core.Engine;

namespace KokonaDownloader.Core;

/// <summary>
/// 托盘整体进度汇总：把任务列表聚合成"总体百分比 / 活动数 / 等待数 / 总速度"。
/// 纯函数，便于单元测试；托盘图标与悬浮提示直接使用。
/// 总体百分比只统计"大小已知"的活动任务，未知大小的任务不计入分母。
/// </summary>
public sealed record TrayProgress(double Percent, int ActiveCount, int WaitingCount, long DownloadSpeed)
{
    public bool IsBusy => ActiveCount > 0;

    public static TrayProgress Compute(IEnumerable<DownloadTaskInfo> tasks)
    {
        long total = 0, done = 0, speed = 0;
        int active = 0, waiting = 0;
        foreach (var t in tasks)
        {
            switch (t.State)
            {
                case TaskState.Active:
                    active++;
                    speed += Math.Max(0, t.DownloadSpeed);
                    if (t.TotalLength > 0)
                    {
                        total += t.TotalLength;
                        done += Math.Min(t.CompletedLength, t.TotalLength);
                    }
                    break;
                case TaskState.Waiting:
                    waiting++;
                    break;
            }
        }
        var percent = total > 0 ? Math.Clamp(done * 100.0 / total, 0, 100) : 0;
        return new TrayProgress(percent, active, waiting, speed);
    }
}

/// <summary>字节数格式化（核心层共享，UI 与托盘提示复用）。</summary>
public static class FormatUtil
{
    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        var i = 0;
        double v = bytes;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return $"{v:0.##} {units[i]}";
    }
}
