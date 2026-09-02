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
            GlobalSpeedLimit = s.GlobalSpeedLimit,
            BtEnabled = s.BtEnabled,
            BtListenPort = s.BtPort,
            BtSeedEnabled = s.BtSeedEnabled,
            SeedRatio = s.SeedRatio,
            SeedTimeMinutes = s.SeedTimeMinutes,
            BtTrackers = LoadCachedTrackers()
        };
    }

    private async void OnSettingsChanged(object? sender, EventArgs e)
    {
        try
        {
            // 全局限速可热更新
            await Engine.SetGlobalSpeedLimitAsync(Settings.Current.GlobalSpeedLimit).ConfigureAwait(false);
            // tracker 开关/列表变化时热更新引擎
            if (Settings.Current.BtEnabled && Settings.Current.BtTrackersEnabled)
                await Engine.SetBtTrackersAsync(LoadCachedTrackers()).ConfigureAwait(false);
            else
                await Engine.SetBtTrackersAsync(string.Empty).ConfigureAwait(false);
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
        if (Settings.Current.BtEnabled && Settings.Current.BtTrackersEnabled)
            _ = RefreshTrackersAsync(); // 后台刷新 tracker 列表，失败静默（用缓存兜底）
        Log("AppHost 已启动");
    }

    /// <summary>读取本地 tracker 缓存（每行一个，逗号拼接给 aria2）。</summary>
    private string? LoadCachedTrackers()
    {
        try
        {
            if (!File.Exists(AppPaths.TrackersFile)) return null;
            var list = System.Text.Json.JsonSerializer.Deserialize<List<string>>(File.ReadAllText(AppPaths.TrackersFile));
            var joined = string.Join(",", (list ?? new()).Where(u => !string.IsNullOrWhiteSpace(u)));
            return joined.Length > 0 ? joined : null;
        }
        catch { return null; }
    }

    /// <summary>更新 BT tracker 列表：超过 24h 或无缓存时从公开源获取，成功后写缓存并热更新引擎。</summary>
    private async Task RefreshTrackersAsync()
    {
        const string url = "https://raw.githubusercontent.com/ngosang/trackerslist/master/trackers_best.txt";
        try
        {
            if (!Settings.Current.BtTrackersEnabled) return;
            if (File.Exists(AppPaths.TrackersFile) &&
                (DateTime.Now - Settings.Current.BtTrackersUpdatedAt).TotalHours < 24) return;

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; KokonaDownloader)");
            var body = await http.GetStringAsync(url).ConfigureAwait(false);
            var trackers = body.Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.StartsWith('#'))
                .Distinct().ToList();
            if (trackers.Count == 0) return;

            var dir = Path.GetDirectoryName(AppPaths.TrackersFile);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(AppPaths.TrackersFile, System.Text.Json.JsonSerializer.Serialize(trackers));
            Settings.Update(s => { s.BtTrackersUpdatedAt = DateTime.Now; return true; });
            await Engine.SetBtTrackersAsync(string.Join(",", trackers)).ConfigureAwait(false);
            Log($"BT tracker 列表已更新（{trackers.Count} 条）");
        }
        catch (Exception ex) { Log($"更新 BT tracker 列表失败（使用缓存）: {ex.Message}"); }
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
