using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using KokonaDownloader.App.Themes;
using KokonaDownloader.App.ViewModels;
using KokonaDownloader.Core;
using KokonaDownloader.Core.Engine;
using KokonaDownloader.Core.Notifications;
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

public partial class MainWindow : Window
{
    private readonly ObservableCollection<TaskItemViewModel> _tasks = new();
    private readonly Dictionary<string, TaskItemViewModel> _taskMap = new();
    private readonly DispatcherTimer _timer = new();
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _showEventWait;
    private string _filter = "all";
    private string _search = string.Empty;
    private bool _exiting;
    /// <summary>每个任务对应的进度小窗（IDM 式），任务结束后保留引用以便关闭。</summary>
    private readonly Dictionary<string, ProgressWindow> _progressWindows = new();

    /// <summary>悬停投影只在首帧模板实例化后挂载一次。</summary>
    private bool _shadowsAttached;

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
        // 原生 Mica 磨砂背景（声明式 SystemBackdrop，生命周期与激活状态由框架托管，最可靠）
        try { SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt }; }
        catch (Exception ex) { App.Log($"应用 Mica 失败: {ex.Message}"); }

        _timer.Interval = TimeSpan.FromMilliseconds(900);
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();

        ApplyTheme();
        // 主题服务：资源覆盖 + 原生标题栏着色（内部已订阅设置变更与系统强调色变化）
        ThemeService.Register(this);
        // 窗口激活后再应用一次，确保标题栏颜色在首帧之后生效
        Activated += (_, _) => ApplyTheme();
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
            _showEventWait?.Unregister(null);
            _showEvent?.Dispose();
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
                try
                {
                    var win = new ProgressWindow(t.Gid, t.Name, t.IsBt);
                    win.Closed += (_, _) => _progressWindows.Remove(t.Gid);
                    _progressWindows[t.Gid] = win;
                    SizeAndPlaceProgressWindow(win);
                    win.Activate();
                    // 下载可能由扩展在后台触发，必须强制把小窗拉到前台，否则用户看不到
                    WindowEffects.ForceForeground(win);
                    App.Log($"[ui] 进度小窗已创建 gid={t.Gid} title={t.Name}");
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

    /// <summary>把进度小窗放到主显示器屏幕正中央，尺寸固定为紧凑卡片。</summary>
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
            win.AppWindow.Move(new Windows.Graphics.PointInt32(Math.Max(0, x), Math.Max(0, y)));
        }
        catch { }
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

    private async Task RefreshAsync()
    {
        if (App.Host?.Engine == null || !App.Host.Engine.IsRunning)
        {
            ConnStatusText.Text = "引擎未运行";
            return;
        }
        try
        {
            var tasks = await App.Host.Engine.GetAllTasksAsync();
            var stat = await App.Host.Engine.GetGlobalStatAsync();

            DispatcherQueue.TryEnqueue(() =>
            {
                GlobalSpeedText.Text = $"{TaskItemViewModel.FormatSpeed(stat.DownloadSpeed)}/s";
                ActiveCountText.Text = $"活动 {stat.NumActive} · 等待 {stat.NumWaiting}";
                ConnStatusText.Text = "引擎运行中";
                MergeTasks(tasks);
            });
        }
        catch (Exception ex)
        {
            DispatcherQueue.TryEnqueue(() => ConnStatusText.Text = $"引擎异常: {ex.Message}");
        }
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
        UpdateBtSummary();
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
        await ShowNewDownloadDialog();
    }

    private async void OnPauseAllClick(object sender, RoutedEventArgs e)
    {
        if (App.Host?.Engine != null) await App.Host.Engine.PauseAllAsync();
    }

    private async void OnResumeAllClick(object sender, RoutedEventArgs e)
    {
        if (App.Host?.Engine != null) await App.Host.Engine.ResumeAllAsync();
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
        if (ItemFromButton(sender) is { } vm) await ConfirmDeleteAsync(vm);
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
        var dlg = new ContentDialog
        {
            Title = "批量删除",
            Content = $"确定要删除选中的 {list.Count} 个任务吗？",
            PrimaryButtonText = "删除任务和文件",
            SecondaryButtonText = "仅删除任务",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            PrimaryButtonStyle = (Style)RootGrid.Resources["DangerButtonStyle"],
            XamlRoot = RootGrid.XamlRoot
        };
        var result = await dlg.ShowAsync();
        if (App.Host?.Engine == null || result == ContentDialogResult.None) return;
        var deleteFile = result == ContentDialogResult.Primary;
        foreach (var vm in list)
            await App.Host.Engine.RemoveAsync(vm.Gid, deleteFile);
        ActiveList.SelectedItems.Clear();
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
        var dlg = new ContentDialog
        {
            Title = "删除任务",
            Content = $"确定要删除“{vm.Name}”吗？",
            PrimaryButtonText = "删除任务和文件",
            SecondaryButtonText = "仅删除任务",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            PrimaryButtonStyle = (Style)RootGrid.Resources["DangerButtonStyle"],
            XamlRoot = RootGrid.XamlRoot
        };
        var result = await dlg.ShowAsync();
        if (result == ContentDialogResult.Primary && App.Host?.Engine != null)
            await App.Host.Engine.RemoveAsync(vm.Gid, deleteFile: true);
        else if (result == ContentDialogResult.Secondary && App.Host?.Engine != null)
            await App.Host.Engine.RemoveAsync(vm.Gid, deleteFile: false);
    }

    private async Task ShowNewDownloadDialog(string? initialUrl = null)
    {
        App.Log($"[ui] ShowNewDownloadDialog 打开 预填长度={initialUrl?.Length ?? 0}");
        var dlg = new NewDownloadDialog(App.Host!, initialUrl) { XamlRoot = RootGrid.XamlRoot };
        await dlg.ShowAsync();
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
        DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                var dlg = new ContentDialog
                {
                    Title = "任务已在下载中",
                    Content = $"以下任务已在下载列表中，浏览器送来的链接已跳过：\n{names}",
                    CloseButtonText = "知道了",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = RootGrid.XamlRoot
                };
                await dlg.ShowAsync();
            }
            catch (Exception ex)
            {
                App.Log($"[api] 重复任务提醒弹窗失败: {ex.Message}");
            }
        });
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
