using KokonaDownloader.Core.Api;
using KokonaDownloader.Core.Engine;
using KokonaDownloader.Core.Notifications;
using KokonaDownloader.Core.Settings;

namespace KokonaDownloader.Core;

/// <summary>
/// 应用编排层：把引擎、设置、API 服务组装成一个整体。
/// UI 层（WinUI）与测试都通过它访问能力，避免逻辑散落。
/// </summary>
public sealed class AppHost : IAsyncDisposable
{
    public SettingsStore Settings { get; }
    public TaskStore TaskStore { get; }
    public NotifiedStore Notified { get; }
    public DownloadEngine Engine { get; }
    public ApiService Api { get; }
    public Action<string> Log { get; }

    private readonly string _aria2Path;
    private bool _started;

    public AppHost(string aria2Path, SettingsStore settings, Action<string>? log = null)
    {
        _aria2Path = aria2Path;
        Settings = settings;
        Log = log ?? (_ => { });

        TaskStore = new TaskStore(AppPaths.TasksFile);
        Notified = new NotifiedStore(AppPaths.NotifiedFile);
        Engine = new DownloadEngine(BuildEngineConfig(), TaskStore, Log);
        Api = new ApiService(Engine, Settings, settings.Current.ApiPort, Log);

        // 设置变化时同步引擎
        Settings.Changed += OnSettingsChanged;
    }

    private EngineConfig BuildEngineConfig()
    {
        var s = Settings.Current;
        return new EngineConfig
        {
            Aria2Path = _aria2Path,
            WorkDir = AppPaths.EngineWorkDir,
            DefaultDownloadDir = s.DefaultDownloadDir,
            RpcPort = s.RpcPort,
            RpcSecret = s.ApiSecret,
            MaxConcurrentDownloads = s.MaxConcurrentDownloads,
            DefaultConnections = s.DefaultConnections,
            GlobalSpeedLimit = s.GlobalSpeedLimit
        };
    }

    private async void OnSettingsChanged(object? sender, EventArgs e)
    {
        try
        {
            // 全局限速可热更新
            await Engine.SetGlobalSpeedLimitAsync(Settings.Current.GlobalSpeedLimit).ConfigureAwait(false);
        }
        catch (Exception ex) { Log($"同步设置失败: {ex.Message}"); }
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_started) return;
        _started = true;
        // 启动即持久化设置，保证密钥/端口在异常退出后仍稳定
        Settings.Save();
        Directory.CreateDirectory(Settings.Current.DefaultDownloadDir);
        await Engine.StartAsync(ct).ConfigureAwait(false);
        Api.Start();
        Log("AppHost 已启动");
    }

    /// <summary>退出用快速关闭：强杀引擎进程树 + 同步落盘关键数据，
    /// 不做任何网络等待；API 监听端口随进程退出由系统立即回收。</summary>
    public void FastShutdown()
    {
        try { Engine.KillNow(); } catch { }
        try { TaskStore.SaveNow(); } catch { }
        try { Settings.Save(); } catch { }
        Log("AppHost 已快速退出");
    }

    public async ValueTask DisposeAsync()
    {
        Settings.Changed -= OnSettingsChanged;
        Api.Dispose();
        await Engine.DisposeAsync().ConfigureAwait(false);
        Settings.Save();
        Log("AppHost 已停止");
    }
}
