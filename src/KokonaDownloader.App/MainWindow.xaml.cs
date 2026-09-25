using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using KokonaDownloader.App.Themes;
using KokonaDownloader.App.ViewModels;
using KokonaDownloader.Core;
using KokonaDownloader.Core.Engine;
using KokonaDownloader.Core.Notifications;
using KokonaDownloader.Core.Settings;
using KokonaDownloader.Core.Themes;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.UI;

namespace KokonaDownloader.App;

public partial class MainWindow : Window, IDisposable
{
    private readonly ObservableCollection<TaskItemViewModel> _tasks = new();
    private readonly Dictionary<string, TaskItemViewModel> _taskMap = new();
    private readonly DispatcherTimer _timer = new();
    /// <summary>界面刷新定时器当前是否在跑（只在 UI 线程读写，见 StopTimerForHidden/ResumeRefreshTimer）。</summary>
    private bool _timerRunning = true;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _showEventWait;
    private string _filter = "all";
    private string _search = string.Empty;
    private bool _exiting;
    /// <summary>每个任务对应的进度小窗（IDM 式），任务结束后保留引用以便关闭。</summary>
    private readonly Dictionary<string, ProgressWindow> _progressWindows = new();

    /// <summary>同时存在的进度小窗上限：每个窗口约 88 个句柄 / 3.3 MB，
    /// 无上限时"扩展连发 200 个链接"实测会造成 18 000 句柄 / 729 MB 内存与满屏弹窗。</summary>
    private const int MaxLiveProgressWindows = 6;
    /// <summary>突发限流：一个窗口期内最多新建几个小窗（批量任务不刷屏）。</summary>
    private const int MaxNewWindowsPerBurst = 3;
    private static readonly TimeSpan WindowBurstInterval = TimeSpan.FromSeconds(5);
    private readonly Queue<DateTime> _recentWindowCreations = new();

    /// <summary>两次实际建窗之间的最小间隔：XAML 加载 + 原生背景挂载 + 窗口激活都是重活，
    /// 密集连发（批量任务）时实测会命中 XAML 层原生故障（Microsoft.UI.Xaml.dll 0xc000027b，
    /// 表现为未处理 XamlParseException 后进程直接崩溃）。拉开间隔即可规避。</summary>
    private static readonly TimeSpan MinWindowCreationGap = TimeSpan.FromMilliseconds(600);
    private DateTime _lastWindowCreationUtc = DateTime.MinValue;

    /// <summary>引擎最近一次轮询推送的快照与统计（StatsUpdated 事件写入，界面刷新时直接渲染）。</summary>
    private volatile List<DownloadTaskInfo>? _snapshot;
    private volatile GlobalStat? _stats;

    /// <summary>悬停投影只在首帧模板实例化后挂载一次。</summary>
    private bool _shadowsAttached;

    /// <summary>WinUI 同一时刻只允许一个 ContentDialog 处于打开状态：第二个 ShowAsync 会抛
    /// COMException(0x80000019)「Only a single ContentDialog can be open at any time.」。
    /// 主窗口里既有用户点出来的对话框（新建下载/批量删除/删除确认），也有浏览器扩展经 API 触发的
    /// 重复提醒弹窗，两者会互相撞车（历史上 09-01 磁力链接、09-05/09-13 重复提醒都因此失败）。
    /// 统一走这道闸门串行化：同时只显示一个，后来的按到达顺序排队，避免直接抛错丢提示。</summary>
    private readonly SemaphoreSlim _dialogGate = new(1, 1);

    /// <summary>串行显示 ContentDialog，返回用户选择结果（闸门忙时排队等待，不会抛 0x80000019）。</summary>
    private async Task<ContentDialogResult> ShowDialogAsync(Func<ContentDialog> createDialog)
    {
        // 窗口已关闭（退出/关窗竞态）：闸门可能已释放，直接按"未打开对话框"处理，
        // 语义等同用户取消，调用方现有的 None 分支已覆盖这种结果
        if (_disposed) return ContentDialogResult.None;
        await _dialogGate.WaitAsync();
        try
        {
            return await createDialog().ShowAsync();
        }
        finally
        {
            ReleaseDialogGate();
        }
    }

    /// <summary>窗口关闭时闸门可能已被 Dispose（关窗与在途对话框竞态），归还动作必须容错。</summary>
    private void ReleaseDialogGate()
    {
        try { _dialogGate.Release(); }
        catch (ObjectDisposedException) { }
    }

    private bool _disposed;

