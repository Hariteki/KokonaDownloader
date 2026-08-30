using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KokonaDownloader.Core.Engine;

/// <summary>引擎事件：任务状态变化、统计刷新、引擎自身状态。</summary>
public sealed class EngineEventArgs : EventArgs
{
    public required string Type { get; init; }          // "TaskChanged" | "StatsUpdated" | "EngineError"
    public DownloadTaskInfo? Task { get; init; }
    public GlobalStat? Stats { get; init; }
    /// <summary>StatsUpdated 时附带的当前全量任务快照（托盘进度汇总用，避免额外 RPC）。</summary>
    public List<DownloadTaskInfo>? Tasks { get; init; }
    public string? Message { get; init; }
    /// <summary>TaskChanged 时：该任务是否为本会话内新建（UI 据此决定是否弹出进度小窗）。</summary>
    public bool IsNewTask { get; init; }
}

/// <summary>
/// 下载引擎门面：封装 aria2 进程 + RPC + 轮询，向上提供简洁的任务管理接口。
/// 设计决策：
///  - 轮询采用 multicall 一次拉取 active/waiting/stopped 三类，降低开销；
///  - 维护 gid → 元数据（URL/文件名/添加时间）字典，弥补 aria2 不保存自定义元数据的问题；
///  - 通过事件向外推送变化，UI 层无需自行轮询。
/// </summary>
public sealed class DownloadEngine : IAsyncDisposable
{
    private readonly EngineConfig _config;
    private readonly Aria2Process _process;
    private readonly Aria2RpcClient _client;
    private readonly TaskStore _store;
    private readonly TombstoneStore _tombstones;
    private readonly Action<string> _log;
    private CancellationTokenSource? _pollCts;
    private Task? _pollTask;
    private readonly ConcurrentDictionary<string, TaskState> _lastStates = new();
    /// <summary>本会话内新建任务的 Gid 标记：轮询首次上报时消费并随事件携带 IsNewTask。</summary>
    private readonly ConcurrentDictionary<string, byte> _newTaskGids = new();
    private readonly object _startLock = new();
    private bool _started;

    public event EventHandler<EngineEventArgs>? EngineEvent;
    public bool IsRunning => _process.IsRunning;
    public EngineConfig Config => _config;

