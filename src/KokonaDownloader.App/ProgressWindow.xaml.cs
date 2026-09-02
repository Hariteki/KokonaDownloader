using KokonaDownloader.App.Themes;
using KokonaDownloader.Core.Engine;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace KokonaDownloader.App;

/// <summary>
/// IDM 式单任务下载进度小窗：独立窗口，Acrylic 磨砂背景。
/// 由主界面在任务开始下载时弹出，跟踪单个任务进度；完成/失败后保留并显示"打开文件夹"。
/// </summary>
public partial class ProgressWindow : Window
{
    private readonly string _gid;
    private readonly DispatcherTimer _timer = new();
    /// <summary>BT 任务小窗：以方块矩阵替代进度条，窗口更高。</summary>
    public bool IsBt { get; }

    public ProgressWindow(string gid, string taskName, bool isBt = false)
    {
        _gid = gid;
        IsBt = isBt;
        InitializeComponent();

        Title = taskName;
        TitleText.Text = taskName;

        // 沉浸式标题栏：任务名融入内容区（与设置窗口同款样式），Title 仍供任务栏/Alt-Tab 显示
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleDrag);

        // 固定窗口大小：去掉调整边框与最大化能力（尺寸由主界面统一设定为 400×192）
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
        }

        // Acrylic 磨砂背景；失败则退回纯色
        if (!WindowEffects.TryApplyAcrylic(this))
            RootGrid.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

        WindowEffects.SetDarkTitleBar(this, true);
        ThemeService.Register(this);

        // BT 弹窗内容高度随分片数/位图状态变化，尺寸稳定后自适应收放窗口高度
        PieceGrid.SizeChanged += (_, _) => TryAutoFitHeight();
        RootGrid.SizeChanged += (_, _) => TryAutoFitHeight();

        _timer.Interval = TimeSpan.FromMilliseconds(500);
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();

        Closed += (_, _) =>
        {
            _timer.Stop();
            ThemeService.Unregister(this);
        };
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (App.Host?.Engine == null || !App.Host.Engine.IsRunning) return;
        try
        {
            var t = await App.Host.Engine.GetTaskAsync(_gid);
            if (t == null)
            {
                // 任务已被删除：关闭窗口
                DispatcherQueue.TryEnqueue(Close);
                return;
            }
            DispatcherQueue.TryEnqueue(() => UpdateUi(t));
        }
        catch { /* 引擎异常忽略，下次轮询再试 */ }
    }

    private void UpdateUi(DownloadTaskInfo t)
    {
        var percent = Math.Round(t.Progress * 100, 1);
        Bar.Value = percent;
        PercentText.Text = $"{percent:0.#}%";

        // BT 任务：方块矩阵替代进度条；分片位图/总数随轮询同步刷新
        PieceGrid.Visibility = t.IsBt ? Visibility.Visible : Visibility.Collapsed;
        Bar.Visibility = t.IsBt ? Visibility.Collapsed : Visibility.Visible;
        if (t.IsBt)
        {
            PieceGrid.BitField = t.BitField;
            PieceGrid.NumPieces = t.NumPieces;
            PieceGrid.IsActive = t.State == TaskState.Active;
        }

        // BT 下载/做种时叠加上传速率展示
        SpeedText.Text = t.State switch
        {
            TaskState.Active when t.UploadSpeed > 0 => $"↓{FormatBytes(t.DownloadSpeed)} · ↑{FormatBytes(t.UploadSpeed)}/s",
            TaskState.Active => $"{FormatBytes(t.DownloadSpeed)}/s",
            TaskState.Seeding when t.UploadSpeed > 0 => $"↑{FormatBytes(t.UploadSpeed)}/s",
            _ => "0 B/s"
        };
        SizeText.Text = t.TotalLength > 0
            ? $"{FormatBytes(t.CompletedLength)} / {FormatBytes(t.TotalLength)}"
            : FormatBytes(t.CompletedLength);
        EtaText.Text = t.State == TaskState.Active && t.Eta.HasValue ? $"剩余 {FormatTime(t.Eta.Value)}" : "";

        switch (t.State)
        {
            case TaskState.Active:
                StatusText.Text = "下载中";
                SetPauseButton(pause: true);
                break;
            case TaskState.Seeding:
                StatusText.Text = "做种中";
                SetPauseButton(pause: true);
                break;
            case TaskState.Waiting:
                StatusText.Text = "排队中";
                SetPauseButton(pause: true);
                break;
            case TaskState.Paused:
                StatusText.Text = "已暂停";
                SetPauseButton(pause: false);
                break;
            case TaskState.Completed:
                StatusText.Text = "已完成";
                BtnPause.Visibility = Visibility.Collapsed;
                BtnOpenFolder.Visibility = Visibility.Visible;
                _timer.Stop();
                break;
            case TaskState.Failed:
                StatusText.Text = string.IsNullOrEmpty(t.ErrorMessage) ? "下载失败" : $"失败: {t.ErrorMessage}";
                BtnPause.Visibility = Visibility.Collapsed;
                _timer.Stop();
                break;
        }

        if (t.IsBt) TryAutoFitHeight();
    }

    private double _lastAppliedContentHeight = -1;

    /// <summary>
    /// BT 弹窗高度自适应：按当前窗口宽度测量内容期望高度并 Resize 窗口，
    /// 高度变化时向上/下微调位置保持视觉居中；期望高度未变化时直接跳过，防止 Resize 回调死循环。
    /// </summary>
    private void TryAutoFitHeight()
    {
        if (!IsBt) return;
        try
        {
            var scale = RootGrid.XamlRoot?.RasterizationScale ?? 1.0;
            if (scale <= 0) scale = 1.0;
            var size = AppWindow.Size;
            if (size.Width <= 0) return;

            RootGrid.Measure(new Windows.Foundation.Size(size.Width / scale, double.PositiveInfinity));
            var desired = RootGrid.DesiredSize.Height;
            if (desired <= 0 || Math.Abs(desired - _lastAppliedContentHeight) < 1.0) return;

            var minH = (int)Math.Round(160 * scale);
            var newH = Math.Max((int)Math.Round(desired * scale), minH);
            _lastAppliedContentHeight = desired;
            if (Math.Abs(newH - size.Height) < 2) return;

            AppWindow.Resize(new Windows.Graphics.SizeInt32(size.Width, newH));
            var pos = AppWindow.Position;
            AppWindow.Move(new Windows.Graphics.PointInt32(pos.X, Math.Max(0, pos.Y - (newH - size.Height) / 2)));
        }
        catch { /* 窗口已关闭等场景忽略 */ }
    }

    private void SetPauseButton(bool pause)
    {
        // pause=true 表示当前可暂停（显示"暂停"）；否则显示"继续"
        PauseIcon.Glyph = pause ? "\uE769" : "\uE768";
        PauseLabel.Text = pause ? "暂停" : "继续";
    }

    private async void OnPauseClick(object sender, RoutedEventArgs e)
    {
        if (App.Host?.Engine == null) return;
        try
        {
            var t = await App.Host.Engine.GetTaskAsync(_gid);
            if (t == null) return;
            if (t.State is TaskState.Active or TaskState.Waiting or TaskState.Seeding)
                await App.Host.Engine.PauseAsync(_gid);
            else if (t.State == TaskState.Paused)
                await App.Host.Engine.ResumeAsync(_gid);
            await RefreshAsync();
        }
        catch { }
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        var t = GetLastTask();
        if (t != null)
        {
            var dir = !string.IsNullOrEmpty(t.FilePath) ? Path.GetDirectoryName(t.FilePath) : t.Dir;
            Shell.OpenFolder(dir, t.FilePath);
        }
    }

    private DownloadTaskInfo? GetLastTask()
    {
        try { return App.Host?.Engine.GetTaskAsync(_gid).GetAwaiter().GetResult(); }
        catch { return null; }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        var i = 0;
        double v = bytes;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return $"{v:0.##} {units[i]}";
    }

    private static string FormatTime(TimeSpan t)
    {
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours} 小时 {t.Minutes} 分";
        if (t.TotalMinutes >= 1) return $"{t.Minutes} 分 {t.Seconds} 秒";
        return $"{t.Seconds} 秒";
    }
}
