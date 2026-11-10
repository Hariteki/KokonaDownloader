using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.Foundation;
using Windows.Graphics;
using WinRT.Interop;

namespace KokonaDownloader.App;

/// <summary>
/// 托盘右键菜单：纯原生 MenuFlyout。
/// 托盘没有可视的 XAML 根，而 Flyout 必须锚定在某个已显示窗口内的元素上，
/// 因此用一个 1×1 无边框窗口作宿主：弹出时把窗口挪到光标处并激活，
/// 再把菜单锚定在光标点上。
///
/// 宿主窗口一旦显示就**不再隐藏**（全透明 + 工具窗口 + 点击穿透），关闭菜单只把它挪回屏幕外。
/// 原因：隐藏后再在同一拍里 Show + Activate + ShowAt，前台还没生效，弹出层拿不到输入捕获，
/// 表现为"第一次点空白能关、第二次点空白关不掉"。窗口保持可见就没有这段状态切换。
/// </summary>
public sealed class TrayMenuHost
{
    private const int HostSize = 1;
    /// <summary>宿主窗口的"收起"位置（屏幕外）。不用 Hide，见类注释。</summary>
    private const int ParkedX = -32000;

    private Window? _host;
    private Grid? _anchor;
    private MenuFlyout? _menu;
    private (int X, int Y)? _pending;
    private long _showStartTicks;
    private nint _prevForeground;

    /// <summary>
    /// 启动后在空闲时预建宿主窗口与菜单的 XAML 资源。
    /// 不预热时，冷启动后第一次右键要现建 Window + XAML 树 + 弹出层，
    /// 实测 378ms，而之后每次只要 125ms——多出的 250ms 全砸在用户点下去的那一刻。
    /// 这里在屏幕外 Show 一次并**保持显示**：不激活、不抢焦点、不显示菜单。
    /// </summary>
    public void Warm()
    {
        try
        {
            EnsureHost();
            var host = _host!;
            host.AppWindow.Move(new PointInt32(ParkedX, ParkedX));
            host.AppWindow.Show();
        }
        catch (Exception ex) { App.Log($"[tray] 菜单预热失败: {ex.Message}"); }
    }

    /// <summary>在指定物理屏幕坐标处弹出托盘菜单。</summary>
    public void Show(int screenX, int screenY)
    {
        EnsureHost();
        var host = _host!;

        // 菜单已经开着时再右键：收起。判断依据是"屏幕上是否真的有菜单弹出层"，
        // 不用布尔标志——首帧失败/动画未结束时标志容易与实际状态脱节。
        if (RefreshMenuRect())
        {
            try { _menu!.Hide(); } catch { }
            return;
        }

        _showStartTicks = Stopwatch.GetTimestamp();
        _pending = (screenX, screenY);
        _prevForeground = GetForegroundWindow();
        InstallDismissHook();

        host.AppWindow.Move(new PointInt32(screenX, screenY));
        host.AppWindow.Show();
        host.Activate();
        ForceForeground(WindowNative.GetWindowHandle(host));

        // 关键：不要和 Show/Activate 同一拍弹。此时窗口还没真正成为前台，
        // 弹出层建立时拿不到输入捕获 → 外部点击不会触发 light-dismiss
        // （"第一次能点空白关闭、第二次点空白没反应"就是这么来的）。
        // 延后一拍，让激活先落地；弹出成功后再取菜单矩形。
        _anchor!.DispatcherQueue.TryEnqueue(() =>
        {
            if (!_anchor!.IsLoaded) return;   // 未 Loaded 时由 _anchor.Loaded 回调补弹
            ShowPendingMenu();
            RefreshMenuRect();
        });
    }

