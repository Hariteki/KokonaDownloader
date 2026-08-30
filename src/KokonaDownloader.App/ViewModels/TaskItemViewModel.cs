using System.ComponentModel;
using System.Runtime.CompilerServices;
using KokonaDownloader.Core.Engine;
using Microsoft.UI.Xaml.Media;

namespace KokonaDownloader.App.ViewModels;

/// <summary>任务列表项视图模型：把引擎快照转成可绑定属性。</summary>
public sealed class TaskItemViewModel : INotifyPropertyChanged
{
    private string _name = string.Empty;
    private TaskState _state;
    private double _progress;
    private string _speedText = string.Empty;
    private string _etaText = string.Empty;
    private string _sizeText = string.Empty;
    private string _statusText = string.Empty;

    public string Gid { get; private set; } = string.Empty;
    public DownloadTaskInfo Model { get; private set; } = new();

    public string Name { get => _name; private set => Set(ref _name, value); }
    public TaskState State { get => _state; private set { if (Set(ref _state, value)) { Raise(nameof(IsActive)); Raise(nameof(CanPause)); Raise(nameof(CanResume)); Raise(nameof(PauseVisible)); Raise(nameof(ResumeVisible)); Raise(nameof(StatusColor)); Raise(nameof(StatusDotBrush)); Raise(nameof(StatusGlowBrush)); } } }
    public double Progress { get => _progress; private set => Set(ref _progress, value); }
    public string SpeedText { get => _speedText; private set => Set(ref _speedText, value); }
    public string EtaText { get => _etaText; private set => Set(ref _etaText, value); }
    public string SizeText { get => _sizeText; private set => Set(ref _sizeText, value); }
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }

    public bool IsActive => State == TaskState.Active;
    public bool CanPause => State is TaskState.Active or TaskState.Waiting;
    public bool CanResume => State == TaskState.Paused;

    // 乐观操作：点击暂停/继续的瞬间覆盖按钮可见性（图标立即切换）并显示过渡小字，
    // 引擎真实状态确认后由 Update() 清除；RPC 异常/超时兜底清除，避免乐观状态卡死。
    private string? _pendingAction;
    private DateTime _pendingAt;
    private const double PendingTimeoutSeconds = 8;

    /// <summary>乐观操作：null=无；"pause"=已点暂停待引擎确认；"resume"=已点继续待引擎确认。</summary>
    public string? PendingAction
    {
        get => _pendingAction;
        private set
        {
            if (Set(ref _pendingAction, value))
            {
                Raise(nameof(PauseVisible));
                Raise(nameof(ResumeVisible));
                Raise(nameof(PendingText));
                Raise(nameof(PendingOpacity));
            }
        }
    }

    /// <summary>暂停按钮显示：真实可暂停（未被乐观暂停覆盖），或已乐观继续（预期即将可暂停）。</summary>
    public bool PauseVisible => (CanPause && PendingAction != "pause") || PendingAction == "resume";

    /// <summary>继续按钮显示：真实可继续（未被乐观继续覆盖），或已乐观暂停（预期即将暂停）。</summary>
    public bool ResumeVisible => (CanResume && PendingAction != "resume") || PendingAction == "pause";

    /// <summary>过渡小字文本："暂停中"/"恢复下载中"，无乐观操作时为空。</summary>
    public string PendingText => PendingAction switch
    {
        "pause" => "暂停中",
        "resume" => "恢复下载中",
        _ => ""
    };

    /// <summary>过渡小字透明度：无乐观操作时 0（常驻占位避免行高跳动），否则 1。</summary>
    public double PendingOpacity => PendingAction == null ? 0 : 1;

    /// <summary>标记乐观操作：点击暂停/继续后立即调用，让 UI 先行反馈。</summary>
    public void MarkPending(string action)
    {
        _pendingAt = DateTime.UtcNow;
        PendingAction = action;
    }

    /// <summary>清除乐观操作（RPC 失败等异常场景兜底）。</summary>
    public void ClearPending() => PendingAction = null;

    // 红绿灯状态色（亮色）：绿=下载中 黄=暂停/排队 蓝=完成 红=失败
    public string StatusColor => State switch
    {
        TaskState.Active => "#4ADE80",
        TaskState.Waiting => "#FACC15",
        TaskState.Paused => "#FACC15",
        TaskState.Completed => "#60A5FA",
        TaskState.Failed => "#F87171",
        _ => "#9CA3AF"
    };

    /// <summary>任务卡片右上角红绿灯画刷，随状态即时变化。</summary>
    public SolidColorBrush StatusDotBrush => new(FromHex(StatusColor));

    /// <summary>红绿灯外圈光晕画刷：同色径向渐变，由内向外衰减到透明。</summary>
    public RadialGradientBrush StatusGlowBrush
    {
        get
        {
            var c = FromHex(StatusColor);
            var gb = new RadialGradientBrush
            {
                Center = new Windows.Foundation.Point(0.5, 0.5),
                GradientOrigin = new Windows.Foundation.Point(0.5, 0.5),
                RadiusX = 0.5,
                RadiusY = 0.5
            };
            gb.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(0xB4, c.R, c.G, c.B), Offset = 0.0 });
            gb.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(0x00, c.R, c.G, c.B), Offset = 1.0 });
            return gb;
        }
    }

    private static Windows.UI.Color FromHex(string hex)
    {
        var v = Convert.ToUInt32(hex.TrimStart('#'), 16);
        return Windows.UI.Color.FromArgb(0xFF, (byte)((v >> 16) & 0xFF), (byte)((v >> 8) & 0xFF), (byte)(v & 0xFF));
    }

    public void Update(DownloadTaskInfo info)
    {
        // 乐观操作清除：引擎真实状态到达目标（暂停→Paused / 继续→Active|Waiting，终态视为到达），
        // 或超时兜底（引擎长时间未确认，避免图标与小字卡在过渡状态）
        if (_pendingAction != null)
        {
            var expired = (DateTime.UtcNow - _pendingAt).TotalSeconds > PendingTimeoutSeconds;
            var reached = _pendingAction == "pause"
                ? info.State is TaskState.Paused or TaskState.Removed or TaskState.Completed or TaskState.Failed
                : info.State is TaskState.Active or TaskState.Waiting or TaskState.Removed or TaskState.Completed or TaskState.Failed;
            if (expired || reached) PendingAction = null;
        }

        Model = info;
        Gid = info.Gid;
        Name = info.Name;
        State = info.State;
        Progress = Math.Round(info.Progress * 100, 1);
        SpeedText = info.State == TaskState.Active ? $"{FormatSpeed(info.DownloadSpeed)}/s" : "";
        EtaText = info.State == TaskState.Active && info.Eta.HasValue ? $"剩余 {FormatTime(info.Eta.Value)}" : "";
        SizeText = info.TotalLength > 0
            ? $"{FormatSize(info.CompletedLength)} / {FormatSize(info.TotalLength)}"
            : FormatSize(info.CompletedLength);
        StatusText = State switch
        {
            TaskState.Active => $"下载中 {Progress:0.#}%",
            TaskState.Waiting => "排队中",
            TaskState.Paused => $"已暂停 {Progress:0.#}%",
            TaskState.Completed => "已完成",
            TaskState.Failed => string.IsNullOrEmpty(info.ErrorMessage) ? "失败" : $"失败: {info.ErrorMessage}",
            TaskState.Removed => "已删除",
            _ => ""
        };
    }

    public static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        var i = 0;
        double v = bytes;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return $"{v:0.##} {units[i]}";
    }

    public static string FormatSpeed(long bps) => $"{FormatSize(bps)}";

    public static string FormatTime(TimeSpan t)
    {
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours} 小时 {t.Minutes} 分";
        if (t.TotalMinutes >= 1) return $"{t.Minutes} 分 {t.Seconds} 秒";
        return $"{t.Seconds} 秒";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}
