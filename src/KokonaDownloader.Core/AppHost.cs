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

    /// <summary>tracker 列表的内存缓存：避免每次设置变更都去读 trackers.json。</summary>
    private string? _cachedTrackers;
    /// <summary>上一次真正下发给引擎的值：只有相关字段变了才发 RPC（见 OnSettingsChanged）。</summary>
    private long _appliedSpeedLimit;
    private string _appliedTrackers = string.Empty;
    /// <summary>已下发给引擎的默认目录/线程数基线（第八轮 P1-1：以前这两项改了要重启才生效）。</summary>
    private string _appliedDefaultDir = string.Empty;
    private int _appliedConnections;
    private int _appliedMaxConcurrent;

    public AppHost(string aria2Path, SettingsStore settings, Action<string>? log = null)
    {
        _aria2Path = aria2Path;
        Settings = settings;
        Log = log ?? (_ => { });

        TaskStore = new TaskStore(AppPaths.TasksFile);
        Notified = new NotifiedStore(AppPaths.NotifiedFile);
        _cachedTrackers = LoadCachedTrackers();
        Engine = new DownloadEngine(BuildEngineConfig(), TaskStore, Log);
        Api = new ApiService(Engine, Settings, settings.Current.ApiPort, Log);

        // 引擎启动时已通过 EngineConfig 拿到这些值，记下来作为"已下发"基线，
        // 这样第一次设置变更不会被误判成需要重新下发。
        _appliedSpeedLimit = settings.Current.GlobalSpeedLimit;
        _appliedTrackers = _cachedTrackers ?? string.Empty;
        _appliedDefaultDir = settings.Current.DefaultDownloadDir ?? string.Empty;
        _appliedConnections = settings.Current.DefaultConnections;
        _appliedMaxConcurrent = settings.Current.MaxConcurrentDownloads;

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
            BtTrackers = _cachedTrackers
        };
    }

    /// <summary>
    /// 设置变更 → 引擎热更新。**只下发真正变化的字段**：
    /// 历史上这里对每次 Changed 都无条件下发全局限速与 tracker 两条 RPC，
    /// 于是切换主题色这类与引擎无关的操作也会各打两次 aria2 调用。
    ///
    /// 可热更新的项：全局限速、BT tracker、默认下载目录、默认线程数、最大并发任务数。
    /// 前两项靠 aria2 的 changeGlobalOption；后三项由引擎在**每次添加任务**时按当前设置
    /// 显式下发（aria2 的 --dir/--split/--max-concurrent-downloads 只是启动快照），
    /// 因此用户在设置里改目录后立刻新建下载就会落到新目录（第八轮 P1-1）。
    /// </summary>
    private async void OnSettingsChanged(object? sender, EventArgs e)
    {
        try
        {
            var s = Settings.Current;

            // 全局限速可热更新
            if (s.GlobalSpeedLimit != _appliedSpeedLimit)
            {
                _appliedSpeedLimit = s.GlobalSpeedLimit;
                await Engine.SetGlobalSpeedLimitAsync(s.GlobalSpeedLimit).ConfigureAwait(false);
            }

            // 默认目录/线程数：引擎在每次添加任务时显式下发当前默认值，改完立刻对新任务生效
            if (!string.IsNullOrWhiteSpace(s.DefaultDownloadDir) &&
                !string.Equals(s.DefaultDownloadDir, _appliedDefaultDir, StringComparison.Ordinal))
            {
                Engine.UpdateRuntimeDefaults(defaultDownloadDir: s.DefaultDownloadDir);
                _appliedDefaultDir = s.DefaultDownloadDir;
            }
            if (s.DefaultConnections > 0 && s.DefaultConnections != _appliedConnections)
            {
                Engine.UpdateRuntimeDefaults(defaultConnections: s.DefaultConnections);
                _appliedConnections = s.DefaultConnections;
            }
            // 最大并发：aria2 支持经 RPC 改，立即生效（否则改了要重启才起作用）
            if (s.MaxConcurrentDownloads > 0 && s.MaxConcurrentDownloads != _appliedMaxConcurrent)
            {
                _appliedMaxConcurrent = s.MaxConcurrentDownloads;
                await Engine.SetMaxConcurrentDownloadsAsync(s.MaxConcurrentDownloads).ConfigureAwait(false);
            }

            // tracker 开关/列表变化时热更新引擎（读内存缓存，不再每次读盘）
            var trackers = s.BtEnabled && s.BtTrackersEnabled ? (_cachedTrackers ?? string.Empty) : string.Empty;
            if (!string.Equals(trackers, _appliedTrackers, StringComparison.Ordinal))
            {
                _appliedTrackers = trackers;
                await Engine.SetBtTrackersAsync(trackers).ConfigureAwait(false);
            }
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

            // 先更新内存缓存与"已下发"基线，再写设置：这样 Settings.Update 触发的
            // OnSettingsChanged 不会把同一份 tracker 再下发一遍。
            var joined = string.Join(",", trackers);
            _cachedTrackers = joined;
            _appliedTrackers = joined;

            Settings.Update(s => { s.BtTrackersUpdatedAt = DateTime.Now; return true; });
            await Engine.SetBtTrackersAsync(joined).ConfigureAwait(false);
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