    /// <summary>
    /// 强制把窗口置为前台。
    ///
    /// 托盘点击的回调来自 Explorer，本进程并没有"最后输入事件"，于是系统前台锁会拒绝
    /// 普通的 SetForegroundWindow。窗口拿不到前台，WinUI 的 MenuFlyout 就不进入激活状态：
    /// 菜单虽然画出来了，但点空白不关闭、点菜单项也不响应——真机日志里前台一直停在控制台窗口。
    /// 标准解法是临时把本线程挂到当前前台线程的输入队列上，置前完成后立即解挂。
    /// </summary>
    private static void ForceForeground(nint hwnd)
    {
        if (hwnd == nint.Zero) return;
        uint currentThread = 0;
        uint foregroundThread = 0;
        bool attached = false;
        // 整段都包在 try 里：这里全是 P/Invoke，任何一处签名/库名写错都会抛
        // EntryPointNotFoundException。托盘菜单绝不能因此把整个应用带崩。
        try
        {
            var current = GetForegroundWindow();
            if (current == hwnd) return;

            currentThread = GetCurrentThreadId();
            foregroundThread = current == nint.Zero ? 0 : GetWindowThreadProcessId(current, out _);
            if (foregroundThread != 0 && foregroundThread != currentThread)
                attached = AttachThreadInput(currentThread, foregroundThread, true);
            SetForegroundWindow(hwnd);
            BringWindowToTop(hwnd);
        }
        catch (Exception ex) { App.Log($"[tray] 托盘菜单置前失败: {ex.Message}"); }
        finally
        {
            if (attached)
            {
                try { AttachThreadInput(currentThread, foregroundThread, false); } catch { }
            }
        }
    }

    /// <summary>菜单关闭：把宿主挪回屏幕外，并把前台焦点还给用户原先的窗口。</summary>
    private void OnMenuClosed()
    {
        _menuRect = null;
        UninstallDismissHook();
        try { _host?.AppWindow.Move(new PointInt32(ParkedX, ParkedX)); } catch { }

        // 菜单关闭后前台通常落回宿主窗口（1×1、全透明）。若不归还，
        // 紧接着敲键盘会敲到一个看不见的窗口上，用户会觉得"应用没反应"。
        var fg = GetForegroundWindow();
        if (fg != nint.Zero && IsWindow(_prevForeground) && _prevForeground != fg)
        {
            // 返回值是前台窗口所在线程 id，这里故意丢弃（失败即 0），但 CA1806 要求显式消费
            uint threadId = GetWindowThreadProcessId(fg, out uint pid);
            // 只在前台还属于本进程时归还：用户已经点了别的窗口就别去抢
            if (threadId != 0 && pid == (uint)Environment.ProcessId)
                SetForegroundWindow(_prevForeground);
        }
        _prevForeground = nint.Zero;
    }