    public DownloadEngine(EngineConfig config, TaskStore store, Action<string>? log = null)
    {
        _config = config;
        _store = store;
        _log = log ?? (_ => { });
        // 墓碑文件与会话文件同目录：启动 aria2 前用它过滤会话中已删除的条目
        _tombstones = new TombstoneStore(Path.Combine(config.WorkDir, "tombstones.json"));
        _process = new Aria2Process(config, _tombstones, _log);
        _client = new Aria2RpcClient("127.0.0.1", config.RpcPort, config.RpcSecret);
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        lock (_startLock)
        {
            if (_started) return;
            _started = true;
        }
        _process.Start();
        await _process.WaitForReadyAsync(_client, ct: ct).ConfigureAwait(false);
        _pollCts = new CancellationTokenSource();
        _pollTask = Task.Run(() => PollLoopAsync(_pollCts.Token), CancellationToken.None);
        _log("下载引擎已启动");
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var results = await _client.MultiCallAsync(new[]
                {
                    ("aria2.tellActive", Array.Empty<object?>()),
                    ("aria2.tellWaiting", new object?[] { 0, 1000 }),
                    ("aria2.tellStopped", new object?[] { 0, 1000 }),
                    ("aria2.getGlobalStat", Array.Empty<object?>())
                }, ct).ConfigureAwait(false);

                var tasks = new List<DownloadTaskInfo>();
                foreach (var node in results.Take(3))
                {
                    if (node is JsonArray arr)
                    {
                        foreach (var item in arr)
                        {
                            var raw = item?.Deserialize<Aria2TaskStatus>(JsonOpts);
                            if (raw != null) tasks.Add(ToTaskInfo(raw));
                        }
                    }
                }

                // 统计
                var statNode = results.Count > 3 ? results[3] : null;
                var gstat = statNode?.Deserialize<Aria2GlobalStat>(JsonOpts);
                var stats = new GlobalStat
                {
                    DownloadSpeed = gstat?.DownloadSpeed ?? 0,
                    UploadSpeed = gstat?.UploadSpeed ?? 0,
                    NumActive = gstat?.NumActive ?? 0,
                    NumWaiting = gstat?.NumWaiting ?? 0,
                    NumStopped = gstat?.NumStopped ?? 0
                };

                // 检测状态变化并触发事件
                foreach (var t in tasks)
                {
                    var hasPrev = _lastStates.TryGetValue(t.Gid, out var prev);
                    if (hasPrev && prev == t.State) continue;
                    _lastStates[t.Gid] = t.State;
                    if (t.State is TaskState.Completed or TaskState.Failed or TaskState.Removed)
                    {
                        // 判断是否"刚刚结束"：
                        //  - 有前态且前态为非终态 → 运行中刚完成；
                        //  - 无前态（首次上报）且元数据无完成时间 → 本次会话内刚完成；
                        //  - 无前态且有旧完成时间 → 重启后历史任务回放，不视为刚完成。
                        var justFinished = hasPrev
                            ? prev is not (TaskState.Completed or TaskState.Failed or TaskState.Removed)
                            : t.FinishedAt == null;
                        _store.UpdateFinished(t);
                        if (justFinished) t.FinishedAt = DateTime.Now;
                    }
                    var isNewTask = _newTaskGids.TryRemove(t.Gid, out _);
                    EngineEvent?.Invoke(this, new EngineEventArgs { Type = "TaskChanged", Task = t, IsNewTask = isNewTask });
                }

                EngineEvent?.Invoke(this, new EngineEventArgs { Type = "StatsUpdated", Stats = stats, Tasks = tasks });
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log($"轮询异常: {ex.Message}");
                EngineEvent?.Invoke(this, new EngineEventArgs { Type = "EngineError", Message = ex.Message });
            }
            try { await Task.Delay(_config.PollIntervalMs, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
    };

    private DownloadTaskInfo ToTaskInfo(Aria2TaskStatus raw)
    {
        var meta = _store.GetMeta(raw.Gid);
        var info = new DownloadTaskInfo
        {
            Gid = raw.Gid,
            Dir = raw.Dir,
            TotalLength = raw.TotalLength,
            CompletedLength = raw.CompletedLength,
            DownloadSpeed = raw.DownloadSpeed,
            Connections = raw.Connections,
            ErrorCode = raw.ErrorCode,
            ErrorMessage = raw.ErrorMessage,
            State = MapState(raw.Status),
            AddedAt = meta?.AddedAt ?? DateTime.Now,
            FinishedAt = meta?.FinishedAt,
            Referer = meta?.Referer
        };

        // 文件名与路径：优先取第一个文件
        var file = raw.Files?.FirstOrDefault();
        if (file != null && !string.IsNullOrEmpty(file.Path))
        {
            info.FilePath = file.Path;
            info.Name = Path.GetFileName(file.Path);
        }

        // URL 列表：优先文件 uris，其次元数据
        var urls = file?.Uris?.Select(u => u.Uri).Where(u => !string.IsNullOrEmpty(u)).Distinct().ToList();
        info.Urls = urls is { Count: > 0 } ? urls : (meta?.Urls ?? new List<string>());

        if (string.IsNullOrEmpty(info.Name))
            info.Name = meta?.Name ?? (info.Urls.FirstOrDefault() ?? raw.Gid);

        // 限速与线程数来自 option
        if (raw.Option != null)
        {
            if (raw.Option.TryGetValue("max-download-limit", out var lim) && long.TryParse(lim, out var lv))
                info.SpeedLimit = lv;
            if (raw.Option.TryGetValue("split", out var sp) && int.TryParse(sp, out var sv))
                info.Split = sv;
        }
        return info;
    }

    private static TaskState MapState(string s) => s switch
    {
        "active" => TaskState.Active,
        "waiting" => TaskState.Waiting,
        "paused" => TaskState.Paused,
        "complete" => TaskState.Completed,
        "error" => TaskState.Failed,
        "removed" => TaskState.Removed,
        _ => TaskState.Failed
    };

    #region 任务操作

    public async Task<DownloadTaskInfo> AddTaskAsync(NewTaskRequest req, CancellationToken ct = default)
    {
        var gid = await _client.AddUriAsync(req, ct).ConfigureAwait(false);
        _newTaskGids[gid] = 0; // 标记新建任务：轮询上报时随事件携带，UI 仅对新任务弹进度小窗
        // 显式重新添加视为撤销墓碑，保证之后可以正常恢复/续传
        _tombstones.Unmark(req.Urls, req.Directory ?? _config.DefaultDownloadDir);
        _store.AddMeta(gid, new TaskMeta
        {
            Gid = gid,
            Name = req.FileName ?? string.Empty,
            Urls = req.Urls,
            Referer = req.Referer,
            AddedAt = DateTime.Now
        });
        var status = await _client.TellStatusAsync(gid, ct).ConfigureAwait(false);
        return status != null ? ToTaskInfo(status) : new DownloadTaskInfo { Gid = gid, Name = req.FileName ?? req.Urls.First() };
    }

    public async Task<List<DownloadTaskInfo>> AddTasksAsync(IEnumerable<NewTaskRequest> requests, CancellationToken ct = default)
    {
        var list = requests.ToList();
        var gids = await _client.AddUriBatchAsync(list, ct).ConfigureAwait(false);
        var results = new List<DownloadTaskInfo>();
        for (var i = 0; i < gids.Count && i < list.Count; i++)
        {
            if (string.IsNullOrEmpty(gids[i])) continue;
            _newTaskGids[gids[i]] = 0; // 标记新建任务：轮询上报时随事件携带，UI 仅对新任务弹进度小窗
            // 显式重新添加视为撤销墓碑，保证之后可以正常恢复/续传
            _tombstones.Unmark(list[i].Urls, list[i].Directory ?? _config.DefaultDownloadDir);
            _store.AddMeta(gids[i], new TaskMeta
            {
                Gid = gids[i],
                Name = list[i].FileName ?? string.Empty,
                Urls = list[i].Urls,
                Referer = list[i].Referer,
                AddedAt = DateTime.Now
            });
            results.Add(new DownloadTaskInfo { Gid = gids[i], Name = list[i].FileName ?? list[i].Urls.First() });
        }
        return results;
    }

    public async Task PauseAsync(string gid, CancellationToken ct = default) => await _client.PauseAsync(gid, ct).ConfigureAwait(false);
    public async Task ResumeAsync(string gid, CancellationToken ct = default) => await _client.UnpauseAsync(gid, ct).ConfigureAwait(false);
    public async Task PauseAllAsync(CancellationToken ct = default) => await _client.PauseAllAsync(ct).ConfigureAwait(false);
    public async Task ResumeAllAsync(CancellationToken ct = default) => await _client.UnpauseAllAsync(ct).ConfigureAwait(false);

    /// <summary>删除任务。对已完成/失败的任务调用 removeDownloadResult 清理。</summary>
    public async Task RemoveAsync(string gid, bool deleteFile = false, CancellationToken ct = default)
    {
        Aria2TaskStatus? status = null;
        try { status = await _client.TellStatusAsync(gid, ct).ConfigureAwait(false); } catch { }

        // 登记删除墓碑：会话文件每 10 秒落盘，异常退出时已删任务仍留在其中，
        // 下次启动 --input-file 会把任务复活；记下 URL+目录哈希，启动前过滤
        try
        {
            var tombUrls = new List<string>();
            if (status?.Files != null)
                tombUrls.AddRange(status.Files
                    .SelectMany(f => f.Uris ?? Enumerable.Empty<Aria2Uri>())
                    .Select(u => u.Uri)
                    .Where(u => !string.IsNullOrWhiteSpace(u)));
            var meta = _store.GetMeta(gid);
            if (meta != null)
                tombUrls.AddRange(meta.Urls.Where(u => !string.IsNullOrWhiteSpace(u)));
            if (tombUrls.Count > 0)
                _tombstones.Mark(tombUrls.Distinct(), status?.Dir ?? _config.DefaultDownloadDir);
        }
        catch (Exception ex) { _log($"登记删除墓碑失败: {ex.Message}"); }

        try
        {
            if (status?.Status is "active" or "waiting" or "paused")
                await _client.RemoveAsync(gid, ct).ConfigureAwait(false);
            else
                await _client.RemoveDownloadResultAsync(gid, ct).ConfigureAwait(false);
        }
        catch (Aria2RpcException) { /* 任务可能已消失 */ }

        if (deleteFile && status?.Files != null)
        {
            foreach (var f in status.Files)
            {
                try { if (!string.IsNullOrEmpty(f.Path) && File.Exists(f.Path)) File.Delete(f.Path); }
                catch (Exception ex) { _log($"删除文件失败 {f.Path}: {ex.Message}"); }
            }
        }
        _store.RemoveMeta(gid);
        _lastStates.TryRemove(gid, out _);
    }

    /// <summary>重新下载：用原任务的 URL 与参数新建任务。</summary>
    public async Task<DownloadTaskInfo> RedownloadAsync(string gid, CancellationToken ct = default)
    {
        var info = await GetTaskAsync(gid, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"任务 {gid} 不存在");
        if (info.Urls.Count == 0) throw new InvalidOperationException("任务没有可用的下载链接");
        var req = new NewTaskRequest
        {
            Urls = info.Urls,
            Directory = info.Dir,
            FileName = info.Name,
            Connections = info.Split > 0 ? info.Split : _config.DefaultConnections,
            SpeedLimit = info.SpeedLimit,
            Referer = info.Referer
        };
        return await AddTaskAsync(req, ct).ConfigureAwait(false);
    }

    public async Task<DownloadTaskInfo?> GetTaskAsync(string gid, CancellationToken ct = default)
    {
        var status = await _client.TellStatusAsync(gid, ct).ConfigureAwait(false);
        return status != null ? ToTaskInfo(status) : null;
    }

    public async Task<List<DownloadTaskInfo>> GetAllTasksAsync(CancellationToken ct = default)
    {
        var (active, waiting, stopped) = (
            await _client.TellActiveAsync(ct).ConfigureAwait(false),
            await _client.TellWaitingAsync(ct: ct).ConfigureAwait(false),
            await _client.TellStoppedAsync(ct: ct).ConfigureAwait(false));
        var all = active.Concat(waiting).Concat(stopped).Select(ToTaskInfo).ToList();
        // 已完成/失败优先按完成时间倒序，其余保持
        return all.OrderByDescending(t => t.State is TaskState.Completed or TaskState.Failed ? 1 : 0)
                  .ThenByDescending(t => t.AddedAt).ToList();
    }

    public async Task<GlobalStat> GetGlobalStatAsync(CancellationToken ct = default)
        => await _client.GetGlobalStatAsync(ct).ConfigureAwait(false);

    public async Task SetGlobalSpeedLimitAsync(long bytesPerSec, CancellationToken ct = default)
        => await _client.SetGlobalSpeedLimitAsync(bytesPerSec, ct).ConfigureAwait(false);

    public async Task SetTaskSpeedLimitAsync(string gid, long bytesPerSec, CancellationToken ct = default)
        => await _client.SetTaskSpeedLimitAsync(gid, bytesPerSec, ct).ConfigureAwait(false);

    #endregion

    /// <summary>快速停止（退出用）：取消轮询并立即强杀 aria2 进程树，不做任何网络等待。</summary>
    public void KillNow()
    {
        try { _pollCts?.Cancel(); } catch { }
        _process.KillNow();
    }

    public async Task StopAsync()
    {
        _pollCts?.Cancel();
        if (_pollTask != null)
        {
            try { await _pollTask.ConfigureAwait(false); } catch { }
        }
        await _process.StopAsync(_client).ConfigureAwait(false);
        _log("下载引擎已停止");
    }

    public async ValueTask DisposeAsync()
    {
        try { await StopAsync().ConfigureAwait(false); } catch { }
        _client.Dispose();
        _process.Dispose();
    }
}
