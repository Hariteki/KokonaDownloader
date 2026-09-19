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
    private bool _closed;
    private bool _engineSubscribed;
    /// <summary>BT 任务小窗：以方块矩阵替代进度条，窗口更高。</summary>
    public bool IsBt { get; }

    /// <summary>任务是否已结束（结束的小窗可被主界面回收，用于限制同时存在的窗口数）。</summary>
    public bool IsFinished { get; private set; }

    /// <summary>创建时刻：回收时按"最旧的已结束窗口"优先。</summary>
    public DateTime CreatedAt { get; } = DateTime.UtcNow;

    /// <summary>级联槽位（由主界面分配）：决定小窗相对屏幕中心的错开位置，避免并发小窗完全堆叠。</summary>
    public int Slot { get; set; }

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

        // 不再自己每 500ms 轮询：引擎本来就在轮询（StatsUpdated 带全量快照），订阅即可。
        // 原先每个小窗 2 次/秒 RPC，批量任务（上百个体检窗）时是最大的一笔常驻开销。
        if (App.Host?.Engine != null)
        {
            App.Host.Engine.EngineEvent += OnEngineEvent;
            _engineSubscribed = true;
        }

        Closed += (_, _) =>
        {
            _closed = true;
            // 必须退订：引擎生命周期长于窗口，不退订会让已关闭窗口继续被回调（泄漏 + 无效工作）
            if (_engineSubscribed && App.Host?.Engine != null)
                App.Host.Engine.EngineEvent -= OnEngineEvent;
            ThemeService.Unregister(this);
        };
        _ = RefreshOnceAsync();
    }

    /// <summary>引擎轮询线程回调：只关心本窗口对应的任务（快照里没有 = 任务已被移除）。</summary>
    private void OnEngineEvent(object? sender, EngineEventArgs e)
    {
        if (_closed) return;
        switch (e.Type)
        {
            case "StatsUpdated" when e.Tasks != null:
                var t = e.Tasks.FirstOrDefault(x => x.Gid == _gid);
                if (t == null)
                {
                    DispatcherQueue.TryEnqueue(Close);
                    return;
                }
                DispatcherQueue.TryEnqueue(() => UpdateUi(t));
                break;
            case "TaskRemoved" when e.Task?.Gid == _gid:
                DispatcherQueue.TryEnqueue(Close);
                break;
        }
    }

    /// <summary>一次性取当前状态（开窗首帧、点击暂停/继续后立即反馈用）。</summary>
    private async Task RefreshOnceAsync()
    {
        var engine = App.Host?.Engine;
        if (engine == null || !engine.IsRunning) return;
        try
        {
            // 优先复用引擎快照，取不到再单查一次
            var t = engine.TryGetRecentSnapshot(5000)?.FirstOrDefault(x => x.Gid == _gid)
                    ?? await engine.GetTaskAsync(_gid);
            if (t == null)
            {
                // 任务已被删除：关闭窗口
                DispatcherQueue.TryEnqueue(Close);
                return;
            }
            DispatcherQueue.TryEnqueue(() => UpdateUi(t));
        }
        catch { /* 引擎异常忽略，后续由引擎事件驱动刷新 */ }
    }

    private void UpdateUi(DownloadTaskInfo t)
    {
        // 终态小窗保留"打开文件夹"，但会被主界面回收（腾出窗口配额），见 MainWindow.ShouldCreateProgressWindow
        IsFinished = t.State is TaskState.Completed or TaskState.Failed or TaskState.Removed;
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
                break;
            case TaskState.Failed:
                StatusText.Text = string.IsNullOrEmpty(t.ErrorMessage) ? "下载失败" : $"失败: {t.ErrorMessage}";
                BtnPause.Visibility = Visibility.Collapsed;
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
            await RefreshOnceAsync();
        }
        catch { }
    }

    private async void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var t = await GetLastTaskAsync();
            if (t != null)
            {
                var dir = !string.IsNullOrEmpty(t.FilePath) ? Path.GetDirectoryName(t.FilePath) : t.Dir;
                Shell.OpenFolder(dir, t.FilePath);
            }
        }
        catch (Exception ex) { App.Log($"[ui] 打开文件夹失败: {ex.Message}"); }
    }

    /// <summary>取本任务当前状态（异步，不阻塞 UI 线程）。
    /// 原先用 GetAwaiter().GetResult() 同步等 RPC：aria2 无响应时 UI 线程会冻结至超时。</summary>
    private async Task<DownloadTaskInfo?> GetLastTaskAsync()
    {
        var engine = App.Host?.Engine;
        if (engine == null || !engine.IsRunning) return null;
        try
        {
            return await engine.GetTaskAsync(_gid);
        }
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
