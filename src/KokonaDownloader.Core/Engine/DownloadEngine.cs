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
    private string? _lastPollSig;
    /// <summary>本会话内新建任务的 Gid 标记：轮询首次上报时消费并随事件携带 IsNewTask。</summary>
    private readonly ConcurrentDictionary<string, byte> _newTaskGids = new();

    /// <summary>最近一次轮询得到的任务快照：UI 定时刷新与"下载前重复预检"直接复用它，
    /// 省掉各自再打一轮 RPC（界面原本每 900ms 自己拉 4 次、每次下载前又拉 3 次）。</summary>
    private volatile List<DownloadTaskInfo> _latestSnapshot = new();
    /// <summary>快照发布时间（DateTime.Ticks，0 = 立即失效）。用 long + Volatile 保证跨线程读写原子。</summary>
    private long _latestSnapshotTicks;

    /// <summary>空闲（没有下载中/排队任务）时的轮询间隔：退避以降低常驻开销。</summary>
    private const int IdlePollIntervalMs = 2500;
    /// <summary>infohash → (Gid, 任务名) 索引：每轮询重建，添加任务前做重复预检。</summary>
    private volatile ConcurrentDictionary<string, (string Gid, string Name)> _btHashIndex = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _startLock = new();
    private bool _started;
    /// <summary>最近一次分配的任务编号（时间戳，单调递增），时钟回拨/同毫秒并发时用它保证唯一。</summary>
    private long _lastTaskNumber;

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
        // 用持久化元数据里的最大编号初始化：系统时钟回拨后重启也不会发出重复编号
        _lastTaskNumber = _store.All().Select(m => m.TaskNumber).DefaultIfEmpty(0).Max();
    }

    /// <summary>生成任务唯一编号：取当前时间戳（yyyyMMddHHmmssfff），与时钟无关的并发/回拨用单调递增兜底。</summary>
    private long NextTaskNumber()
    {
        long candidate, last;
        do
        {
            last = Interlocked.Read(ref _lastTaskNumber);
            var ts = long.Parse(DateTime.Now.ToString("yyyyMMddHHmmssfff", System.Globalization.CultureInfo.InvariantCulture));
            candidate = ts > last ? ts : last + 1;
        } while (Interlocked.CompareExchange(ref _lastTaskNumber, candidate, last) != last);
        return candidate;
    }

    /// <summary>从 URL 猜测落盘文件名（取路径最后一段），猜不出返回 null。</summary>
    private static string? GuessFileNameFromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (url.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)) return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        var seg = uri.Segments.Length > 0 ? uri.Segments[^1] : string.Empty;
        seg = Uri.UnescapeDataString(seg.TrimEnd('/'));
        if (string.IsNullOrEmpty(seg)) return null;
        if (seg.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
        return seg;
    }

    /// <summary>目录内文件名冲突时追加编号：name.ext → name (1).ext → name (2).ext …</summary>
    private static string ResolveUniqueFileName(string dir, string fileName)
    {
        var target = Path.Combine(dir, fileName);
        if (!File.Exists(target)) return fileName;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var i = 1; i < 1000; i++)
        {
            var candidate = $"{stem} ({i}){ext}";
            if (!File.Exists(Path.Combine(dir, candidate))) return candidate;
        }
        return $"{stem} ({DateTime.Now:yyyyMMddHHmmssfff}){ext}";
    }

    /// <summary>普通（非磁力）任务落盘名冲突处理：目标文件已存在时自动改名并把 out 固定为新名，
    /// 使相同链接可以重复下载而不覆盖旧文件。
    /// 仅对"可靠文件名"（浏览器解析出的真实名 / 用户显式指定）生效；
    /// 文件名未知时绝不从 URL 猜测并固定 out——那会覆盖 aria2 按响应 Content-Disposition
    /// 解析出的真实文件名（动态端点下载 exe/zip 时 URL 里只是临时名）。
    /// 磁力/BT 任务不做改名（依赖 infohash 注册语义）。</summary>
    private NewTaskRequest ApplyUniqueFileName(NewTaskRequest req)
    {
        if (req.Urls.Any(u => u.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))) return req;
        if (string.IsNullOrWhiteSpace(req.FileName)) return req; // 文件名未知：交给 aria2 按响应头解析
        var dir = string.IsNullOrWhiteSpace(req.Directory) ? _config.DefaultDownloadDir : req.Directory;
        if (string.IsNullOrEmpty(dir)) return req;
        var baseName = req.FileName;
        if (!File.Exists(Path.Combine(dir, baseName))) return req;
        var unique = ResolveUniqueFileName(dir, baseName);
        if (string.Equals(unique, req.FileName, StringComparison.Ordinal)) return req;
        _log($"目标文件已存在，自动重命名: {baseName} → {unique}");
        return new NewTaskRequest
        {
            Urls = req.Urls,
            Directory = req.Directory,
            FileName = unique,
            Connections = req.Connections,
            SpeedLimit = req.SpeedLimit,
            Referer = req.Referer,
            Headers = req.Headers,
            ExtraOptions = req.ExtraOptions
        };
    }

    /// <summary>
    /// 剔除"只是把 URL 末段重复一遍"的伪文件名。
    ///
    /// 背景：部分站点用不带扩展名的编号链接指向真实文件，真实文件名只在
    /// 响应头（Content-Disposition）或 302 重定向目标里，例如
    ///   https://down.wsyhn.com/23_377276 --302--&gt; https://soft.wsyhn.com/soft/cinebenchr2323.2.zip
    /// 旧版扩展/外部脚本会把链接末段当文件名发过来（"23_377276"），一旦它被固定为
    /// aria2 的 out，真实文件名就永远解析不出来，落盘名会是不带扩展名的编号。
    ///
    /// 判定：给定文件名与任一 URL 的末段（去查询串、URL 解码后）相同 → 视为伪名丢弃，
    /// 交给 aria2 按响应头/重定向解析。与 URL 末段不同的名字（用户手填、浏览器已解析出的
    /// 真实名）保持原样，仍固定 out。
    /// </summary>
    private NewTaskRequest DropUrlDerivedFileName(NewTaskRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.FileName)) return req;
        var name = req.FileName!.Trim();
        foreach (var url in req.Urls)
        {
            var seg = GuessFileNameFromUrl(url);
            if (seg == null || !string.Equals(seg, name, StringComparison.OrdinalIgnoreCase)) continue;
            _log($"忽略 URL 末段伪文件名 '{name}'：交由 aria2 按响应头/重定向解析真实文件名");
            return new NewTaskRequest
            {
                Urls = req.Urls,
                Directory = req.Directory,
                FileName = null,
                Connections = req.Connections,
                SpeedLimit = req.SpeedLimit,
                Referer = req.Referer,
                Headers = req.Headers,
                ExtraOptions = req.ExtraOptions
            };
        }
        return req;
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
            var idle = false;
            try
            {
                var results = await _client.MultiCallAsync(new[]
                {
                    ("aria2.tellActive", Array.Empty<object?>()),
                    ("aria2.tellWaiting", new object?[] { 0, 1000 }),
                    ("aria2.tellStopped", new object?[] { 0, 1000 }),
                    ("aria2.getGlobalStat", Array.Empty<object?>())
                }, ct).ConfigureAwait(false);

                var raws = new List<Aria2TaskStatus>();
                foreach (var node in results.Take(3))
                {
                    if (node is JsonArray arr)
                    {
                        foreach (var item in arr)
                        {
                            var raw = item?.Deserialize<Aria2TaskStatus>(JsonOpts);
                            if (raw != null) raws.Add(raw);
                        }
                    }
                }

                // 兜底清理：aria2 对已注册种子的重复添加会生成 "already registered" 失败任务，
                // 预检存在时间窗（元数据阶段哈希未知），这里把漏网的噪音任务直接从 aria2 与列表中清除
                var dupFailGids = raws
                    .Where(r => r.Status == "error" &&
                                r.ErrorMessage?.Contains("already registered", StringComparison.OrdinalIgnoreCase) == true)
                    .Select(r => r.Gid).ToHashSet();
                if (dupFailGids.Count > 0)
                {
                    foreach (var g in dupFailGids)
                    {
                        try { await _client.RemoveDownloadResultAsync(g, ct).ConfigureAwait(false); } catch { }
                        _store.RemoveMeta(g);
                        _lastStates.TryRemove(g, out _);
                        EngineEvent?.Invoke(this, new EngineEventArgs
                        {
                            Type = "TaskRemoved",
                            Task = new DownloadTaskInfo { Gid = g, State = TaskState.Removed }
                        });
                        _log($"清理重复种子失败任务 gid={g}");
                    }
                    raws = raws.Where(r => !dupFailGids.Contains(r.Gid)).ToList();
                }

                // 重建 infohash 索引（供添加任务重复预检）。
                // 只有 aria2 中真正持有注册的任务（活动/等待/暂停，含做种中）会拒绝重复添加；
                // 完成/失败等 stopped 残留不占注册，删除任务后哈希自然释放、允许重新下载。
                var hashIdx = new ConcurrentDictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
                foreach (var r in raws)
                {
                    if (string.IsNullOrEmpty(r.InfoHash)) continue;
                    if (r.Status is not ("active" or "waiting" or "paused")) continue;
                    var name = r.Files?.FirstOrDefault()?.Path is { } p && !string.IsNullOrEmpty(p)
                        ? Path.GetFileName(p)
                        : r.Bittorrent?.Info?.Name ?? _store.GetMeta(r.Gid)?.Name ?? string.Empty;
                    hashIdx[r.InfoHash] = (r.Gid, name);
                }
                _btHashIndex = hashIdx;

                // 磁力两阶段处理：元数据任务（[METADATA]）完成后通过 followedBy 指向真实下载任务。
                // 处理动作：为真实任务补记元数据 → 清理元数据任务（aria2 + 本地轮询）→ 通知 UI 移除。
                var metaGids = new HashSet<string>();
                foreach (var raw in raws)
                {
                    if (raw.FollowedBy is not { Count: > 0 }) continue;
                    metaGids.Add(raw.Gid);
                    _log($"元数据任务 {raw.Gid} 完成 → 跟随任务 [{string.Join(",", raw.FollowedBy)}]");
                    var mMeta = _store.GetMeta(raw.Gid);
                    var magnet = mMeta?.SourceMagnet
                        ?? mMeta?.Urls.FirstOrDefault(u => u.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
                        ?? mMeta?.Urls.FirstOrDefault();
                    foreach (var fg in raw.FollowedBy)
                    {
                        if (_store.GetMeta(fg) != null) continue;
                        _store.AddMeta(fg, new TaskMeta
                        {
                            Gid = fg,
                            // 真实下载任务继承元数据阶段的编号（同一逻辑任务）；元数据缺失时补发新编号
                            TaskNumber = mMeta?.TaskNumber ?? NextTaskNumber(),
                            Name = mMeta?.Name ?? string.Empty,
                            Urls = mMeta?.Urls ?? new List<string>(),
                            IsBt = true,
                            SourceMagnet = magnet,
                            AddedAt = mMeta?.AddedAt ?? DateTime.Now
                        });
                        _newTaskGids[fg] = 0; // 真实任务按"新建"上报，UI 可弹出进度小窗
                    }
                    try { await _client.RemoveDownloadResultAsync(raw.Gid, ct).ConfigureAwait(false); } catch { }
                }

                var tasks = raws.Where(r => !metaGids.Contains(r.Gid)).Select(ToTaskInfo).ToList();

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

                // 诊断打点：任务集合或统计变化时记录一次（空轮询静默）
                var taskSig = string.Join(",", tasks.Select(t => $"{t.Gid[..Math.Min(8, t.Gid.Length)]}:{t.State}"));
                var pollSig = $"{stats.NumActive}/{stats.NumWaiting}/{stats.NumStopped}|{taskSig}";
                if (pollSig != _lastPollSig)
                {
                    _lastPollSig = pollSig;
                    if (stats.NumActive + stats.NumWaiting + stats.NumStopped > 0)
                        _log($"轮询 active={stats.NumActive} waiting={stats.NumWaiting} stopped={stats.NumStopped} 任务=[{taskSig}]");
                }

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

                // 元数据任务已从 aria2 清除，同步通知 UI 从列表移除
                foreach (var gid in metaGids)
                {
                    _lastStates.TryRemove(gid, out _);
                    EngineEvent?.Invoke(this, new EngineEventArgs { Type = "TaskRemoved", Task = new DownloadTaskInfo { Gid = gid, State = TaskState.Removed } });
                }

                // 发布快照：UI 与重复预检后续都从这里取，不再各自 RPC
                _latestSnapshot = tasks;
                Volatile.Write(ref _latestSnapshotTicks, DateTime.Now.Ticks);

                // 状态字典清理：消失的任务（被删除/被清理）不该一直留在内存里
                if (_lastStates.Count > tasks.Count)
                {
                    var alive = new HashSet<string>(tasks.Select(t => t.Gid));
                    foreach (var gid in _lastStates.Keys)
                        if (!alive.Contains(gid)) _lastStates.TryRemove(gid, out _);
                }

                EngineEvent?.Invoke(this, new EngineEventArgs { Type = "StatsUpdated", Stats = stats, Tasks = tasks });

                // 空转退避：没有任何进行中/排队任务时拉长轮询间隔（仍能及时看到新任务：新任务由 addUri 后立即置位）
                idle = stats.NumActive == 0 && stats.NumWaiting == 0;
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log($"轮询异常: {ex.Message}");
                EngineEvent?.Invoke(this, new EngineEventArgs { Type = "EngineError", Message = ex.Message });
            }
            var delayMs = idle ? Math.Max(_config.PollIntervalMs, IdlePollIntervalMs) : _config.PollIntervalMs;
            try { await Task.Delay(delayMs, ct).ConfigureAwait(false); }
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
            TaskNumber = meta?.TaskNumber ?? 0,
            Dir = raw.Dir,
            TotalLength = raw.TotalLength,
            CompletedLength = raw.CompletedLength,
            DownloadSpeed = raw.DownloadSpeed,
            UploadSpeed = raw.UploadSpeed,
            Connections = raw.Connections,
            NumSeeders = raw.NumSeeders,
            ErrorCode = raw.ErrorCode,
            ErrorMessage = raw.ErrorMessage,
            State = MapState(raw.Status),
            AddedAt = meta?.AddedAt ?? DateTime.Now,
            FinishedAt = meta?.FinishedAt,
            Referer = meta?.Referer,
            NumPieces = raw.NumPieces > int.MaxValue ? int.MaxValue : (int)raw.NumPieces,
            BitField = raw.BitField,
            InfoHash = raw.InfoHash
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

        // BT 任务识别：bittorrent 对象存在 / URL 为磁力 / 元数据标记（多文件种子名取 bittorrent.info.name）
        var isMagnetUrl = info.Urls.Any(u => u.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase));
        info.IsBt = raw.Bittorrent != null || isMagnetUrl || meta?.IsBt == true;
        if (info.IsBt && string.IsNullOrEmpty(info.Name))
            info.Name = raw.Bittorrent?.Info?.Name ?? meta?.Name ?? string.Empty;

        if (string.IsNullOrEmpty(info.Name))
            info.Name = meta?.Name ?? (info.Urls.FirstOrDefault() ?? raw.Gid);

        // 做种状态：bt-detach-seed-only 开启后，做种任务仍是 active 但 seeder=true
        if (info.State == TaskState.Active && raw.Seeder == "true")
            info.State = TaskState.Seeding;

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

    /// <summary>重复种子预检：同 infohash 的任务仍在 aria2（含做种中）时友好拦截，避免产生失败噪音任务。</summary>
    private void EnsureBtNotDuplicate(string? infoHash)
    {
        if (string.IsNullOrEmpty(infoHash)) return;
        if (_btHashIndex.TryGetValue(infoHash, out var existing))
        {
            var name = string.IsNullOrEmpty(existing.Name) ? existing.Gid : existing.Name;
            throw new DuplicateTaskException($"任务\"{name}\"已存在（同一种子正在下载或做种中），无需重复添加", existing.Gid);
        }
    }

    /// <summary>
    /// 检测与给定 URL 重复的"未结束"任务（下载中/排队/暂停/做种中）。
    /// 已完成、失败、已移除的任务不算重复——用户可能已删除下载好的文件，重新添加应作为新任务开始。
    /// </summary>
    public async Task<List<DownloadTaskInfo>> FindActiveDuplicatesAsync(IEnumerable<string> urls, CancellationToken ct = default)
    {
        var targets = urls.Where(u => !string.IsNullOrWhiteSpace(u))
            .Select(u => u.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (targets.Count == 0) return new List<DownloadTaskInfo>();
        // 优先复用轮询快照：每次 /api/download 都额外打一轮 RPC 是纯浪费。
        // 窗口取 1.2s（略大于有任务在跑时的轮询间隔 800ms）：既能在下载中稳定命中缓存，
        // 又不会拿一份过旧的数据去判重（把"刚刚完成"的任务误判成"仍在下载中"而拒收新任务）。
        var all = TryGetRecentSnapshot(1200) ?? await GetAllTasksAsync(ct).ConfigureAwait(false);
        return all
            .Where(t => t.State is not (TaskState.Completed or TaskState.Failed or TaskState.Removed))
            .Where(t => t.Urls.Any(u => targets.Contains(u)))
            .ToList();
    }

    public async Task<DownloadTaskInfo> AddTaskAsync(NewTaskRequest req, CancellationToken ct = default)
    {
        var isMagnet = req.Urls.Any(u => u.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase));
        if (isMagnet)
        {
            EnsureBtNotDuplicate(BtHashUtil.FromMagnet(
                req.Urls.First(u => u.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))));
            // 磁力元数据阶段无法指定文件名/分片，仅保留目录与做种参数
            req = new NewTaskRequest
            {
                Urls = req.Urls,
                Directory = req.Directory,
                SpeedLimit = req.SpeedLimit,
                Referer = req.Referer,
                ExtraOptions = BuildBtSeedOptions()
            };
        }
        else
        {
            // 先剔除"只是把 URL 末段重复一遍"的伪文件名，再做重名冲突处理
            req = DropUrlDerivedFileName(req);
            // 相同文件已存在时自动重命名（追加编号），新任务不覆盖历史下载
            req = ApplyUniqueFileName(req);
        }
        var taskNumber = NextTaskNumber();
        var gid = await _client.AddUriAsync(req, ct).ConfigureAwait(false);
        _log($"addUri 成功 gid={gid} 编号={taskNumber} 磁力={isMagnet}");
        if (!isMagnet) _newTaskGids[gid] = 0; // 磁力首 gid 是元数据任务，不弹进度窗；真实任务由 followedBy 阶段标记
        // 显式重新添加视为撤销墓碑，保证之后可以正常恢复/续传
        _tombstones.Unmark(req.Urls, req.Directory ?? _config.DefaultDownloadDir);
        _store.AddMeta(gid, new TaskMeta
        {
            Gid = gid,
            TaskNumber = taskNumber,
            // 未指定文件名时，用 URL 末段仅作列表显示占位（aria2 尚未上报真实文件路径前）；
            // 该值只用于展示，不会传给 aria2 固定 out——真实落盘名由 aria2 按响应头解析后在轮询中更新
            Name = req.FileName ?? GuessFileNameFromUrl(req.Urls.FirstOrDefault()) ?? string.Empty,
            Urls = req.Urls,
            Referer = req.Referer,
            IsBt = isMagnet,
            SourceMagnet = isMagnet ? req.Urls.First(u => u.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)) : null,
            AddedAt = DateTime.Now
        });
        InvalidateSnapshot(); // 集合已变，判重/列表不应再用旧快照
        var status = await _client.TellStatusAsync(gid, ct).ConfigureAwait(false);
        return status != null
            ? ToTaskInfo(status)
            : new DownloadTaskInfo { Gid = gid, TaskNumber = taskNumber, Name = req.FileName ?? req.Urls.First(), IsBt = isMagnet };
    }

    /// <summary>添加 .torrent 种子任务。</summary>
    public async Task<DownloadTaskInfo> AddTorrentAsync(byte[] torrentData, NewTaskRequest req, CancellationToken ct = default)
    {
        EnsureBtNotDuplicate(BtHashUtil.FromTorrent(torrentData));
        var req2 = new NewTaskRequest
        {
            Urls = req.Urls,
            Directory = req.Directory,
            FileName = req.FileName,
            SpeedLimit = req.SpeedLimit,
            Referer = req.Referer,
            ExtraOptions = BuildBtSeedOptions()
        };
        var taskNumber = NextTaskNumber();
        var gid = await _client.AddTorrentAsync(torrentData, req2, ct).ConfigureAwait(false);
        _newTaskGids[gid] = 0;
        _store.AddMeta(gid, new TaskMeta
        {
            Gid = gid,
            TaskNumber = taskNumber,
            Name = req.FileName ?? string.Empty,
            Urls = req.Urls,
            Referer = req.Referer,
            IsBt = true,
            AddedAt = DateTime.Now
        });
        InvalidateSnapshot();
        var status = await _client.TellStatusAsync(gid, ct).ConfigureAwait(false);
        return status != null
            ? ToTaskInfo(status)
            : new DownloadTaskInfo { Gid = gid, TaskNumber = taskNumber, Name = req.FileName ?? "(种子任务)", IsBt = true };
    }

    /// <summary>构造 BT 做种选项（跟随当前设置；添加时生效，无需等引擎重启）。</summary>
    private Dictionary<string, string>? BuildBtSeedOptions()
    {
        if (!_config.BtEnabled) return null;
        var opts = new Dictionary<string, string>();
        if (!_config.BtSeedEnabled)
        {
            opts["seed-time"] = "0";
        }
        else
        {
            if (_config.SeedRatio > 0)
                opts["seed-ratio"] = _config.SeedRatio.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (_config.SeedTimeMinutes > 0)
                opts["seed-time"] = _config.SeedTimeMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        return opts;
    }

    public async Task<List<DownloadTaskInfo>> AddTasksAsync(IEnumerable<NewTaskRequest> requests, CancellationToken ct = default)
    {
        var list = requests.ToList();
        foreach (var r in list)
        {
            var m = r.Urls.FirstOrDefault(u => u.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase));
            if (m != null) EnsureBtNotDuplicate(BtHashUtil.FromMagnet(m));
        }
        // 每个任务一个时间戳唯一编号；同名文件已存在时自动重命名（磁力除外）
        for (var i = 0; i < list.Count; i++)
        {
            if (!list[i].Urls.Any(u => u.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)))
                list[i] = ApplyUniqueFileName(list[i]);
        }
        var numbers = list.Select(_ => NextTaskNumber()).ToList();
        var gids = await _client.AddUriBatchAsync(list, ct).ConfigureAwait(false);
        var results = new List<DownloadTaskInfo>();
        for (var i = 0; i < gids.Count && i < list.Count; i++)
        {
            if (string.IsNullOrEmpty(gids[i])) continue;
            if (!list[i].Urls.Any(u => u.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)))
                _newTaskGids[gids[i]] = 0; // 磁力首 gid 是元数据任务，不弹进度窗
            // 显式重新添加视为撤销墓碑，保证之后可以正常恢复/续传
            _tombstones.Unmark(list[i].Urls, list[i].Directory ?? _config.DefaultDownloadDir);
            _store.AddMeta(gids[i], new TaskMeta
            {
                Gid = gids[i],
                TaskNumber = numbers[i],
                Name = list[i].FileName ?? string.Empty,
                Urls = list[i].Urls,
                Referer = list[i].Referer,
                AddedAt = DateTime.Now
            });
            results.Add(new DownloadTaskInfo { Gid = gids[i], TaskNumber = numbers[i], Name = list[i].FileName ?? list[i].Urls.First() });
        }
        InvalidateSnapshot();
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
            // 磁力任务以来源磁力链接登记墓碑（会话文件中保存的就是磁力 URI）
            if (meta?.SourceMagnet != null) tombUrls.Add(meta.SourceMagnet);
            if (tombUrls.Count > 0)
                _tombstones.Mark(tombUrls.Distinct(), status?.Dir ?? _config.DefaultDownloadDir);
        }
        catch (Exception ex) { _log($"登记删除墓碑失败: {ex.Message}"); }

        try
        {
            if (status?.Status is "active" or "waiting" or "paused")
            {
                await _client.RemoveAsync(gid, ct).ConfigureAwait(false);
                // aria2.remove 只把任务转入 stopped 结果（做种中的完成任务会残留为 complete），
                // 稍候重试清除该结果，确保列表行消失；失败次数用尽则交由下次手动删除
                for (var i = 0; i < 6; i++)
                {
                    try { await Task.Delay(250, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
                    try
                    {
                        await _client.RemoveDownloadResultAsync(gid, ct).ConfigureAwait(false);
                        break;
                    }
                    catch (Aria2RpcException) { /* 仍在转移为 stopped 结果，重试 */ }
                }
            }
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
        InvalidateSnapshot(); // 集合已变：避免"刚删掉又立刻重下"被判成重复任务而拒收
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

    /// <summary>取全部任务。用一次 multicall 完成（原先 3 次独立 RPC + 3 次 HTTP 往返）。</summary>
    public async Task<List<DownloadTaskInfo>> GetAllTasksAsync(CancellationToken ct = default)
    {
        var (active, waiting, stopped) = await _client.TellAllAsync(ct: ct).ConfigureAwait(false);
        var all = active.Concat(waiting).Concat(stopped).Select(ToTaskInfo).ToList();
        // 已完成/失败优先按完成时间倒序，其余保持
        return all.OrderByDescending(t => t.State is TaskState.Completed or TaskState.Failed ? 1 : 0)
                  .ThenByDescending(t => t.AddedAt).ToList();
    }

    public async Task<GlobalStat> GetGlobalStatAsync(CancellationToken ct = default)
        => await _client.GetGlobalStatAsync(ct).ConfigureAwait(false);

    /// <summary>
    /// 最近一次轮询快照（不超过 <paramref name="maxAgeMs"/> 毫秒时返回，否则返回 null）。
    /// 界面刷新与重复预检用它可以把各自的 RPC 降到 0：轮询本身最多 2.5s 一次，
    /// 对"列表显示"与"重复判断"这类容忍秒级延迟的场景完全够用。
    /// </summary>
    public List<DownloadTaskInfo>? TryGetRecentSnapshot(int maxAgeMs)
    {
        var published = Volatile.Read(ref _latestSnapshotTicks);
        if (published == 0) return null;
        return (DateTime.Now.Ticks - published) <= maxAgeMs * TimeSpan.TicksPerMillisecond ? _latestSnapshot : null;
    }

    /// <summary>
    /// 任务集合刚发生变化（本地新增/删除）时让快照立即失效。
    /// 必须做：否则"刚添加完立刻再发一次同样的链接"这种重复预检会拿着**添加之前**的快照去判重，
    /// 漏判成"不是重复"从而真的建出第二个任务（新增测试 重复链接返回duplicate并跳过添加 抓到的正是这个）。
    /// </summary>
    private void InvalidateSnapshot() => Volatile.Write(ref _latestSnapshotTicks, 0);

    public async Task SetGlobalSpeedLimitAsync(long bytesPerSec, CancellationToken ct = default)
        => await _client.SetGlobalSpeedLimitAsync(bytesPerSec, ct).ConfigureAwait(false);

    public async Task SetTaskSpeedLimitAsync(string gid, long bytesPerSec, CancellationToken ct = default)
        => await _client.SetTaskSpeedLimitAsync(gid, bytesPerSec, ct).ConfigureAwait(false);

    /// <summary>热更新 BT tracker 列表（对后续 announce 生效，无需重启引擎）。</summary>
    public async Task SetBtTrackersAsync(string? trackers, CancellationToken ct = default)
    {
        var opts = new Dictionary<string, string> { ["bt-tracker"] = trackers ?? string.Empty };
        await _client.ChangeGlobalOptionAsync(opts, ct).ConfigureAwait(false);
    }

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