    /// <summary>
    /// 菜单打开期间装低级鼠标/键盘钩子，接管"关闭菜单"的判定。
    ///
    /// 为什么不能只靠 Flyout 自带的 light-dismiss：托盘点击的回调来自 Explorer，
    /// 本进程拿不到前台权（当前台是管理员窗口时连 AttachThreadInput 都会被 UIPI 拒绝），
    /// 弹出层没有激活状态，点空白就关不掉——真机表现为"第一次能关、第二次关不掉"。
    /// 所以这里直接按坐标判定：落点不在菜单矩形内就关闭。
    /// </summary>
    private void InstallDismissHook()
    {
        if (_hook != nint.Zero) return;
        _hookOwner = this;
        _mouseProc ??= LowLevelMouseProc;
        _keyProc ??= LowLevelKeyboardProc;
        var module = GetModuleHandle(null);
        _hook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, module, 0);
        _keyHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyProc, module, 0);
        if (_hook == nint.Zero)
            App.Log($"[tray] 外部点击关闭钩子安装失败: err={Marshal.GetLastWin32Error()}");
        if (_keyHook == nint.Zero)
            App.Log($"[tray] Esc 关闭钩子安装失败: err={Marshal.GetLastWin32Error()}");
    }

    private void UninstallDismissHook()
    {
        if (_hook != nint.Zero) { UnhookWindowsHookEx(_hook); _hook = nint.Zero; }
        if (_keyHook != nint.Zero) { UnhookWindowsHookEx(_keyHook); _keyHook = nint.Zero; }
        if (ReferenceEquals(_hookOwner, this)) _hookOwner = null;
    }

    /// <summary>
    /// Esc 关闭。菜单弹出层在我们的宿主窗口里拿不到键盘焦点（前台是本进程的宿主窗口，
    /// 但焦点元素是空的），所以 Flyout 自带的 Esc 处理不会触发，只能在钩子里判定。
    /// </summary>
    private static nint LowLevelKeyboardProc(int nCode, nint wParam, nint lParam)
    {
        try
        {
            if (nCode >= 0 && (wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN))
            {
                var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                var owner = _hookOwner;
                if (owner != null && info.vkCode == VK_ESCAPE && owner.HasVisibleMenu())
                    owner._host?.DispatcherQueue.TryEnqueue(owner.HideMenuFromOutside);
            }
        }
        catch { }
        return CallNextHookEx(nint.Zero, nCode, wParam, lParam);
    }

    private static nint LowLevelMouseProc(int nCode, nint wParam, nint lParam)
    {
        try
        {
            if (nCode >= 0 && (wParam == WM_LBUTTONDOWN || wParam == WM_RBUTTONDOWN || wParam == WM_MBUTTONDOWN))
            {
                var owner = _hookOwner;
                if (owner != null && owner.HasVisibleMenu())
                {
                    var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    if (!owner.IsPointInsideMenu(info.pt))
                        owner._host?.DispatcherQueue.TryEnqueue(owner.HideMenuFromOutside);
                }
            }
        }
        catch { /* 钩子回调绝不能抛：抛出去会让系统摘掉钩子 */ }
        return CallNextHookEx(nint.Zero, nCode, wParam, lParam);
    }

    /// <summary>菜单当前是否真的显示在屏幕上（以弹出层窗口为准）。</summary>
    private bool HasVisibleMenu() => RefreshMenuRect();

    /// <summary>
    /// 判断点击是否落在菜单本体上。
    ///
    /// 不能用"窗口是否属于本进程"来判：WinUI 给 Transient 弹出层还会建一个铺满屏幕的
    /// 外部点击拦截层，同样属于本进程，那样会把"点空白"误判成"点菜单"。
    /// 菜单本体是那个尺寸正常的 PopupWindowSiteBridge 窗口，拦截层则占满整块屏幕，
    /// 因此取"可见、非全屏"的那一个作为菜单矩形。
    /// </summary>
    private bool IsPointInsideMenu(NATIVE_POINT pt)
    {
        if (_menuRect is not { } r) return false;
        return pt.X >= r.Left && pt.X < r.Right && pt.Y >= r.Top && pt.Y < r.Bottom;
    }

    /// <summary>重新定位菜单弹出层；返回是否真的找到一个可见的菜单。</summary>
    private bool RefreshMenuRect()
    {
        _menuRect = null;
        uint self = (uint)Environment.ProcessId;
        var screen = DisplayArea.Primary;
        double screenArea = (double)screen.OuterBounds.Width * screen.OuterBounds.Height;
        nint best = nint.Zero;
        long bestArea = long.MaxValue;

        EnumWindows((h, _) =>
        {
            uint threadId = GetWindowThreadProcessId(h, out uint pid);
            if (threadId == 0 || pid != self || !IsWindowVisible(h)) return true;
            var cls = new StringBuilder(128);
            if (GetClassNameW(h, cls, cls.Capacity) == 0) return true;
            if (!cls.ToString().Contains("PopupWindowSiteBridge", StringComparison.Ordinal)) return true;
            if (!GetWindowRect(h, out RECT rc)) return true;
            long area = (long)(rc.Right - rc.Left) * (rc.Bottom - rc.Top);
            if (area <= 0 || area >= screenArea * 0.8) return true;   // 全屏拦截层，跳过
            if (area < bestArea) { bestArea = area; best = h; }
            return true;
        }, nint.Zero);

        if (best != nint.Zero && GetWindowRect(best, out RECT rect))
        {
            _menuRect = rect;
            return true;
        }
        return false;
    }

    private void HideMenuFromOutside()
    {
        // 以"屏幕上是否还有菜单弹出层"为准，不依赖可能失步的布尔标志
        if (!RefreshMenuRect()) return;
        try { _menu?.Hide(); } catch (Exception ex) { App.Log($"[tray] 关闭菜单失败: {ex.Message}"); }
    }

    private const int WH_MOUSE_LL = 14;
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_MBUTTONDOWN = 0x0207;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const uint VK_ESCAPE = 0x1B;

    private nint _hook;
    private nint _keyHook;
    private RECT? _menuRect;
    /// <summary>静态保活：委托被 GC 回收后系统仍会回调，会直接崩进程。</summary>
    private static LowLevelMouseProc2? _mouseProc;
    private static LowLevelKeyboardProc2? _keyProc;
    private static TrayMenuHost? _hookOwner;

    private delegate nint LowLevelMouseProc2(int nCode, nint wParam, nint lParam);
    private delegate nint LowLevelKeyboardProc2(int nCode, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NATIVE_POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    private delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(nint hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public NATIVE_POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public nint dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int idHook, LowLevelMouseProc2 lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int idHook, LowLevelKeyboardProc2 lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    // GetCurrentThreadId 属于 kernel32，不是 user32。写错 DLL 会在真机运行时抛
    // EntryPointNotFoundException（声明存在、编译通过，只有真调用才炸）。
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(nint hWnd);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? lpModuleName);

    private void EnsureHost()
    {
        if (_host != null) return;

        _anchor = new Grid { RequestedTheme = ElementTheme.Dark };
        _anchor.Loaded += (_, _) => ShowPendingMenu();

        _host = new Window { Content = _anchor };
        var appWindow = _host.AppWindow;
        appWindow.IsShownInSwitchers = false;
        if (appWindow.Presenter is OverlappedPresenter p)
        {
            // 必须先去边框再缩尺寸：带边框窗口受系统最小窗口尺寸钳制，
            // 直接 Resize(1,1) 会得到约 132×38 的可见深色小块（菜单下方的"黑方框"）
            p.SetBorderAndTitleBar(false, false);
            p.IsAlwaysOnTop = true;
            p.IsResizable = false;
            p.IsMaximizable = false;
            p.IsMinimizable = false;
        }
        appWindow.Resize(new SizeInt32(HostSize, HostSize));
        MakeHostInvisible();

        _menu = BuildMenu();
        // 留一条耗时日志：右键手感是"玄学"投诉，没有数字就只能靠猜。
        // 正常应 <30ms（预热后）；若哪天又是几百毫秒，日志里能直接看到。
        _menu.Opened += (_, _) =>
        {
            // 矩形必须在"弹出层真的建好"之后再取：放在 Opened 里比 Show 之前更可靠，
            // 但仍做惰性兜底（见 IsPointInsideMenu）。这里再取一次覆盖位置调整。
            RefreshMenuRect();
            if (_showStartTicks == 0) return;
            double ms = Stopwatch.GetElapsedTime(_showStartTicks).TotalMilliseconds;
            _showStartTicks = 0;
            if (ms >= 80) App.Log($"[tray] 右键菜单弹出耗时 {ms:0}ms（偏慢，正常 <30ms）");
        };
        _menu.Closed += (_, _) => OnMenuClosed();
    }

    /// <summary>设为全透明分层窗口：系统仍会把 1×1 钳制成约 132×38 的最小窗口，
    /// 但 alpha=0 使其完全不可见且点击穿透；菜单弹窗是独立顶层 hwnd，不受影响。</summary>
    private void MakeHostInvisible()
    {
        var hwnd = WindowNative.GetWindowHandle(_host);
        if (hwnd == nint.Zero) return;
        var before = GetWindowLong(hwnd, GWL_EXSTYLE);
        var prev = SetWindowLong(hwnd, GWL_EXSTYLE, before | WS_EX_LAYERED | WS_EX_TOOLWINDOW);
        // SetWindowLong 的返回值是旧样式（0 也可能合法），因此不靠返回值判成败，而是回读校验：
        // 样式没生效就意味着 1×1 钳制出的深色小块会露出来（历史 bug"菜单下方黑方框"），必须留痕。
        const int want = WS_EX_LAYERED | WS_EX_TOOLWINDOW;
        var after = GetWindowLong(hwnd, GWL_EXSTYLE);
        if ((after & want) != want)
            App.Log($"[tray] 托盘宿主隐藏样式未生效：before=0x{before:X} SetWindowLong 返回=0x{prev:X} after=0x{after:X}，可能出现黑方框");
        if (!SetLayeredWindowAttributes(hwnd, 0, 0, LWA_ALPHA))
            App.Log($"[tray] 托盘宿主透明属性设置失败：after=0x{after:X}，可能出现黑方框");
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const uint LWA_ALPHA = 0x2;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(nint hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(nint hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(nint hWnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint hWnd);

    private MenuFlyout BuildMenu()
    {
        var menu = new MenuFlyout();
        menu.Items.Add(NativeItem("显示主界面", "\uE8A7", () => App.ShowMainWindow()));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(NativeItem("全部暂停", "\uE769", () => RunLogged("全部暂停", () => App.Host?.Engine?.PauseAllAsync())));
        menu.Items.Add(NativeItem("全部继续", "\uE768", () => RunLogged("全部继续", () => App.Host?.Engine?.ResumeAllAsync())));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(NativeItem("退出海兔下载器", "\uE7E8", () => App.ExitApp()));
        return menu;
    }

    /// <summary>托盘菜单里的异步动作：原先写成 <c>_ = PauseAllAsync()</c> 属"射后不管"，
    /// 失败时异常既不进 UnhandledException 也不落日志（第四轮 N-8）。改为就地 await 并记日志。</summary>
    private static async void RunLogged(string what, Func<Task?> start)
    {
        try
        {
            var pending = start();
            if (pending != null) await pending;
        }
        catch (Exception ex)
        {
            App.Log($"[tray] 菜单动作「{what}」失败: {ex.Message}");
        }
    }

    private static MenuFlyoutItem NativeItem(string text, string glyph, Action action)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
        // 同步异常此前依赖全局 UnhandledException 兜底；这里就地记录，日志里能直接看到是哪个菜单项
        item.Click += (_, _) =>
        {
            try { action(); }
            catch (Exception ex) { App.Log($"[tray] 菜单项「{text}」执行失败: {ex.Message}"); }
        };
        return item;
    }

    private void ShowPendingMenu()
    {
        if (_pending == null || _menu == null || _anchor == null || !_anchor.IsLoaded) return;
        var (x, y) = _pending.Value;
        _pending = null;

        // 手动决定弹出方向：光标在屏幕下半部（托盘通常在底部）时向上弹，否则向下；
        // 靠近屏幕右缘时改为右对齐，避免菜单溢出屏幕
        var area = DisplayArea.GetFromPoint(new PointInt32(x, y), DisplayAreaFallback.Nearest);
        var belowCursor = y < area.WorkArea.Y + area.WorkArea.Height / 2;
        var nearRight = x > area.WorkArea.X + area.WorkArea.Width - 260;

        var placement = (belowCursor, nearRight) switch
        {
            (true, true) => FlyoutPlacementMode.TopEdgeAlignedRight,
            (true, false) => FlyoutPlacementMode.TopEdgeAlignedLeft,
            (false, true) => FlyoutPlacementMode.BottomEdgeAlignedRight,
            (false, false) => FlyoutPlacementMode.BottomEdgeAlignedLeft
        };

        try
        {
            _menu.ShowAt(_anchor, new FlyoutShowOptions
            {
                Placement = placement,
                Position = new Point(0, 0),
                ShowMode = FlyoutShowMode.Transient
            });
        }
        catch (Exception ex)
        {
            App.Log($"[tray][diag] ShowAt 失败: {ex}");
        }
    }
}
