using System.Runtime.InteropServices;
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
/// 再把菜单锚定在光标点上；菜单关闭后隐藏宿主窗口。
/// </summary>
public sealed class TrayMenuHost
{
    private const int HostSize = 1;

    private Window? _host;
    private Grid? _anchor;
    private MenuFlyout? _menu;
    private (int X, int Y)? _pending;

    /// <summary>在指定物理屏幕坐标处弹出托盘菜单。</summary>
    public void Show(int screenX, int screenY)
    {
        EnsureHost();
        var host = _host!;
        _pending = (screenX, screenY);

        host.AppWindow.Move(new PointInt32(screenX, screenY));
        host.AppWindow.Show();
        host.Activate();

        // 宿主首次显示时内容尚未 Loaded（XamlRoot 不可用），等 Loaded 回调补弹；已加载则立即弹
        if (_anchor!.IsLoaded) ShowPendingMenu();
    }

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
        _menu.Closed += (_, _) => _host?.AppWindow.Hide();
    }

    /// <summary>设为全透明分层窗口：系统仍会把 1×1 钳制成约 132×38 的最小窗口，
    /// 但 alpha=0 使其完全不可见且点击穿透；菜单弹窗是独立顶层 hwnd，不受影响。</summary>
    private void MakeHostInvisible()
    {
        var hwnd = WindowNative.GetWindowHandle(_host);
        if (hwnd == nint.Zero) return;
        SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_LAYERED | WS_EX_TOOLWINDOW);
        SetLayeredWindowAttributes(hwnd, 0, 0, LWA_ALPHA);
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

    private MenuFlyout BuildMenu()
    {
        var menu = new MenuFlyout();
        menu.Items.Add(NativeItem("显示主界面", "\uE8A7", () => App.ShowMainWindow()));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(NativeItem("全部暂停", "\uE769", () => _ = App.Host?.Engine?.PauseAllAsync()));
        menu.Items.Add(NativeItem("全部继续", "\uE768", () => _ = App.Host?.Engine?.ResumeAllAsync()));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(NativeItem("退出海兔下载器", "\uE7E8", () => App.ExitApp()));
        return menu;
    }

    private static MenuFlyoutItem NativeItem(string text, string glyph, Action action)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
        item.Click += (_, _) => action();
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

        _menu.ShowAt(_anchor, new FlyoutShowOptions
        {
            Placement = placement,
            Position = new Point(0, 0),
            ShowMode = FlyoutShowMode.Transient
        });
    }
}
