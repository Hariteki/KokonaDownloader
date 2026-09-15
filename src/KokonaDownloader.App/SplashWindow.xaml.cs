using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace KokonaDownloader.App;

/// <summary>
/// 启动动画窗口：应用启动时立即显示，让用户知道点击已生效（首次启动等待较久时尤其重要）。
/// 画面只用下载图标 + 应用名称，不含任何说明性文字。
///
/// 生命周期要点：OnLaunched 是同步执行的，返回之前 XAML 消息泵不会运行，
/// 因此此刻关闭窗口等于从未显示过。关闭必须用 DispatcherQueue 计时器延后，
/// 由 App 在 OnLaunched 末尾调用 <see cref="RequestDismiss"/> 起算最短展示时长。
/// </summary>
public partial class SplashWindow : Window
{
    /// <summary>最短展示时长：避免等待很快时窗口一闪而过，反而像闪烁故障。</summary>
    private static readonly TimeSpan MinimumVisible = TimeSpan.FromMilliseconds(1200);

    /// <summary>兜底上限：万一主窗口流程异常没来关，启动动画也不会赖在屏幕上。</summary>
    private static readonly TimeSpan MaximumVisible = TimeSpan.FromSeconds(6);

    private readonly Stopwatch _animationClock = Stopwatch.StartNew();
    private DispatcherQueueTimer? _closeTimer;
    private bool _dismissRequested;
    private bool _closed;

    public SplashWindow()
    {
        InitializeComponent();

        // 无标题栏、不可缩放、置顶的启动画面
        try
        {
            if (AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsResizable = false;
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = false;
                presenter.IsAlwaysOnTop = true;
                presenter.SetBorderAndTitleBar(true, false);
            }
        }
        catch (Exception ex) { App.Log($"设置启动动画窗口样式失败: {ex.Message}"); }

        try
        {
            AppWindow.Resize(new Windows.Graphics.SizeInt32(420, 420));
            CenterOnScreen();
        }
        catch (Exception ex) { App.Log($"设置启动动画窗口尺寸失败: {ex.Message}"); }

        PlayAnimation();
    }

    /// <summary>把窗口摆到当前显示器工作区正中（多屏时按窗口所在显示器计算）。</summary>
    private void CenterOnScreen()
    {
        var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
        if (display == null) return;

        var work = display.WorkArea;
        int x = work.X + (work.Width - AppWindow.Size.Width) / 2;
        int y = work.Y + (work.Height - AppWindow.Size.Height) / 2;
        AppWindow.Move(new Windows.Graphics.PointInt32(Math.Max(work.X, x), Math.Max(work.Y, y)));
    }

    /// <summary>
    /// 入场动画：图标淡入并轻微放大，应用名延迟淡入上浮。
    /// Storyboard 在代码里搭建——XAML 属性文本无法表达 EasingFunction（XBF 会报 Property Not Found）。
    /// </summary>
    private void PlayAnimation()
    {
        // 动画目标属性必须真实存在：XAML 里没声明 RenderTransform，这里补上
        IconHost.RenderTransform = new ScaleTransform();
        NameText.RenderTransform = new TranslateTransform();

        var iconFade = Fade(0, 1, 600, 0, IconHost);
        var iconScaleX = Scale("X", 0.7, 1.0, 600, 0, IconHost);
        var iconScaleY = Scale("Y", 0.7, 1.0, 600, 0, IconHost);
        var nameFade = Fade(0, 1, 500, 350, NameText);
        var nameRise = Rise(12, 0, 500, 350, NameText);

        var storyboard = new Storyboard();
        storyboard.Children.Add(iconFade);
        storyboard.Children.Add(iconScaleX);
        storyboard.Children.Add(iconScaleY);
        storyboard.Children.Add(nameFade);
        storyboard.Children.Add(nameRise);
        storyboard.Begin();
    }

    private static DoubleAnimation Fade(double from, double to, int ms, int beginMs, DependencyObject target)
    {
        var anim = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(ms)),
            BeginTime = TimeSpan.FromMilliseconds(beginMs),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(anim, target);
        Storyboard.SetTargetProperty(anim, "Opacity");
        return anim;
    }

    private static DoubleAnimation Scale(string axis, double from, double to, int ms, int beginMs, DependencyObject target)
    {
        var anim = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(ms)),
            BeginTime = TimeSpan.FromMilliseconds(beginMs),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(anim, target);
        Storyboard.SetTargetProperty(anim, $"(UIElement.RenderTransform).(ScaleTransform.Scale{axis})");
        return anim;
    }

    private static DoubleAnimation Rise(double from, double to, int ms, int beginMs, DependencyObject target)
    {
        var anim = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(ms)),
            BeginTime = TimeSpan.FromMilliseconds(beginMs),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(anim, target);
        Storyboard.SetTargetProperty(anim, "(UIElement.RenderTransform).(TranslateTransform.Y)");
        return anim;
    }

    /// <summary>
    /// 请求关闭（主窗口流程走完时由 App 调用）。实际关闭会在最短展示时长之后，
    /// 且必须等 XAML 消息泵开始运行——否则窗口还没绘制就被销毁。
    /// </summary>
    public void RequestDismiss()
    {
        if (_dismissRequested) return;
        _dismissRequested = true;

        // 计时器回调只在消息泵运行后触发，天然满足"先显示、后关闭"
        var delay = MinimumVisible - _animationClock.Elapsed;
        if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
        ScheduleClose(delay);
    }

    /// <summary>兜底关闭：无论主窗口流程是否成功，超过上限后自行消失。</summary>
    public void ScheduleFallbackClose()
    {
        ScheduleClose(MaximumVisible);
    }

    private void ScheduleClose(TimeSpan delay)
    {
        try
        {
            if (_closed) return;
            _closeTimer ??= DispatcherQueue.CreateTimer();
            _closeTimer.IsRepeating = false;
            _closeTimer.Interval = delay;
            _closeTimer.Tick += (_, _) => CloseNow();
            _closeTimer.Start();
        }
        catch (Exception ex) { App.Log($"启动动画关闭调度失败: {ex.Message}"); }
    }

    private void CloseNow()
    {
        if (_closed) return;
        _closed = true;
        try
        {
            _closeTimer?.Stop();
            Close();
        }
        catch (Exception ex) { App.Log($"关闭启动动画失败: {ex.Message}"); }
    }
}
