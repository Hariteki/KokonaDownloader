using KokonaDownloader.App.Themes;
using KokonaDownloader.Core;
using KokonaDownloader.Core.Settings;
using Microsoft.UI.Xaml;

namespace KokonaDownloader.App;

/// <summary>
/// 应用入口。负责单实例互斥、AppHost 生命周期、全局异常兜底。
/// </summary>
public partial class App : Application
{
    private static Mutex? _mutex;
    public static AppHost? Host { get; private set; }
    public static MainWindow? MainWin { get; private set; }
    public static TrayService? Tray { get; private set; }
    public static TrayMenuHost? TrayMenu { get; private set; }
    public static NotificationService? Notifications { get; private set; }
    public static string Aria2Path { get; private set; } = string.Empty;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            e.Handled = true;
            Log($"未处理异常: {e.Exception}");
        };
    }

    /// <summary>日志大小上限：超过即轮转为 app.log.1（只保留一份历史，防止无限增长）。</summary>
    private const long MaxLogBytes = 5 * 1024 * 1024;

    public static void Log(string msg)
    {
        try
        {
            // aria2 的 stdout 里大量是空白/纯空格行（控制台读数残影）：丢弃。
            // 实测这类行占历史 app.log 的 96%，保留它们只会让文件以 ~8 MB/天 无限膨胀。
            if (string.IsNullOrWhiteSpace(msg)) return;
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}{Environment.NewLine}";
            RotateLogIfNeeded();
            File.AppendAllText(AppPaths.LogFile, line);
        }
        catch { }
    }

    /// <summary>超过上限时把 app.log 轮转为 app.log.1（覆盖上一份），保证日志有界。</summary>
    private static void RotateLogIfNeeded()
    {
        var path = AppPaths.LogFile;
        var info = new FileInfo(path);
        if (!info.Exists || info.Length < MaxLogBytes) return;
        File.Move(path, path + ".1", overwrite: true);
    }

    private static SplashWindow? _splash;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // 命令行中的磁力链接（magnet: 协议唤起）
        var magnetArg = Environment.GetCommandLineArgs()
            .Skip(1).FirstOrDefault(a => a.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase));

        // 单实例：已运行则激活现有窗口并转发磁力链接
        _mutex = new Mutex(true, @"Local\KokonaDownloader_SingleInstance", out var isNew);
        if (!isNew)
        {
            try
            {
                // 优先经命名管道把磁力链接转发给运行中的实例
                if (magnetArg != null && MagnetIpc.TrySend(magnetArg))
                {
                    Exit();
                    return;
                }
                // 通知已有实例显示窗口（通过命名事件）
                using var ev = System.Threading.EventWaitHandle.OpenExisting(@"Local\KokonaDownloader_ShowWindow");
                ev.Set();
            }
            catch { }
            Exit();
            return;
        }

        // 启动动画：立即显示，让用户知道应用已启动（首次启动等待较久时尤为关键）。
        // 注意：OnLaunched 返回前消息泵不运行，此处只是创建窗口，画面在返回后立刻绘出。
        if (!TryCreateSplash())
        {
            // 创建失败（如 WinUI 组件缺失）：等消息泵启动后重试一次；
            // 再失败也不阻塞启动——主窗口照常显示，用户依然有明确反馈。
            try
            {
                var retryTimer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
                retryTimer.Interval = TimeSpan.FromMilliseconds(300);
                retryTimer.IsRepeating = false;
                retryTimer.Tick += (_, _) =>
                {
                    if (_splash != null) return;
                    if (TryCreateSplash()) Log("启动动画窗口重试创建成功");
                    else Log("启动动画窗口重试仍失败，仅显示主窗口");
                };
                retryTimer.Start();
            }
            catch (Exception exRetry) { Log($"调度启动动画重试失败: {exRetry.Message}"); }
        }

        Aria2Path = Path.Combine(AppContext.BaseDirectory, "aria2c.exe");
        if (!File.Exists(Aria2Path))
        {
            Log($"未找到 aria2c.exe: {Aria2Path}");
        }

        var settings = new SettingsStore(AppPaths.SettingsFile);
        Host = new AppHost(Aria2Path, settings, Log);
        _ = Host.StartAsync();

        // 开机自启：以注册表实际状态为准，保证设置与系统一致
        StartupHelper.SetEnabled(settings.Current.LaunchAtStartup);

        Tray = new TrayService();
        Notifications = new NotificationService(Log);
        TrayMenu = new TrayMenuHost();
        Tray.ShowRequested += ShowMainWindow;
        Tray.TrayRightClicked += (x, y) => TrayMenu.Show(x, y);

        // 主题服务：初始化资源覆盖与设置监听（须在创建窗口之前）
        ThemeService.Initialize();

        MainWin = new MainWindow();

        // magnet: 协议唤起的 IPC 服务端 + 注册表注册（HKCU，无需管理员）
        MagnetIpc.StartServer(url => MainWin?.HandleExternalMagnet(url));
        MagnetProtocol.Register();

        if (magnetArg != null)
        {
            // 首实例带磁力链接启动：弹出独立的磁力确认窗口，主窗口保持隐藏
            MainWin.HandleExternalMagnet(magnetArg);
        }
        else if (Environment.GetCommandLineArgs().Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase)))
        {
            // 开机自启静默启动：只出现在托盘，不显示主窗口，等用户从托盘唤起
            MainWin.AppWindow.Hide();
        }
        else
        {
            MainWin.Activate();
        }

        // 主窗口流程已就绪：请求关闭启动动画。真正的关闭由窗口内的 DispatcherQueue 计时器
        // 在最短展示时长之后执行——此刻消息泵尚未运行，立即 Close 会让启动动画从未被看见。
        DismissSplash();
    }

    /// <summary>创建并激活启动动画窗口。成功返回 true；失败记录日志并返回 false（不抛出）。</summary>
    private static bool TryCreateSplash()
    {
        try
        {
            _splash = new SplashWindow();
            _splash.ScheduleFallbackClose();
            _splash.Activate();
            return true;
        }
        catch (Exception ex)
        {
            _splash = null;
            Log($"启动动画窗口创建失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>关闭启动动画（幂等；窗口内部负责延后到最短展示时长）。</summary>
    public static void DismissSplash() => _splash?.RequestDismiss();

    /// <summary>显示并激活主窗口（托盘双击 / 单实例唤醒 / 通知点击）。</summary>
    public static void ShowMainWindow()
    {
        var win = MainWin;
        if (win == null) return;
        win.DispatcherQueue.TryEnqueue(() =>
        {
            win.AppWindow.Show();
            if (win.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter p) p.Restore();
            win.Activate();
            // 托盘唤起时焦点常在别的进程窗口上，Activate 受系统前台锁定限制不会置顶，
            // 需 AttachThreadInput 强制拉到前台（与进度小窗/磁力确认窗同一处理）
            WindowEffects.ForceForeground(win);
        });
    }

    /// <summary>彻底退出（托盘菜单"退出"）。</summary>
    public static void ExitApp()
    {
        MainWin?.RequestExit();
    }
}
