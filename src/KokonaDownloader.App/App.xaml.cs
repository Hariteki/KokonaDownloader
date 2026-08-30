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
        // 单实例：已运行则激活现有窗口
        _mutex = new Mutex(true, @"Local\KokonaDownloader_SingleInstance", out var isNew);
        if (!isNew)
        {
            // 通知已有实例显示窗口（通过命名事件）
            try
            {
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
        if (Environment.GetCommandLineArgs().Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase)))
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
        });
    }

    /// <summary>彻底退出（托盘菜单"退出"）。</summary>
    public static void ExitApp()
    {
        MainWin?.RequestExit();
    }
}
