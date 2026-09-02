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

    public static void Log(string msg)
    {
        try
        {
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}{Environment.NewLine}";
            File.AppendAllText(AppPaths.LogFile, line);
        }
        catch { }
    }

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
    }

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
