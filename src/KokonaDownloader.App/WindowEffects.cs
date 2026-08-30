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

    /// <summary>切换标题栏深浅色模式。注意必须传真实 HWND（WindowId.Value 不是 HWND，曾导致静默失效）。</summary>
    public static void SetDarkTitleBar(Window window, bool dark)
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            if (hwnd == IntPtr.Zero) return;
            var v = dark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref v, sizeof(int));
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
            DwmSetWindowAttribute(hwnd, attribute, ref colorref, sizeof(int));
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

        // 主题跟随内容
        if (window.Content is FrameworkElement root)
        {
            config.Theme = ToBackdropTheme(root.ActualTheme);
            root.ActualThemeChanged += (_, _) =>
                config.Theme = ToBackdropTheme(root.ActualTheme);
        }

        // 窗口激活/失活状态
        config.IsInputActive = true;
        window.Activated += (_, e) => config.IsInputActive = e.WindowActivationState != WindowActivationState.Deactivated;
        window.Closed += (_, _) => controller.Dispose();
    }

    private static SystemBackdropTheme ToBackdropTheme(ElementTheme theme) => theme switch
    {
        ElementTheme.Dark => SystemBackdropTheme.Dark,
        ElementTheme.Light => SystemBackdropTheme.Light,
        _ => SystemBackdropTheme.Default
    };
}
