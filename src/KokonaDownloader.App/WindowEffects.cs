using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using WinRT;

namespace KokonaDownloader.App;

/// <summary>
/// 窗口原生效果辅助：
///  1. 应用 Windows 11 Mica / Acrylic 磨砂半透明背景（原生 SystemBackdrop 控制器）；
///  2. 通过 Win32 子类化拦截 WM_GETMINMAXINFO 强制窗口最小尺寸，防止元素裁剪重叠；
///  3. 切换标题栏深浅色（DWM 原生属性）。
/// </summary>
public static class WindowEffects
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_TEXT_COLOR = 36;
    private const uint WM_GETMINMAXINFO = 0x0024;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, IntPtr processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    /// <summary>
    /// 「这个窗口现在真的看得见吗」——两个信号任一为真即算可见：
    /// WinUI 自己的 <c>AppWindow.IsVisible</c> 标记，以及 Win32 对该 HWND 的实际判断。
    ///
    /// 为什么要两个信号：外部（脚本、辅助工具、任务管理器之外的自动化）直接对 HWND 调
    /// <c>ShowWindow</c> 时，WinUI 的标记仍停在 <c>false</c>（它只认自己的 Hide/Show）。
    /// 只看标记做判断，会出现"窗口明明在屏幕上、界面却再也不刷新"的僵局
    /// ——刷新定时器的隐藏态停表（见 MainWindow.RefreshAsync）正是一处这样的判断，故这里以实际窗口状态兜底。
    /// </summary>
    public static bool WindowVisibleByAnySignal(Window window)
    {
        try { if (window.AppWindow.IsVisible) return true; } catch { /* 窗口已销毁等 */ }
        try { return IsWindowVisible(WinRT.Interop.WindowNative.GetWindowHandle(window)); } catch { return false; }
    }

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(IntPtr hwnd, SUBCLASSPROC pfnSubclass, UIntPtr uIdSubclass, UIntPtr dwRefData);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hwnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    private delegate IntPtr SUBCLASSPROC(IntPtr hwnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, UIntPtr dwRefData);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    /// <summary>hwnd → (最小宽高逻辑像素, 子类化委托)。委托必须保活，防止被 GC 回收后崩溃。</summary>
    private static readonly Dictionary<IntPtr, (double MinW, double MinH, SUBCLASSPROC Proc)> MinSizeMap = new();

    /// <summary>
    /// 设置窗口最小宽高（逻辑像素，内部按窗口 DPI 换算为物理像素）。
    /// 通过子类化拦截 WM_GETMINMAXINFO 实现，用户无法把窗口拖到更小。
    /// </summary>
    public static void SetMinSize(Window window, double minWidth, double minHeight)
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            if (hwnd == IntPtr.Zero || MinSizeMap.ContainsKey(hwnd)) return;

            SUBCLASSPROC proc = MinMaxSubclassProc;
            if (!SetWindowSubclass(hwnd, proc, UIntPtr.Zero, UIntPtr.Zero))
            {
                App.Log("设置最小尺寸失败：SetWindowSubclass 返回 false");
                return;
            }
            MinSizeMap[hwnd] = (minWidth, minHeight, proc);
        }
        catch (Exception ex) { App.Log($"设置最小尺寸失败: {ex.Message}"); }
    }

    private static IntPtr MinMaxSubclassProc(IntPtr hwnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, UIntPtr dwRefData)
    {
        if (uMsg == WM_GETMINMAXINFO && MinSizeMap.TryGetValue(hwnd, out var info))
        {
            var dpi = GetDpiForWindow(hwnd);
            var scale = dpi > 0 ? dpi / 96.0 : 1.0;
            var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            mmi.ptMinTrackSize.X = (int)(info.MinW * scale);
            mmi.ptMinTrackSize.Y = (int)(info.MinH * scale);
            Marshal.StructureToPtr(mmi, lParam, false);
        }
        return DefSubclassProc(hwnd, uMsg, wParam, lParam);
    }

    /// <summary>DWM 属性设置失败的"只记一次"开关：Win10 早期版本本就不支持标题栏着色属性，
    /// 每次主题切换都会失败，逐条记录会刷屏；但完全忽略会让真正的失效（如 HWND 传错）无人发现。</summary>
    private static int _dwmAttrFailureLogged;

    private static void NoteDwmAttrResult(int hr, string what)
    {
        if (hr == 0) return; // S_OK
        if (Interlocked.Exchange(ref _dwmAttrFailureLogged, 1) == 0)
            App.Log($"[effects] {what} 设置失败（HRESULT 0x{hr:X8}），后续同类失败不再记录");
    }

    /// <summary>切换标题栏深浅色模式。注意必须传真实 HWND（WindowId.Value 不是 HWND，曾导致静默失效）。</summary>
    public static void SetDarkTitleBar(Window window, bool dark)
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            if (hwnd == IntPtr.Zero) return;
            var v = dark ? 1 : 0;
            NoteDwmAttrResult(DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref v, sizeof(int)), "标题栏深浅色");
        }
        catch { }
    }

    /// <summary>标题栏底色着色（DWM 原生，Win11 22000+；旧系统静默忽略）。</summary>
    public static void SetCaptionColor(Window window, Windows.UI.Color color) =>
        SetDwmColorAttribute(window, DWMWA_CAPTION_COLOR, color);

    /// <summary>标题栏文字/按钮着色（DWM 原生，Win11 22000+；旧系统静默忽略）。</summary>
    public static void SetCaptionTextColor(Window window, Windows.UI.Color color) =>
        SetDwmColorAttribute(window, DWMWA_TEXT_COLOR, color);

    private static void SetDwmColorAttribute(Window window, int attribute, Windows.UI.Color color)
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            if (hwnd == IntPtr.Zero) return;
            // COLORREF 为 0x00BBGGRR（红在低位）
            var colorref = (int)(color.R | (color.G << 8) | (color.B << 16));
            NoteDwmAttrResult(DwmSetWindowAttribute(hwnd, attribute, ref colorref, sizeof(int)), $"DWM 颜色属性 {attribute}");
        }
        catch { }
    }

    /// <summary>
    /// 强制把窗口置于前台并获得焦点。
    /// 后台进程直接 SetForegroundWindow 会被系统拒绝（前台锁定限制），
    /// 这里先把本线程输入附加到前台窗口线程，绕过限制后再置顶、激活。
    /// </summary>
    public static void ForceForeground(Window window)
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            if (hwnd == IntPtr.Zero) return;

            var fore = GetForegroundWindow();
            if (fore != hwnd && fore != IntPtr.Zero)
            {
                var foreThread = GetWindowThreadProcessId(fore, IntPtr.Zero);
                var curThread = GetCurrentThreadId();
                var attached = false;
                if (foreThread != curThread)
                    attached = AttachThreadInput(curThread, foreThread, true);
                try
                {
                    ShowWindow(hwnd, 5 /* SW_SHOW */);
                    BringWindowToTop(hwnd);
                    SetForegroundWindow(hwnd);
                }
                finally
                {
                    if (attached) AttachThreadInput(curThread, foreThread, false);
                }
            }
            else
            {
                window.Activate();
            }
        }
        catch (Exception ex) { App.Log($"窗口置顶失败: {ex.Message}"); }
    }

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    /// <summary>
    /// 显示 + 还原 + 置顶，并**核对结果**（第八轮 P3-7：从托盘/第二个实例唤醒时偶发"进程活着但窗口不出来"）。
    ///
    /// 与 <see cref="ForceForeground"/> 的关键差异：那里只用 SW_SHOW，而 SW_SHOW 对
    /// **已最小化**的窗口是"保持当前（最小化）状态显示"——窗口依旧缩在任务栏角上。
    /// 唤醒路径必须先 SW_RESTORE 还原；改完再实测一次窗口是否真的可见且未最小化，
    /// 不行就重试一次，仍不行就留下日志，不再静默失败。
    /// </summary>
    /// <returns>最终窗口是否可见且未最小化。</returns>
    public static bool RevealAndActivate(Window window)
    {
        IntPtr hwnd;
        try { hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window); }
        catch (Exception ex) { App.Log($"唤醒主窗口失败：取不到窗口句柄（{ex.Message}）"); return false; }
        if (hwnd == IntPtr.Zero)
        {
            App.Log("唤醒主窗口失败：窗口句柄为 0（窗口可能已销毁）");
            return false;
        }

        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                // 9 = SW_RESTORE（还原并显示），5 = SW_SHOW（显示但保持当前状态）
                ShowWindow(hwnd, IsIconic(hwnd) ? 9 : 5);
                if (!IsWindowVisible(hwnd)) ShowWindow(hwnd, 9); // 仍不可见说明处于隐藏态，再强推一次
                BringWindowToTop(hwnd);
                SetForegroundWindow(hwnd);
            }
            catch (Exception ex) { App.Log($"唤醒主窗口异常: {ex.Message}"); }
            if (IsWindowVisible(hwnd) && !IsIconic(hwnd)) return true;
        }
        App.Log("唤醒主窗口后窗口仍不可见或被最小化（系统前台锁定/窗口已销毁），请在托盘或任务栏手动打开");
        return false;
    }

    /// <summary>
    /// 为窗口应用 Mica 磨砂背景（原生）。不支持时返回 false。
    /// </summary>
    public static bool TryApplyMica(Window window, bool useAlt = false)
    {
        try
        {
            if (!MicaController.IsSupported()) return false;

            var controller = new MicaController();
            var config = new SystemBackdropConfiguration();
            ConfigureForWindow(controller, window, config, useAlt ? MicaKind.BaseAlt : MicaKind.Base);
            return true;
        }
        catch (Exception ex) { App.Log($"应用 Mica 失败: {ex.Message}"); return false; }
    }

    /// <summary>
    /// 为窗口应用 Acrylic（亚克力）磨砂背景（原生）。不支持时返回 false。
    /// </summary>
    public static bool TryApplyAcrylic(Window window)
    {
        try
        {
            if (!DesktopAcrylicController.IsSupported()) return false;

            var controller = new DesktopAcrylicController();
            var config = new SystemBackdropConfiguration();
            ConfigureForWindow(controller, window, config, null);
            return true;
        }
        catch (Exception ex) { App.Log($"应用 Acrylic 失败: {ex.Message}"); return false; }
    }

    /// <summary>
    /// 可定制色调的 Acrylic 磨砂背景：主窗口磨砂背景（唯一主题）用。
    /// tintOpacity 越大主题色越浓（背景越不明显），luminosityOpacity 控制明度层。
    /// 返回实际生效的控制器（供重应用时释放），失败返回 null。
    /// </summary>
    public static ISystemBackdropControllerWithTargets? TryApplyAcrylicTinted(
        Window window,
        Windows.UI.Color tintColor,
        double tintOpacity,
        double luminosityOpacity,
        bool thin)
    {
        try
        {
            if (!DesktopAcrylicController.IsSupported())
            {
                App.Log("当前系统不支持 Acrylic 背景");
                return null;
            }

            var controller = new DesktopAcrylicController
            {
                Kind = thin ? DesktopAcrylicKind.Thin : DesktopAcrylicKind.Base,
                TintColor = tintColor,
                TintOpacity = (float)Math.Clamp(tintOpacity, 0.0, 1.0),
                LuminosityOpacity = (float)Math.Clamp(luminosityOpacity, 0.0, 1.0)
            };
            var config = new SystemBackdropConfiguration();
            ConfigureForWindow(controller, window, config, null);
            return controller;
        }
        catch (Exception ex) { App.Log($"应用半透明 Acrylic 失败: {ex.Message}"); return null; }
    }

    /// <summary>
    /// 复用窗口上已挂载的染色 Acrylic 控制器，直接更新染色参数（主题色切换时用）。
    /// 不销毁重建：销毁后到新控制器上屏前的间隙会露出窗口黑底（XAML 根背景为透明），
    /// 新控制器上屏时还带淡入动画，两者叠加表现为切换主题色时闪黑一下。
    /// 返回 false 表示该窗口当前没有可复用的 DesktopAcrylic 接线，
    /// 调用方应走完整挂载流程（<see cref="TryApplyAcrylicTinted"/>）。
    /// </summary>
    public static bool TryUpdateAcrylicTint(
        Window window,
        Windows.UI.Color tintColor,
        double tintOpacity,
        double luminosityOpacity,
        bool thin)
    {
        try
        {
            if (!BackdropWirings.TryGetValue(window, out var wiring)) return false;
            if (wiring.Controller is not DesktopAcrylicController acrylic) return false;
            acrylic.Kind = thin ? DesktopAcrylicKind.Thin : DesktopAcrylicKind.Base;
            acrylic.TintColor = tintColor;
            acrylic.TintOpacity = (float)Math.Clamp(tintOpacity, 0.0, 1.0);
            acrylic.LuminosityOpacity = (float)Math.Clamp(luminosityOpacity, 0.0, 1.0);
            return true;
        }
        catch (Exception ex) { App.Log($"更新磨砂染色失败: {ex.Message}"); return false; }
    }

    /// <summary>窗口当前的后台背景接线：控制器、配置与该窗口上的三个事件处理器。
    /// 保存处理器引用是必需的——重新应用主题时会再次进入
    /// <see cref="ConfigureForWindow"/>，不退订旧处理器就会**每次 Apply 都往同一个窗口上再挂 3 个**
    /// （长会话 + 频繁切换主题时无限累积，并让已释放的控制器无法被回收）。</summary>
    private sealed class BackdropWiring
    {
        public required SystemBackdropConfiguration Config { get; init; }
        public required ISystemBackdropControllerWithTargets Controller { get; init; }
        public Windows.Foundation.TypedEventHandler<FrameworkElement, object>? ThemeChanged { get; init; }
        public Windows.Foundation.TypedEventHandler<object, WindowActivatedEventArgs>? Activated { get; init; }
        public Windows.Foundation.TypedEventHandler<object, WindowEventArgs>? Closed { get; init; }
    }

    private static readonly Dictionary<Window, BackdropWiring> BackdropWirings = new();

    /// <summary>退订该窗口上一次的背景接线（幂等）。释放控制器时也应调用。</summary>
    public static void DetachBackdropWiring(Window window)
    {
        if (!BackdropWirings.TryGetValue(window, out var wiring)) return;
        BackdropWirings.Remove(window);
        try
        {
            if (wiring.ThemeChanged != null && window.Content is FrameworkElement root)
                root.ActualThemeChanged -= wiring.ThemeChanged;
        }
        catch { }
        try { if (wiring.Activated != null) window.Activated -= wiring.Activated; } catch { }
        try { if (wiring.Closed != null) window.Closed -= wiring.Closed; } catch { }
    }

    private static void ConfigureForWindow(
        ISystemBackdropControllerWithTargets controller,
        Window window,
        SystemBackdropConfiguration config,
        MicaKind? kind)
    {
        // 关联窗口：目标 + 配置
        controller.AddSystemBackdropTarget(window.As<ICompositionSupportsSystemBackdrop>());
        controller.SetSystemBackdropConfiguration(config);

        if (kind.HasValue && controller is MicaController mica)
            mica.Kind = kind.Value;

        // 先退订上一次的接线，再挂新的（见 BackdropWiring 注释）
        DetachBackdropWiring(window);

        // 主题跟随内容
        Windows.Foundation.TypedEventHandler<FrameworkElement, object>? themeChanged = null;
        if (window.Content is FrameworkElement root)
        {
            config.Theme = ToBackdropTheme(root.ActualTheme);
            themeChanged = (_, _) => config.Theme = ToBackdropTheme(root.ActualTheme);
            root.ActualThemeChanged += themeChanged;
        }

        // 窗口激活/失活状态
        config.IsInputActive = true;
        var activated = new Windows.Foundation.TypedEventHandler<object, WindowActivatedEventArgs>(
            (_, e) => config.IsInputActive = e.WindowActivationState != WindowActivationState.Deactivated);
        window.Activated += activated;

        var closed = new Windows.Foundation.TypedEventHandler<object, WindowEventArgs>((_, _) =>
        {
            DetachBackdropWiring(window);
            controller.Dispose();
        });
        window.Closed += closed;

        BackdropWirings[window] = new BackdropWiring
        {
            Config = config,
            Controller = controller,
            ThemeChanged = themeChanged,
            Activated = activated,
            Closed = closed
        };
    }

    private static SystemBackdropTheme ToBackdropTheme(ElementTheme theme) => theme switch
    {
        ElementTheme.Dark => SystemBackdropTheme.Dark,
        ElementTheme.Light => SystemBackdropTheme.Light,
        _ => SystemBackdropTheme.Default
    };
}