    /// <summary>释放本窗口自有的内核/同步资源（第四轮 CA1001/CA2213）：
    /// 命名事件句柄（内核对象）、线程池等待项、对话框闸门信号量。
    /// 原先 _showEvent 只在窗口关闭事件里释放、_dialogGate 从未释放；
    /// 现集中到 Dispose，由 Closed 事件调用（重复调用安全）。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _showEventWait?.Unregister(null); } catch { }
        _showEventWait = null;
        try { _showEvent?.Dispose(); } catch { }
        _showEvent = null;
        try { _dialogGate.Dispose(); } catch { }
    }

    /// <summary>WinUI3 唯一可挂到 UIElement.Shadow 的具体类型是 ThemeShadow（合成层 DropShadow 不派生自它），
    /// 共享一个实例挂到所有悬停投影宿主上，接收者为根 Grid。</summary>
    private void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        if (_shadowsAttached) return;
        _shadowsAttached = true;
        try
        {
            var shadow = new ThemeShadow();
            shadow.Receivers.Add(RootGrid);
            foreach (var host in FindShadowHosts(RootGrid))
            {
                host.Shadow = shadow;
                // Translation 在本地自定义类型上无法经 XAML 属性解析（XBF Property Not Found），改在挂载时代码设置
                host.Translation = new System.Numerics.Vector3(0, 0, 24);
            }
        }
        catch (Exception ex) { App.Log($"挂载悬停投影失败: {ex.Message}"); }
    }

    private static IEnumerable<FrameworkElement> FindShadowHosts(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement { Name: "TabShadowHost" or "BtShadowHost" })
                yield return (FrameworkElement)child;
            foreach (var nested in FindShadowHosts(child))
                yield return nested;
        }
    }

    public MainWindow()
    {
        InitializeComponent();
        // 两个列表共用同一 ObservableCollection：BT 专用页仅在 BT 筛选下可见，
        // 此时 MatchesFilter 已保证集合中只有 BT 任务，无需维护第二份数据源
        TaskList.ItemsSource = _tasks;
        BtList.ItemsSource = _tasks;
        HookSelectionVisuals(TaskList);
        HookSelectionVisuals(BtList);
        RootGrid.Loaded += OnRootLoaded;

        // 沉浸式标题栏：无系统色带，标题文字融入内容区（与设置窗口同款样式）
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleDrag);

        // 最小窗口尺寸：保证工具栏/列表/状态栏完整显示，不被裁剪重叠
        // 940 = Tab 栏五项 + 筛选按钮(≈540) 与右上角 BT下载 + 搜索框(≈310) 同行容纳的最低宽度
        WindowEffects.SetMinSize(this, 940, 560);
        // 初始即以最小尺寸启动：等价于用户手动缩到最小后的状态
        try
        {
            var s = GetDpiScale();
            AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(940 * s), (int)(560 * s)));
        }
        catch (Exception ex) { App.Log($"设置初始窗口尺寸失败: {ex.Message}"); }
        // 窗口背景与 SystemBackdrop 由 ThemeService.ApplyTransparency 统一管理（磨砂透明为唯一主题）

        _timer.Interval = TimeSpan.FromMilliseconds(900);
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();

        ApplyTheme();
        // 主题服务：资源覆盖 + 原生标题栏着色（内部已订阅设置变更与系统强调色变化）
        ThemeService.Register(this);
        ThemeService.ThemeChanged += OnThemeChangedRefreshSelectionVisuals;
        // 窗口激活后再应用一次，确保标题栏颜色在首帧之后生效；
        // 顺带恢复刷新定时器：从托盘/任务栏重新显示窗口时，Tick 可能已因"窗口不可见"而停摆
        Activated += (_, _) => { ApplyTheme(); ResumeRefreshTimer(); };
        BuildThemeMenu();

        // 引擎事件：托盘进度 + 完成/失败通知（引擎轮询线程触发，需切回 UI 线程）
        if (App.Host != null)
        {
            App.Host.Engine.EngineEvent += OnEngineEvent;
            // 浏览器扩展经 /api/download 送来的单条磁力链接：弹独立确认窗口（不弹主窗口）
            App.Host.Api.MagnetConfirmRequested += OnApiMagnetConfirm;
            // 扩展送来的链接命中下载中的重复任务：主窗口弹窗提醒（任务已被跳过）
            App.Host.Api.DuplicateTaskNoticeRequested += OnApiDuplicateNotice;
        }

        // 单实例：监听"显示窗口"命名事件（第二个实例启动时触发）
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\KokonaDownloader_ShowWindow");
        _showEventWait = ThreadPool.RegisterWaitForSingleObject(
            _showEvent, (_, _) => DispatcherQueue.TryEnqueue(App.ShowMainWindow), null, -1, true);

        // 窗口/任务栏图标：titlebar-tray 造型（下载箭头），与托盘视觉一致
        try { AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "icons", "tray.ico")); }
        catch (Exception ex) { App.Log($"设置窗口图标失败: {ex.Message}"); }

        // 关闭行为：按设置决定最小化到托盘还是退出
        AppWindow.Closing += OnWindowClosing;
        Closed += async (_, _) =>
        {
            _timer.Stop();
            Dispose(); // 释放命名事件/线程池等待项/对话框闸门（见 Dispose）
            ThemeService.ThemeChanged -= OnThemeChangedRefreshSelectionVisuals;
            ThemeService.Unregister(this);
            if (App.Host != null)
            {
                App.Host.Engine.EngineEvent -= OnEngineEvent;
                App.Host.Api.MagnetConfirmRequested -= OnApiMagnetConfirm;
                await App.Host.DisposeAsync();
            }
            App.Tray?.Dispose();
        };

        _ = RefreshAsync();
    }

    private void OnWindowClosing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (_exiting) return;
        if (App.Host?.Settings.Current.MinimizeToTrayOnClose == true)
        {
            args.Cancel = true;
            AppWindow.Hide();
            StopTimerForHidden(); // 隐藏后界面刷新不再有产出，定时器立即让出 CPU（见 RefreshAsync）
            return;
        }
        // 点 X 直接退出时同样走快速退出路径，避免优雅关闭的长等待与进程残留
        args.Cancel = true;
        RequestExit();
    }

    /// <summary>真正退出（托盘菜单 / 关闭窗口）。
    /// 快速彻底路径：强杀 aria2 进程树 + 同步落盘关键数据，然后 Environment.Exit 立即结束进程。
    /// 不再走"SaveSession → 等待 aria2 退出"的优雅关闭（最长约 7 秒卡顿）——会话每 10 秒自动
    /// 落盘、启动前有墓碑过滤，硬杀安全；也不再依赖"所有窗口关闭"（IDM 式进度小窗会拖住
    /// 消息循环导致进程残留），Environment.Exit 无视一切存活窗口与后台线程。</summary>
    public void RequestExit()
    {
        if (_exiting) return;
        _exiting = true;
        try { App.Tray?.Dispose(); } catch { }
        try { App.Host?.FastShutdown(); } catch { }
        Environment.Exit(0);
    }

    private void OnEngineEvent(object? sender, EngineEventArgs e)
    {
        switch (e.Type)
        {
            case "StatsUpdated" when e.Tasks != null:
                // 引擎每次轮询都会推送全量快照：界面与托盘共用它，各自不再单独轮询
                _snapshot = e.Tasks;
                _stats = e.Stats;
                var progress = TrayProgress.Compute(e.Tasks);
                DispatcherQueue.TryEnqueue(() => App.Tray?.Update(progress));
                break;
            case "TaskChanged" when e.Task != null:
                var t = e.Task;
                var isNewTask = e.IsNewTask;
                App.Log($"[task] TaskChanged gid={t.Gid} state={t.State} name={t.Name} 新建={isNewTask}");
                DispatcherQueue.TryEnqueue(() =>
                {
                    HandleTaskFinished(t);
                    HandleProgressWindow(t, isNewTask);
                });
                break;
            case "TaskRemoved" when e.Task != null:
                var removed = e.Task;
                App.Log($"[task] TaskRemoved gid={removed.Gid}");
                DispatcherQueue.TryEnqueue(() => HandleProgressWindow(removed, false));
                break;
        }
    }

    /// <summary>IDM 式进度小窗：仅在新建任务时弹出（暂停后继续/重启恢复不弹），结束/移除时关闭并清理。</summary>
    private void HandleProgressWindow(DownloadTaskInfo t, bool isNewTask)
    {
        App.Log($"[ui] HandleProgressWindow gid={t.Gid} state={t.State} 新建={isNewTask}");
        if (t.State is TaskState.Active or TaskState.Waiting)
        {
            // 只有引擎标记的"本会话新建任务"才弹窗；暂停后继续（状态重回 Active）不弹
            if (!isNewTask) return;
            if (!_progressWindows.ContainsKey(t.Gid))
            {
                if (!ShouldCreateProgressWindow(t.Gid)) return;
                try
                {
                    var win = new ProgressWindow(t.Gid, t.Name, t.IsBt);
                    // 先分配槽位再登记：AssignWindowSlot 扫描存活窗口，若先登记会把新窗自身（默认 Slot=0）
                    // 误判为占用 0 号位，导致首个窗口错位、满员时回退到 5 号位造成重叠
                    win.Slot = AssignWindowSlot();
                    win.Closed += (_, _) => _progressWindows.Remove(t.Gid);
                    _progressWindows[t.Gid] = win;
                    SizeAndPlaceProgressWindow(win);
                    // 激活与强制前台延迟到下一调度周期：把"建窗（XAML 加载 + 原生背景挂载）"与
                    // "窗口激活"拆到不同帧，避免高并发下同一帧内密集触发原生调用（实测会命中
                    // XAML 层原生故障导致进程崩溃）。窗口若在此之前已关闭，Activate 异常被吞掉。
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        try
                        {
                            win.Activate();
                            // 下载可能由扩展在后台触发，必须强制把小窗拉到前台，否则用户看不到
                            WindowEffects.ForceForeground(win);
                        }
                        catch (Exception ex) { App.Log($"激活进度小窗失败: {ex.Message}"); }
                    });
                    App.Log($"[ui] 进度小窗已创建 gid={t.Gid} title={t.Name} slot={win.Slot}");
                }
                catch (Exception ex) { App.Log($"打开进度小窗失败: {ex.Message}"); }
            }
        }
        else if (t.State is TaskState.Removed)
        {
            if (_progressWindows.TryGetValue(t.Gid, out var win))
            {
                _progressWindows.Remove(t.Gid);
                try { win.Close(); } catch { }
            }
        }
        // Completed/Failed：小窗自身轮询会停在完成态并显示"打开文件夹"，不自动关闭
    }

    /// <summary>
    /// 进度小窗配额判定：批量任务不再"一个任务一个窗口"地无限弹出。
    /// 规则（先限流、再腾位、腾不出才放弃）：
    ///  1. 突发限流：<see cref="WindowBurstInterval"/> 内最多新建 <see cref="MaxNewWindowsPerBurst"/> 个（扩展连发/批量粘贴不刷屏）；
    ///  2. 存活窗口已达 <see cref="MaxLiveProgressWindows"/>：优先回收**最旧的已结束窗口**腾出位置；
    ///  3. 存活的全是进行中窗口：本次不新建（任务本身照常在主列表/托盘里可见）。
    /// </summary>
    private bool ShouldCreateProgressWindow(string gid)
    {
        var now = DateTime.UtcNow;

        // 最小建窗间隔：距上次实际建窗不足 MinWindowCreationGap 时本次不建（任务仍可见于主列表）。
        // 这是防 XAML 层原生故障的关键闸门——批量任务连发时把"建窗风暴"摊平成串行慢速创建。
        if (now - _lastWindowCreationUtc < MinWindowCreationGap)
        {
            App.Log($"[ui] 进度小窗距上次创建不足 {MinWindowCreationGap.TotalMilliseconds:0}ms，跳过 gid={gid}");
            return false;
        }

        while (_recentWindowCreations.Count > 0 && now - _recentWindowCreations.Peek() > WindowBurstInterval)
            _recentWindowCreations.Dequeue();

        if (_recentWindowCreations.Count >= MaxNewWindowsPerBurst)
        {
            App.Log($"[ui] 进度小窗突发限流，跳过 gid={gid}（{WindowBurstInterval.TotalSeconds:0}s 内已开 {MaxNewWindowsPerBurst} 个）");
            return false;
        }

        while (_progressWindows.Count >= MaxLiveProgressWindows)
        {
            var staleGid = _progressWindows
                .Where(kv => kv.Value.IsFinished)
                .OrderBy(kv => kv.Value.CreatedAt)
                .Select(kv => kv.Key)
                .FirstOrDefault();
            if (staleGid == null)
            {
                App.Log($"[ui] 进度小窗已达上限 {MaxLiveProgressWindows}，跳过 gid={gid}");
                return false;
            }
            if (_progressWindows.Remove(staleGid, out var stale))
            {
                try { stale.Close(); } catch { }
                App.Log($"[ui] 回收已结束的进度小窗 gid={staleGid} 以腾出配额");
            }
        }

        _recentWindowCreations.Enqueue(now);
        _lastWindowCreationUtc = now;
        return true;
    }

    /// <summary>把进度小窗放到主显示器屏幕中央附近，尺寸固定为紧凑卡片。
    /// 并发小窗按 Slot 级联错开（第 0 号正中央，之后每个向右下移一格），不再全部堆叠在同一坐标。</summary>
    private void SizeAndPlaceProgressWindow(ProgressWindow win)
    {
        try
        {
            // AppWindow.Position/Size 均为物理像素，直接按物理像素计算并设置
            // 宽度留足信息条三列（速度/大小/剩余时间）不挤压；高度贴合内容自然高度，避免按钮下方大片空白
            var scale = GetDpiScale();
            var w = (int)(400 * scale);
            // BT 任务小窗更高：容纳 Motrix 式方块矩阵（最多 400 块 ≈ 24 列 × 17 行 ≈ 252px + 统计行）
            var h = (int)((win.IsBt ? 460 : 192) * scale);
            win.AppWindow.Resize(new Windows.Graphics.SizeInt32(w, h));

            // 居中：取主窗口所在显示器的工作区（物理像素），小窗置于其正中
            var display = GetDisplayArea();
            int x, y;
            if (display != null)
            {
                x = display.WorkArea.X + (display.WorkArea.Width - w) / 2;
                y = display.WorkArea.Y + (display.WorkArea.Height - h) / 2;
            }
            else
            {
                // 兜底：以主窗口为中心
                var mainPos = AppWindow.Position;
                var mainSize = AppWindow.Size;
                x = mainPos.X + (mainSize.Width - w) / 2;
                y = mainPos.Y + (mainSize.Height - h) / 2;
            }

            // 级联偏移：slot 0 保持正中央，slot n 向右下错开 n 格（物理像素随 DPI 缩放），
            // 保证并发小窗互不完全重叠、各自可见
            x += (int)(win.Slot * 28 * scale);
            y += (int)(win.Slot * 20 * scale);

            win.AppWindow.Move(new Windows.Graphics.PointInt32(Math.Max(0, x), Math.Max(0, y)));
        }
        catch { }
    }

    /// <summary>为新小窗分配级联槽位：取当前存活窗口未占用的最小槽位（0..MaxLiveProgressWindows-1）。
    /// 窗口关闭后从 _progressWindows 移除，其槽位自动释放给后续新窗。</summary>
    private int AssignWindowSlot()
    {
        var used = new HashSet<int>(_progressWindows.Values.Select(w => w.Slot));
        for (int s = 0; s < MaxLiveProgressWindows; s++)
            if (!used.Contains(s)) return s;
        return MaxLiveProgressWindows - 1;
    }

    /// <summary>获取主窗口所在显示器的工作区信息（物理像素）。</summary>
    private Microsoft.UI.Windowing.DisplayArea? GetDisplayArea()
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            return Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(windowId, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest);
        }
        catch { return null; }
    }

    private double GetDpiScale()
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            const int MONITOR_DEFAULTTONEAREST = 2;
            var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (monitor != IntPtr.Zero && GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0 && dpiX > 0)
                return dpiX / 96.0;
        }
        catch { }
        return 1.0;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [System.Runtime.InteropServices.DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    private void HandleTaskFinished(DownloadTaskInfo t)
    {
        if (t.State is not (TaskState.Completed or TaskState.Failed)) return;
        if (App.Host == null) return;
        if (App.Host.Notified.Contains(t.Gid)) return; // 同一任务只通知一次（跨重启持久）
        if (App.Host.Settings.Current.NotificationsEnabled != true) return;
        // 重启后历史任务会被引擎重新上报，用完成时间窗口过滤，避免通知回放
        if (!NotificationRules.ShouldNotify(t.FinishedAt, DateTime.Now)) return;

        App.Host.Notified.Mark(t.Gid);
        if (t.State == TaskState.Completed)
            App.Notifications?.ShowDownloadCompleted(t.Name, t.Dir ?? string.Empty, t.FilePath);
        else
            App.Notifications?.ShowDownloadFailed(t.Name, t.ErrorMessage ?? string.Empty);
    }

    private void ApplyTheme()
    {
        // 已移除浅色模式：主界面固定深色，配合 Mica 磨砂呈现层次感
        RootGrid.RequestedTheme = ElementTheme.Dark;
        WindowEffects.SetDarkTitleBar(this, true);
    }

    /// <summary>工具栏"主题配色"下拉菜单：内置主题列表 + 色点图标，当前项整行高亮。</summary>
    private void BuildThemeMenu()
    {
        foreach (var p in ThemeCatalog.BuiltIn)
        {
            var item = new MenuFlyoutItem { Text = p.Name, Tag = p.Id };
            try
            {
                var accent = p.Id == ThemeCatalog.SystemId
                    ? ThemeService.GetOsAccent() ?? PaletteColor.ParseHexOrThrow(p.Accent)
                    : PaletteColor.ParseHexOrThrow(p.Accent);
                item.Icon = new PathIcon
                {
                    Data = new EllipseGeometry { Center = new Point(8, 8), RadiusX = 6, RadiusY = 6 },
                    Foreground = new SolidColorBrush(ThemeService.ToColor(accent))
                };
            }
            catch { }
            item.Click += OnThemeMenuItemClick;
            ThemeMenu.Items.Add(item);
        }
        ThemeMenu.Opening += (_, _) => RefreshThemeMenu();
        RefreshThemeMenu();
    }

    private void RefreshThemeMenu()
    {
        var current = ThemeService.Current.Id;
        foreach (var i in ThemeMenu.Items.OfType<MenuFlyoutItem>())
        {
            var selected = string.Equals(i.Tag as string, current, StringComparison.OrdinalIgnoreCase);
            if (!selected) { i.Background = null; continue; }
            var p = ThemeCatalog.BuiltIn.FirstOrDefault(t => string.Equals(t.Id, i.Tag as string, StringComparison.OrdinalIgnoreCase));
            if (p == null) continue;
            try
            {
                var accent = p.Id == ThemeCatalog.SystemId
                    ? ThemeService.GetOsAccent() ?? PaletteColor.ParseHexOrThrow(p.Accent)
                    : PaletteColor.ParseHexOrThrow(p.Accent);
                var c = ThemeService.ToColor(accent);
                i.Background = new SolidColorBrush(Color.FromArgb(0x33, c.R, c.G, c.B));
            }
            catch { }
        }
    }

    private void OnThemeMenuItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: string id })
            ThemeService.SetThemeColor(id);
    }

    /// <summary>
    /// 主窗不可见时停掉界面刷新定时器（幂等）。隐藏态继续跑等于纯烧 CPU：
    /// 实测最小化到托盘后仍占 0.57 % 单核（测试报告 §12-B 表 2），而这段时间列表没人看。
    /// 引擎侧轮询与托盘图标更新不受影响，恢复显示时 ResumeRefreshTimer 会重新拉起。
    /// </summary>
    private void StopTimerForHidden()
    {
        if (!_timerRunning) return;
        _timerRunning = false;
        _timer.Stop();
        App.Log("[ui] 主窗口不可见，暂停界面刷新定时器");
    }

    /// <summary>主窗可见时恢复界面刷新（幂等；Activated 与托盘唤起路径都会调用）。</summary>
    public void ResumeRefreshTimer()
    {
        if (_timerRunning) return;
        _timerRunning = true;
        _timer.Start();
        App.Log("[ui] 主窗口可见，恢复界面刷新定时器");
    }

    /// <summary>外部告知"窗口已隐藏"（App 的 --minimized 静默启动路径），立刻停表。</summary>
    public void NotifyHidden() => DispatcherQueue.TryEnqueue(StopTimerForHidden);

    /// <summary>
    /// 界面定时刷新。数据来源是引擎轮询推送的快照（<see cref="OnEngineEvent"/> 里的 StatsUpdated），
    /// **不再自己打 RPC**：原先这里每 900ms 要发 4 次 aria2 调用（3 次列表 + 1 次统计），
    /// 与引擎自身的轮询完全重复，是常驻开销的第二大来源。
    /// 仅首帧（还没收到过引擎事件）才兜底拉一次。
    /// </summary>
    private async Task RefreshAsync()
    {
        // 兜底停表：不论窗口经哪条路径隐藏（点 X 收进托盘、开机静默启动、外部直接 Hide），
        // 至多多跑一个 Tick 就会在这里自己停下来；恢复显示由 Activated / ResumeRefreshTimer 负责。
        // 判据用 WindowVisibleByAnySignal：只看 AppWindow.IsVisible 会在"窗口被外部 ShowWindow 复原、
        // 而 WinUI 标记仍是 false"时把界面永久冻住（屏幕上有窗口却再也不刷新），实测复现过。
        if (!WindowEffects.WindowVisibleByAnySignal(this))
        {
            StopTimerForHidden();
            return;
        }

        if (App.Host?.Engine == null || !App.Host.Engine.IsRunning)
        {
            ConnStatusText.Text = "引擎未运行";
            return;
        }

        var tasks = _snapshot;
        var stat = _stats;
        if (tasks == null || stat == null)
        {
            try
            {
                tasks = await App.Host.Engine.GetAllTasksAsync();
                stat = await App.Host.Engine.GetGlobalStatAsync();
            }
            catch (Exception ex)
            {
                DispatcherQueue.TryEnqueue(() => ConnStatusText.Text = $"引擎异常: {ex.Message}");
                return;
            }
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            GlobalSpeedText.Text = $"{TaskItemViewModel.FormatSpeed(stat.DownloadSpeed)}/s";
            ActiveCountText.Text = $"活动 {stat.NumActive} · 等待 {stat.NumWaiting}";
            ConnStatusText.Text = "引擎运行中";
            MergeTasks(tasks);
            // 兜底：定时刷新时重刷可见容器的选中视觉，覆盖虚拟化回收再实例化。
            // 常规列表与 BT 专用页互斥显示，隐藏的那一个没有可见内容可刷，跳过（省一半视觉遍历）。
            if (TaskList.Visibility == Visibility.Visible) ApplySelectionVisuals(TaskList);
            if (BtPage.Visibility == Visibility.Visible) ApplySelectionVisuals(BtList);
        });
    }

    private void MergeTasks(List<DownloadTaskInfo> tasks)
    {
        var seen = new HashSet<string>();
        foreach (var t in tasks)
        {
            seen.Add(t.Gid);
            if (_taskMap.TryGetValue(t.Gid, out var vm))
                vm.Update(t);
            else
            {
                var newVm = new TaskItemViewModel();
                newVm.Update(t);
                _taskMap[t.Gid] = newVm;
            }
        }
        // 移除已不存在的
        foreach (var gid in _taskMap.Keys.Where(k => !seen.Contains(k)).ToList())
            _taskMap.Remove(gid);

        ApplyView();
    }

    private void ApplyView()
    {
        // BT 概要条属于 BT 专用页：三趟 LINQ 扫描 + 拼串在隐藏页每 900ms 白做一次没有意义，
        // 切到 BT 页时 OnFilterChanged 已翻好可见性，这里会照常刷新一次。
        if (BtPage.Visibility == Visibility.Visible) UpdateBtSummary();
        var filtered = _taskMap.Values.Where(MatchesFilter).ToList();
        // 排序：恒定按添加时间倒序（新任务在顶部），不随状态变化，避免暂停/继续等操作导致列表跳动
        filtered = filtered
            .OrderByDescending(t => t.Model.AddedAt)
            .ToList();

        // 差量更新：成员与顺序完全一致时不动集合，避免整表重建导致行闪烁
        if (_tasks.Count == filtered.Count)
        {
            var identical = true;
            for (var i = 0; i < filtered.Count; i++)
            {
                if (!ReferenceEquals(_tasks[i], filtered[i])) { identical = false; break; }
            }
            if (identical)
            {
                EmptyState.Visibility = _tasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                return;
            }
        }
        // 先移除不在结果中的项，再把目标项逐个就位（移动而非重建，仅变动行刷新）
        for (var i = _tasks.Count - 1; i >= 0; i--)
            if (!filtered.Contains(_tasks[i])) _tasks.RemoveAt(i);
        for (var i = 0; i < filtered.Count; i++)
        {
            if (i < _tasks.Count && ReferenceEquals(_tasks[i], filtered[i])) continue;
            var cur = _tasks.IndexOf(filtered[i]);
            if (cur >= 0) _tasks.Move(cur, i);
            else _tasks.Insert(i, filtered[i]);
        }
        EmptyState.Visibility = _tasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>BT 专用页概要条：任务总数 / 下载中 / 做种中（数据源自全量任务表，与筛选无关）。</summary>
    private void UpdateBtSummary()
    {
        var bts = _taskMap.Values.Where(t => t.IsBt).ToList();
        BtSummaryText.Text = bts.Count == 0
            ? "暂无 BT 任务"
            : $"BT 任务 {bts.Count} · 下载中 {bts.Count(t => t.State == TaskState.Active)} · 做种 {bts.Count(t => t.State == TaskState.Seeding)}";
    }

    private bool MatchesFilter(TaskItemViewModel t)
    {
        var okFilter = _filter switch
        {
            "bt" => t.IsBt,
            "active" => t.State == TaskState.Active || t.State == TaskState.Waiting,
            "paused" => t.State == TaskState.Paused,
            "done" => t.State == TaskState.Completed,
            "failed" => t.State == TaskState.Failed,
            _ => true
        };
        var okSearch = string.IsNullOrEmpty(_search) ||
                       t.Name.Contains(_search, StringComparison.OrdinalIgnoreCase);
        return okFilter && okSearch;
    }

    #region 事件处理

    private void OnFilterChanged(object sender, RoutedEventArgs e)
    {
        // BT 入口在右上角，与 Tab 栏不同容器：WinUI 3 的 GroupName 互斥不跨容器生效，这里显式同步选中态
        if (ReferenceEquals(sender, FilterBt))
        {
            var checkedTab = new[] { FilterAll, FilterActive, FilterPaused, FilterDone, FilterFailed }
                .FirstOrDefault(t => t.IsChecked == true);
            if (checkedTab != null) checkedTab.IsChecked = false;
        }
        else
        {
            FilterBt.IsChecked = false;
        }
        _filter = (FilterBt.IsChecked == true) ? "bt"
            : (FilterActive.IsChecked == true) ? "active"
            : (FilterPaused.IsChecked == true) ? "paused"
            : (FilterDone.IsChecked == true) ? "done"
            : (FilterFailed.IsChecked == true) ? "failed" : "all";
        // 视图切换：BT 筛选显示专用页（完整方块矩阵），其余显示常规列表；批量操作作用于当前可见列表
        var btView = _filter == "bt";
        TaskList.Visibility = btView ? Visibility.Collapsed : Visibility.Visible;
        BtPage.Visibility = btView ? Visibility.Visible : Visibility.Collapsed;
        ApplyView();
    }

    private void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        _search = sender.Text.Trim();
        ApplyView();
    }

    private async void OnNewClick(object sender, RoutedEventArgs e)
    {
        App.Log("[ui] 新建下载按钮点击");
        try { await ShowNewDownloadDialog(); }
        catch (Exception ex) { App.Log($"[ui] 新建下载对话框异常: {ex.Message}"); }
    }

    private async void OnPauseAllClick(object sender, RoutedEventArgs e)
    {
        if (App.Host?.Engine == null) return;
        try { await App.Host.Engine.PauseAllAsync(); }
        catch (Exception ex) { App.Log($"[ui] 全部暂停失败: {ex.Message}"); }
    }

    private async void OnResumeAllClick(object sender, RoutedEventArgs e)
    {
        if (App.Host?.Engine == null) return;
        try { await App.Host.Engine.ResumeAllAsync(); }
        catch (Exception ex) { App.Log($"[ui] 全部恢复失败: {ex.Message}"); }
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e) => ShowSettingsDialog();

    private TaskItemViewModel? ItemFromButton(object sender) =>
        (sender as FrameworkElement)?.DataContext as TaskItemViewModel;

    private async void OnItemPauseClick(object sender, RoutedEventArgs e)
    {
        if (ItemFromButton(sender) is { } vm && App.Host?.Engine != null)
        {
            // 乐观反馈：图标立即切换 + "暂停中"小字，引擎确认后由 Update 清除
            vm.MarkPending("pause");
            try { await App.Host.Engine.PauseAsync(vm.Gid); }
            catch { vm.ClearPending(); }
        }
    }

    private async void OnItemResumeClick(object sender, RoutedEventArgs e)
    {
        if (ItemFromButton(sender) is { } vm && App.Host?.Engine != null)
        {
            // 乐观反馈：图标立即切换 + "恢复下载中"小字，引擎确认后由 Update 清除
            vm.MarkPending("resume");
            try { await App.Host.Engine.ResumeAsync(vm.Gid); }
            catch { vm.ClearPending(); }
        }
    }

    private void OnItemOpenClick(object sender, RoutedEventArgs e)
    {
        if (ItemFromButton(sender) is { } vm) OpenFolder(vm.Model);
    }

    /// <summary>双击文件名：打开所在位置并在资源管理器中选中该文件。</summary>
    private void OnItemDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        // 双击的是操作按钮（暂停/继续/打开/删除）时不触发打开文件夹
        if (IsWithinButton(e.OriginalSource as DependencyObject)) return;
        // 双击打开文件夹时取消"再击取消选择"的待执行动作，避免误清选中
        _pendingToggle = null;
        if ((sender as FrameworkElement)?.DataContext is TaskItemViewModel vm)
            OpenFolder(vm.Model);
    }

    private static bool IsWithinButton(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is Microsoft.UI.Xaml.Controls.Primitives.ButtonBase) return true;
            source = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(source);
        }
        return false;
    }

    private async void OnItemDeleteClick(object sender, RoutedEventArgs e)
    {
        if (ItemFromButton(sender) is not { } vm) return;
        try { await ConfirmDeleteAsync(vm); }
        catch (Exception ex) { App.Log($"[ui] 删除任务异常: {ex.Message}"); }
    }

    private void OnTaskRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        // 主列表与 BT 专用页共用右键菜单；单选作用于触发事件的列表
        if (e.OriginalSource is FrameworkElement { DataContext: TaskItemViewModel vm })
        {
            if (sender is ListView list && !list.SelectedItems.Contains(vm))
                list.SelectedItem = vm;
            ShowContextMenu(vm, (FrameworkElement)sender);
        }
    }

    #region 多选与批量操作

    // 批量操作作用于当前可见列表：BT 页操作 BtList，其余页操作 TaskList。
    // 两列表共用同一 ObservableCollection，且 MatchesFilter 保证 BT 页下集合内只有 BT 任务，操作安全
    private ListView ActiveList => _filter == "bt" ? BtList : TaskList;

    private void OnSelectAllClick(object sender, RoutedEventArgs e) => ActiveList.SelectAll();

    private void OnInvertSelectionClick(object sender, RoutedEventArgs e)
    {
        var list = ActiveList;
        var selected = list.SelectedItems.ToHashSet();
        var inverted = list.Items.Where(i => !selected.Contains(i)).ToList();
        list.SelectedItems.Clear();
        foreach (var i in inverted)
            list.SelectedItems.Add(i);
    }

    private void OnClearSelectionClick(object sender, RoutedEventArgs e) => ActiveList.SelectedItems.Clear();

    #region 再击取消选择

    // Extended 模式下普通单击已选中项不会取消选择（需 Ctrl+单击，不可发现）。
    // 这里在"单击后选择结果与单击前完全一致"时，延迟 300ms 取消该单选，
    // 延迟窗口内若发生双击（打开文件夹）则撤销本次取消。
    private HashSet<object>? _preClickSelection;
    private (ListView list, object item)? _pendingToggle;
    private DispatcherTimer? _toggleTimer;

    private void OnListPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not ListView list) return;
        var point = e.GetCurrentPoint(list);
        // 只记录左键按下时的选中快照；右键菜单、按钮点击（事件被 Button 拦截）不记录
        _preClickSelection = point.Properties.IsLeftButtonPressed
            ? new HashSet<object>(list.SelectedItems)
            : null;
    }

    private void OnListTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not ListView list) return;
        var pre = _preClickSelection;
        _preClickSelection = null;
        if (pre is null || pre.Count != 1) return;
        if (e.OriginalSource is not DependencyObject src) return;
        var item = ItemFromSource(src);
        if (item is null || !pre.Contains(item)) return;
        // Ctrl/Shift 组合点击会改变选择结果，走不进这个分支；只有"点击后选择纹丝不动"才切换
        if (list.SelectedItems.Count == 1 && list.SelectedItems.Contains(item))
        {
            _pendingToggle = (list, item);
            _toggleTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _toggleTimer.Stop();
            _toggleTimer.Tick -= OnToggleTimerTick;
            _toggleTimer.Tick += OnToggleTimerTick;
            _toggleTimer.Start();
        }
    }

    private void OnToggleTimerTick(object? sender, object e)
    {
        _toggleTimer?.Stop();
        if (_pendingToggle is not { } pending) return;
        _pendingToggle = null;
        var (list, item) = pending;
        if (list.SelectedItems.Count == 1 && list.SelectedItems.Contains(item))
            list.SelectedItems.Remove(item);
    }

    private static object? ItemFromSource(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ListViewItem lvi) return lvi.Content;
            source = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(source);
        }
        return null;
    }

    #endregion

    #region 代码驱动选中高亮

    // WinUI 的 VisualState Storyboard 对属性的 hold 值在批量取消选择（SelectedItems.Clear）时
    // 不保证被停止，导致高亮残留、且本地值无法覆盖动画值。因此选中着色层与指示条不走 VSM，
    // 由本 region 在 SelectionChanged / 容器生成（含回收再实例化）时直接设置属性。

    private static readonly SolidColorBrush SelectionOffBrush = new(Microsoft.UI.Colors.Transparent);

    private void HookSelectionVisuals(ListView list)
    {
        list.SelectionChanged += (_, _) => ApplySelectionVisuals(list);
        // WinUI3 的 ItemContainerGenerator 没有 ContainersChanged 事件，
        // 用 ListViewBase.ContainerContentChanging 覆盖容器生成/回收再绑定
        list.ContainerContentChanging += (_, _) =>
            list.DispatcherQueue.TryEnqueue(() => ApplySelectionVisuals(list));
    }

    /// <summary>主题切换后覆盖字典重建，选中刷子需重新求值再刷一遍。</summary>
    private void OnThemeChangedRefreshSelectionVisuals()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            ApplySelectionVisuals(TaskList);
            ApplySelectionVisuals(BtList);
        });
    }

    private void ApplySelectionVisuals(ListView list)
    {
        var onBrush = Application.Current.Resources["ListViewItemBackgroundSelected"] as Brush
                      ?? SelectionOffBrush;
        for (int i = 0; i < list.Items.Count; i++)
        {
            if (list.ContainerFromIndex(i) is not ListViewItem container) continue;
            ApplyCardSelection(container, list.SelectedItems.Contains(list.Items[i]), onBrush);
        }
    }

    private static void ApplyCardSelection(ListViewItem container, bool selected, Brush onBrush)
    {
        if (FindTemplatePart(container, "CardSelectTint") is Controls.SquircleBorder tint)
            tint.Fill = selected ? onBrush : SelectionOffBrush;
        if (FindTemplatePart(container, "SelectionIndicator") is Microsoft.UI.Xaml.Shapes.Rectangle indicator)
            indicator.Opacity = selected ? 1 : 0;
    }

    private static DependencyObject? FindTemplatePart(DependencyObject root, string name)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            // 不进入内容呈现子树，避免误命中数据模板内的同名元素
            if (child is ContentPresenter) continue;
            if (child is FrameworkElement fe && fe.Name == name) return child;
            var found = FindTemplatePart(child, name);
            if (found is not null) return found;
        }
        return null;
    }

    #endregion

    private async void OnBatchPauseClick(object sender, RoutedEventArgs e)
    {
        if (App.Host?.Engine == null) return;
        foreach (var vm in SelectedTasks().Where(v => v.CanPause).ToList())
        {
            vm.MarkPending("pause");
            try { await App.Host.Engine.PauseAsync(vm.Gid); }
            catch { vm.ClearPending(); }
        }
    }

    private async void OnBatchResumeClick(object sender, RoutedEventArgs e)
    {
        if (App.Host?.Engine == null) return;
        foreach (var vm in SelectedTasks().Where(v => v.CanResume).ToList())
        {
            vm.MarkPending("resume");
            try { await App.Host.Engine.ResumeAsync(vm.Gid); }
            catch { vm.ClearPending(); }
        }
    }

    private async void OnBatchDeleteClick(object sender, RoutedEventArgs e)
    {
        var list = SelectedTasks().ToList();
        if (list.Count == 0) return;
        try
        {
            var result = await ShowDialogAsync(() => new ContentDialog
            {
                Title = "批量删除",
                Content = $"确定要删除选中的 {list.Count} 个任务吗？",
                PrimaryButtonText = "删除任务和文件",
                SecondaryButtonText = "仅删除任务",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                PrimaryButtonStyle = (Style)RootGrid.Resources["DangerButtonStyle"],
                XamlRoot = RootGrid.XamlRoot
            });
            if (App.Host?.Engine == null || result == ContentDialogResult.None) return;
            var deleteFile = result == ContentDialogResult.Primary;
            foreach (var vm in list)
                await App.Host.Engine.RemoveAsync(vm.Gid, deleteFile);
            // 全部成功才清选择；中途失败保留选中，便于用户重试
            ActiveList.SelectedItems.Clear();
        }
        catch (Exception ex) { App.Log($"[ui] 批量删除失败: {ex.Message}"); }
    }

    private IEnumerable<TaskItemViewModel> SelectedTasks() =>
        ActiveList.SelectedItems.OfType<TaskItemViewModel>();

    #endregion

    #endregion

    private void ShowContextMenu(TaskItemViewModel vm, FrameworkElement anchor)
    {
        var menu = new MenuFlyout();
        menu.Items.Add(new MenuFlyoutItem { Text = "打开文件", Icon = new FontIcon { Glyph = "\uE8E5" } }
            .Tap(() => OpenFile(vm.Model)));
        menu.Items.Add(new MenuFlyoutItem { Text = "打开所在文件夹", Icon = new FontIcon { Glyph = "\uE8DA" } }
            .Tap(() => OpenFolder(vm.Model)));
        menu.Items.Add(new MenuFlyoutItem { Text = "复制下载链接", Icon = new FontIcon { Glyph = "\uE71B" } }
            .Tap(() => CopyUrl(vm.Model)));
        menu.Items.Add(new MenuFlyoutSeparator());
        if (vm.CanPause)
            menu.Items.Add(new MenuFlyoutItem { Text = "暂停", Icon = new FontIcon { Glyph = "\uE769" } }
                .Tap(async () =>
                {
                    if (App.Host?.Engine != null)
                    {
                        vm.MarkPending("pause");
                        try { await App.Host.Engine.PauseAsync(vm.Gid); }
                        catch { vm.ClearPending(); }
                    }
                }));
        if (vm.CanResume)
            menu.Items.Add(new MenuFlyoutItem { Text = "继续", Icon = new FontIcon { Glyph = "\uE768" } }
                .Tap(async () =>
                {
                    if (App.Host?.Engine != null)
                    {
                        vm.MarkPending("resume");
                        try { await App.Host.Engine.ResumeAsync(vm.Gid); }
                        catch { vm.ClearPending(); }
                    }
                }));
        menu.Items.Add(new MenuFlyoutItem { Text = "重新下载", Icon = new FontIcon { Glyph = "\uE895" } }
            .Tap(async () =>
            {
                if (App.Host?.Engine == null) return;
                try { await App.Host.Engine.RedownloadAsync(vm.Gid); }
                catch (DuplicateTaskException dex) { App.Log($"[ui] 重新下载被拦截: {dex.Message}"); }
                catch (Exception ex) { App.Log($"[ui] 重新下载失败: {ex.Message}"); }
            }));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(new MenuFlyoutItem { Text = "删除", Icon = new FontIcon { Glyph = "\uE74D" } }
            .Tap(async () => await ConfirmDeleteAsync(vm)));
        menu.ShowAt(anchor);
    }

    private static void OpenFile(DownloadTaskInfo t) => Shell.OpenFile(t.FilePath);

    private static void OpenFolder(DownloadTaskInfo t)
    {
        var dir = !string.IsNullOrEmpty(t.FilePath) ? Path.GetDirectoryName(t.FilePath) : t.Dir;
        Shell.OpenFolder(dir, t.FilePath);
    }

    private static void CopyUrl(DownloadTaskInfo t)
    {
        if (t.Urls.Count > 0)
        {
            var pkg = new DataPackage();
            pkg.SetText(t.Urls[0]);
            Clipboard.SetContent(pkg);
        }
    }

    private async Task ConfirmDeleteAsync(TaskItemViewModel vm)
    {
        var result = await ShowDialogAsync(() => new ContentDialog
        {
            Title = "删除任务",
            Content = $"确定要删除“{vm.Name}”吗？",
            PrimaryButtonText = "删除任务和文件",
            SecondaryButtonText = "仅删除任务",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            PrimaryButtonStyle = (Style)RootGrid.Resources["DangerButtonStyle"],
            XamlRoot = RootGrid.XamlRoot
        });
        if (result == ContentDialogResult.Primary && App.Host?.Engine != null)
            await App.Host.Engine.RemoveAsync(vm.Gid, deleteFile: true);
        else if (result == ContentDialogResult.Secondary && App.Host?.Engine != null)
            await App.Host.Engine.RemoveAsync(vm.Gid, deleteFile: false);
    }

    private async Task ShowNewDownloadDialog(string? initialUrl = null)
    {
        App.Log($"[ui] ShowNewDownloadDialog 打开 预填长度={initialUrl?.Length ?? 0}");
        await ShowDialogAsync(() => new NewDownloadDialog(App.Host!, initialUrl) { XamlRoot = RootGrid.XamlRoot });
        App.Log("[ui] ShowNewDownloadDialog 返回（对话框已关闭）");
    }

    /// <summary>处理外部唤起的磁力链接（magnet: 协议）：主窗口保持不动，直接弹独立确认窗口。</summary>
    public void HandleExternalMagnet(string url)
    {
        App.Log($"[magnet] 收到外部磁力链接: {(url.Length > 80 ? url[..80] + "…" : url)}");
        ShowMagnetConfirmWindow(url);
    }

    /// <summary>浏览器扩展 /api/download 转发的磁力链接（监听线程触发，需切回 UI 线程）。</summary>
    private void OnApiMagnetConfirm(string url) => ShowMagnetConfirmWindow(url);

    /// <summary>扩展送来的链接命中下载中的重复任务：主窗口弹窗提醒用户（任务已被自动跳过，不会重复添加）。</summary>
    private void OnApiDuplicateNotice(string names)
    {
        bool enqueued = DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                await ShowDialogAsync(() => new ContentDialog
                {
                    Title = "任务已在下载中",
                    Content = $"以下任务已在下载列表中，浏览器送来的链接已跳过：\n{names}",
                    CloseButtonText = "知道了",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = RootGrid.XamlRoot
                });
                App.Log("[api] 重复提醒弹窗已关闭");
            }
            catch (Exception ex)
            {
                App.Log($"[api] 重复任务提醒弹窗失败: hr=0x{ex.HResult:X8} {ex.Message}");
            }
        });
        if (!enqueued) App.Log("[api] 重复提醒投递 UI 线程失败");
    }

    /// <summary>弹出独立的磁力确认窗口（浏览器扩展 / 系统 magnet: 协议共用）。</summary>
    public void ShowMagnetConfirmWindow(string url)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                var win = new MagnetConfirmWindow(url);
                win.Activate();
                // 触发源常在后台（浏览器扩展），必须强制拉到前台，否则用户看不到
                WindowEffects.ForceForeground(win);
                App.Log("[magnet] 磁力确认窗口已弹出");
            }
            catch (Exception ex)
            {
                App.Log($"[magnet] 打开磁力确认窗口失败: {ex}");
            }
        });
    }

    private void ShowSettingsDialog()
    {
        new SettingsWindow(App.Host!).Activate();
    }
}

/// <summary>MenuFlyoutItem 链式绑定点击事件的小扩展。</summary>
internal static class MenuFlyoutExtensions
{
    public static MenuFlyoutItem Tap(this MenuFlyoutItem item, Action action)
    {
        item.Click += (_, _) => action();
        return item;
    }
    public static MenuFlyoutItem Tap(this MenuFlyoutItem item, Func<Task> action)
    {
        item.Click += async (_, _) => await action();
        return item;
    }
}
