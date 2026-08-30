using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using KokonaDownloader.Core;
using WinForms = System.Windows.Forms;

namespace KokonaDownloader.App;

/// <summary>
/// 系统托盘：NotifyIcon + 动态进度图标 + 悬浮提示 + 右键事件。
/// 图标使用 icons 包的托盘底图（titlebar-tray 造型），下载中在外圈叠加总体进度环；底图缺失时退回内置箭头绘制。
/// 右键菜单不再用 WinForms ContextMenuStrip（样式陈旧且无法圆角），
/// 改为抛出 TrayRightClicked，由 TrayMenuHost 用 WinUI 原生 MenuFlyout 展示。
/// </summary>
public sealed class TrayService : IDisposable
{
    private readonly WinForms.NotifyIcon _notifyIcon;
    private Icon? _currentIcon;
    private TrayProgress _last = new(0, 0, 0, 0);
    private double _lastDrawnPercent = -1;
    private bool _lastBusy;

    public event Action? ShowRequested;

    /// <summary>右键点击托盘图标，参数为物理屏幕坐标（光标位置）。</summary>
    public event Action<int, int>? TrayRightClicked;

    /// <summary>当前气泡的点击回调（一次只保留最新一条通知的回调）。</summary>
    private Action? _balloonClick;

    /// <summary>
    /// 通过托盘气泡弹出系统通知横幅（Win11 上气泡会以 Toast 横幅形式弹出，实测比未打包应用的
    /// WinRT Toast 通道更可靠）。onClick 在用户点击横幅时触发（通常用于打开所在文件夹）。
    /// </summary>
    public void ShowBalloon(string title, string message, Action? onClick = null)
    {
        try
        {
            _balloonClick = onClick;
            _notifyIcon.ShowBalloonTip(8000, title, message, WinForms.ToolTipIcon.None);
        }
        catch (Exception ex) { App.Log($"托盘通知失败: {ex.Message}"); }
    }

    public TrayService()
    {
        _notifyIcon = new WinForms.NotifyIcon
        {
            Text = "海兔下载器",
            Visible = true
        };
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left)
                ShowRequested?.Invoke();
            else if (e.Button == WinForms.MouseButtons.Right)
                TrayRightClicked?.Invoke(WinForms.Cursor.Position.X, WinForms.Cursor.Position.Y);
        };
        _notifyIcon.BalloonTipClicked += (_, _) =>
        {
            var action = _balloonClick;
            _balloonClick = null;
            try { action?.Invoke(); } catch { }
        };
        _notifyIcon.BalloonTipClosed += (_, _) => _balloonClick = null;
        UpdateIcon(force: true);
    }

    /// <summary>由引擎统计事件驱动（UI 线程调用）。</summary>
    public void Update(TrayProgress progress)
    {
        _last = progress;
        // 图标只在进度变化 >=0.5% 或忙闲切换时重绘，避免无谓 GDI 开销
        var needRedraw = Math.Abs(progress.Percent - _lastDrawnPercent) >= 0.5 || progress.IsBusy != _lastBusy;
        if (needRedraw) UpdateIcon(force: false);
        UpdateTooltip();
    }

    private void UpdateTooltip()
    {
        var text = _last.IsBusy
            ? $"海兔下载器\n总进度 {_last.Percent:0.#}% · {FormatUtil.FormatBytes(_last.DownloadSpeed)}/s\n下载中 {_last.ActiveCount} · 等待 {_last.WaitingCount}"
            : "海兔下载器\n空闲";
        if (text.Length > 63) text = text[..63];
        _notifyIcon.Text = text;
    }

    private void UpdateIcon(bool force)
    {
        _lastDrawnPercent = _last.Percent;
        _lastBusy = _last.IsBusy;
        var newIcon = DrawIcon(_last.Percent, _last.IsBusy);
        var old = _currentIcon;
        _notifyIcon.Icon = newIcon;
        _currentIcon = newIcon;
        if (!force || old != null) old?.Dispose();
    }

    /// <summary>icons 包托盘底图（titlebar-tray 造型），惰性加载一次并常驻。</summary>
    private static Bitmap? _trayBase;

    private static Bitmap? TrayBase()
    {
        if (_trayBase != null) return _trayBase;
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "icons", "tray.png");
            if (File.Exists(path)) _trayBase = new Bitmap(path);
        }
        catch (Exception ex) { App.Log($"加载托盘底图失败: {ex.Message}"); }
        return _trayBase;
    }

    /// <summary>绘制 32x32 托盘图标：icons 包底图 + 下载中叠加总体进度环。</summary>
    private static Icon DrawIcon(double percent, bool busy)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            var baseBmp = TrayBase();
            if (baseBmp != null)
            {
                g.DrawImage(baseBmp, new Rectangle(0, 0, 32, 32));
            }
            else
            {
                // 底图缺失时退回内置箭头绘制
                var fallback = busy ? Color.FromArgb(255, 0x60, 0xCD, 0xFF) : Color.FromArgb(210, 190, 190, 190);
                using var brush = new SolidBrush(fallback);
                g.FillRectangle(brush, 13.5f, 7f, 5f, 8f);                                  // 箭杆
                g.FillPolygon(brush, new PointF[] { new(9.5f, 15f), new(22.5f, 15f), new(16f, 22.5f) }); // 箭头
                g.FillRectangle(brush, 10f, 24.5f, 12f, 2.4f);                              // 底部托盘线
            }

            if (busy && percent > 0)
            {
                var accent = Color.FromArgb(255, 0x60, 0xCD, 0xFF);   // WinUI 强调色（浅底可见）
                using var progPen = new Pen(accent, 2.6f);
                g.DrawArc(progPen, new RectangleF(1.5f, 1.5f, 29f, 29f), -90f,
                    (float)(360 * Math.Clamp(percent, 0, 100) / 100));
            }
        }

        var handle = bmp.GetHicon();
        try
        {
            using var tmp = Icon.FromHandle(handle);
            return (Icon)tmp.Clone();
        }
        finally { DestroyIcon(handle); }
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _currentIcon?.Dispose();
    }
}
