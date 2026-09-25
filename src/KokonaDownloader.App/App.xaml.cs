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
            catch (Exception ex)
            {
                // 第八轮 P3-7：这里原先是裸 catch{}，"第二个实例发了唤醒但界面毫无反应"
                // 时无从判断是事件不存在（主窗口还没建起来）、还是主实例根本没在监听
                Log($"唤醒已运行实例失败（{ex.GetType().Name}: {ex.Message}）；若界面未显示，请从托盘手动打开");
            }
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
            MainWin.NotifyHidden(); // 界面刷新定时器随之停摆，别在看不见的窗口上空转
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
            // WinUI 侧先复位（同步 AppWindow.IsVisible 标记），再交给 Win32 做真正的显示/还原/置顶，
            // 最后由 RevealAndActivate 核对窗口是否真的出来了（第八轮 P3-7）
            try { win.AppWindow.Show(); } catch (Exception ex) { Log($"显示主窗口失败: {ex.Message}"); }
            try
            {
                if (win.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter p) p.Restore();
            }
            catch (Exception ex) { Log($"还原主窗口失败: {ex.Message}"); }
            // 隐藏期间界面刷新定时器已停摆，显示后立刻拉回（Activated 事件也兜底，双保险）
            win.ResumeRefreshTimer();
            // 托盘/第二个实例唤起时焦点常在别的进程窗口上，Activate 受系统前台锁定限制不会置顶，
            // 需 AttachThreadInput 强制拉到前台（与进度小窗/磁力确认窗同一处理）
            win.Activate();
            WindowEffects.RevealAndActivate(win);
        });
    }

    /// <summary>彻底退出（托盘菜单"退出"）。</summary>
    public static void ExitApp()
    {
        MainWin?.RequestExit();
    }
}
