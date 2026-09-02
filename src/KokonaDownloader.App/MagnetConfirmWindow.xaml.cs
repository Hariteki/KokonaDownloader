using System.Runtime.InteropServices;
using KokonaDownloader.App.Themes;
using KokonaDownloader.Core.Engine;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;

namespace KokonaDownloader.App;

/// <summary>
/// 磁力链接确认窗口：浏览器扩展 / 系统 magnet: 协议唤起时独立弹出，
/// 由用户确认后才创建下载任务。使用系统标准标题栏（非沉浸式），
/// 配色与控件样式经 ThemeService 与主程序保持一致。
/// </summary>
public sealed partial class MagnetConfirmWindow : Window
{
    /// <summary>当前打开的确认窗实例：多个磁力链接依次弹出时按序错开位置。</summary>
    private static readonly List<MagnetConfirmWindow> OpenWindows = new();

    private readonly string _magnetUrl;
    private bool _busy;
    private bool _initialFocusSet;
    private bool _closed;

    public MagnetConfirmWindow(string magnetUrl)
    {
        _magnetUrl = magnetUrl;
        InitializeComponent();

        UrlBox.Text = magnetUrl;
        NameText.Text = ExtractDisplayName(magnetUrl);
        DirText.Text = App.Host?.Settings.Current.DefaultDownloadDir ?? string.Empty;

        // 注册主题服务：原生标题栏着色 + 主题切换时全窗口刷新（与主窗口/设置窗口一致）
        ThemeService.Register(this);
        Closed += (_, _) =>
        {
            _closed = true;
            ThemeService.Unregister(this);
            OpenWindows.Remove(this);
        };
        OpenWindows.Add(this);

        // 确认小窗：固定尺寸，不可调整大小、不可最大化，保留标准系统标题栏
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
        }

        // 窗口/任务栏图标与主程序一致
        try { AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "icons", "tray.ico")); }
        catch { }

        SizeAndPlace();

        // 初始焦点：首次激活后稍作等待再设——ShowMagnetConfirmWindow 会紧接着调
        // ForceForeground（AttachThreadInput 前台切换），过早设置会被其重置；
        // 只在首次激活执行一次，避免之后 Alt+Tab 回窗时抢走用户手动设置的焦点
        Activated += OnActivatedForInitialFocus;
    }

    /// <summary>固定尺寸 + 居中：优先置于鼠标所在显示器（浏览器点击发生的屏幕）。</summary>
    private void SizeAndPlace()
    {
        try
        {
            // AppWindow.Position/Size 为物理像素，按目标显示器 DPI 换算
            var scale = GetDpiScale();
            var w = (int)(500 * scale);
            var h = (int)(430 * scale);
            AppWindow.Resize(new SizeInt32(w, h));

            // 先把窗口挪到鼠标坐标附近，"最近显示器"即变为鼠标所在屏幕，
            // 再按该屏工作区居中；窗口此刻尚未 Activate，两次移动不会闪烁
            try
            {
                if (GetCursorPos(out var pt))
                    AppWindow.Move(new PointInt32(pt.X, pt.Y));
            }
            catch { }

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var display = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Nearest);
            var openIndex = Math.Max(0, OpenWindows.Count - 1);
            var x = display.WorkArea.X + (display.WorkArea.Width - w) / 2 + openIndex * 28;
            var y = display.WorkArea.Y + (display.WorkArea.Height - h) / 2 + openIndex * 28;
            AppWindow.Move(new PointInt32(Math.Max(0, x), Math.Max(0, y)));
        }
        catch { }
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

    /// <summary>提取 magnet dn 参数作为任务名；无 dn 时退回 btih 哈希前段。</summary>
    private static string ExtractDisplayName(string url)
    {
        try
        {
            var qIndex = url.IndexOf('?');
            if (qIndex >= 0)
            {
                foreach (var pair in url[(qIndex + 1)..].Split('&'))
                {
                    var eq = pair.IndexOf('=');
                    if (eq <= 0) continue;
                    if (!pair[..eq].Equals("dn", StringComparison.OrdinalIgnoreCase)) continue;
                    var name = Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' ')).Trim();
                    if (name.Length > 0) return name;
                }
            }
            var xtIndex = url.IndexOf("urn:btih:", StringComparison.OrdinalIgnoreCase);
            if (xtIndex >= 0)
            {
                var hash = url[(xtIndex + "urn:btih:".Length)..];
                var amp = hash.IndexOf('&');
                if (amp >= 0) hash = hash[..amp];
                if (hash.Length > 0) return $"BT 任务（{hash[..Math.Min(16, hash.Length)]}…）";
            }
        }
        catch { }
        return "未命名的磁力任务";
    }

    private async void OnActivatedForInitialFocus(object sender, WindowActivatedEventArgs args)
    {
        if (_initialFocusSet || args.WindowActivationState == WindowActivationState.Deactivated) return;
        _initialFocusSet = true;
        Activated -= OnActivatedForInitialFocus;
        // 前台切换（ForceForeground）过程中 Focus 可能失败，重试直至窗口关闭
        for (var attempt = 1; !_closed; attempt++)
        {
            await Task.Delay(100);
            if (StartBtn.Focus(FocusState.Programmatic))
            {
                App.Log($"[magnet] 确认窗口初始焦点设置成功（第 {attempt} 次尝试）");
                return;
            }
            if (attempt >= 30)
            {
                App.Log("[magnet] 确认窗口初始焦点设置失败：重试已达上限");
                return;
            }
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    private async void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        StartBtn.IsEnabled = false;
        CancelBtn.IsEnabled = false;
        ErrorText.Visibility = Visibility.Collapsed;
        try
        {
            var host = App.Host ?? throw new InvalidOperationException("下载引擎尚未就绪");
            // 磁力链接由引擎做 BT 参数特判；目录传空使用设置中的默认下载目录
            var task = await host.Engine.AddTaskAsync(new NewTaskRequest
            {
                Urls = new List<string> { _magnetUrl },
                Directory = null,
                SpeedLimit = 0
            });
            App.Log($"[magnet] 用户确认下载，任务已创建 gid={task.Gid}");
            Close();
        }
        catch (DuplicateTaskException dex)
        {
            App.Log($"[magnet] 重复任务被拦截: {dex.Message}");
            ErrorText.Text = dex.Message;
            ErrorText.Visibility = Visibility.Visible;
            _busy = false;
            StartBtn.IsEnabled = true;
            CancelBtn.IsEnabled = true;
        }
        catch (Exception ex)
        {
            App.Log($"[magnet] 确认下载创建任务失败: {ex.Message}");
            ErrorText.Text = "创建下载任务失败: " + ex.Message;
            ErrorText.Visibility = Visibility.Visible;
            _busy = false;
            StartBtn.IsEnabled = true;
            CancelBtn.IsEnabled = true;
        }
    }

    private void OnEscapeInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!_busy) Close();
        args.Handled = true;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT pt);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    private struct POINT { public int X; public int Y; }
}
