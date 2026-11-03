using System.Collections.Concurrent;
using System.Net;


namespace KokonaDownloader.Core.Engine.Idm;

/// <summary>
/// IDM 引擎的任务池：把 <see cref="IdmHttpDownloader"/> 包装成 DownloadEngine 认识的
/// <see cref="DownloadTaskInfo"/>，Gid 统一以 <c>idm-</c> 前缀标识，便于与 aria2 任务区分。
/// 只接管 HTTP/HTTPS/FTP；BT/磁力仍然走 aria2（见 DownloadEngine 的路由）。
/// </summary>
public sealed class IdmTaskManager : IAsyncDisposable
{
    private sealed class Entry
    {
        public required string Gid;
        public required DownloadTaskInfo Info;
        /// <summary>
        /// 当前这次运行的下载器。每次 Start 都新建一个：文件句柄的生命周期与 runner 完全一致，
        /// 暂停后再继续才能干净地重新打开同一个 .kdpart（复用实例会撞上"文件正被另一个进程使用"）。
        /// </summary>
        public IdmHttpDownloader? Downloader;
        public CancellationTokenSource? Cts;
        public Task? Runner;
        public IdmTaskState State = IdmTaskState.Waiting;
        public long Speed;
        public int Connections;
        public long Completed;
        public long Total;
        /// <summary>单任务限速（0 = 跟随全局）。</summary>
        public long TaskLimit;
        public string? Error;
        /// <summary>额外请求头；排队补位/继续/重启恢复都要原样带上。</summary>
        public List<string>? Headers;
    }

    private readonly ConcurrentDictionary<string, Entry> _tasks = new();
    private readonly string _defaultDir;
    private int _defaultConnections;   // 可被 UpdateDefaults 热更新
    private readonly Action<string> _log;
    /// <summary>断点目录（%APPDATA%\KokonaDownloader\parts），与下载目录分离。</summary>
    private readonly string _partsDir;
    /// <summary>全局限速（字节/秒，0 = 不限速）；按活动任务数平分。</summary>
    private long _globalLimit;
    /// <summary>最大同时下载数（与 aria2 的 max-concurrent-downloads 对齐）。</summary>
    private int _maxConcurrent;
    private readonly Queue<string> _pending = new();
    /// <summary>HTTPS 上是否尝试 HTTP/2（见 EngineConfig.Http2Enabled 的取舍说明）。</summary>
    private readonly bool _http2;

    public IdmTaskManager(string defaultDir, int defaultConnections, Action<string>? log = null,
        int maxConcurrent = 3, string? partsDir = null, bool http2 = false)
    {
        _http2 = http2;
        _defaultDir = defaultDir;
        _defaultConnections = defaultConnections;
        _maxConcurrent = Math.Max(1, maxConcurrent);
        _partsDir = string.IsNullOrWhiteSpace(partsDir) ? defaultDir : partsDir;
        _log = log ?? (_ => { });
    }

    /// <summary>全局限速变更：按当前活动任务数重新平分。</summary>
    public void SetGlobalLimit(long bytesPerSecond)
    {
        Interlocked.Exchange(ref _globalLimit, Math.Max(0, bytesPerSecond));
        ApplyFairShare();
    }

    /// <summary>单任务限速变更：与全局平分取较小值。</summary>
    public void SetTaskLimit(string gid, long bytesPerSecond)
    {
        if (!_tasks.TryGetValue(gid, out var e)) return;
        e.TaskLimit = Math.Max(0, bytesPerSecond);
        ApplyFairShare();
    }

    /// <summary>默认目录/连接数变更后，对后续新任务生效。</summary>
    public void UpdateDefaults(string? dir, int? connections)
    {
        if (!string.IsNullOrWhiteSpace(dir)) _defaultDirOverride = dir;
        if (connections is > 0) _defaultConnections = connections.Value;
    }

    private string? _defaultDirOverride;

    private void ApplyFairShare()
    {
        var active = _tasks.Values.Where(t => t.State == IdmTaskState.Active).ToList();
        long global = Interlocked.Read(ref _globalLimit);
        long share = global > 0 && active.Count > 0 ? global / active.Count : 0;

        foreach (var e in active)
        {
            long effective = e.TaskLimit > 0
                ? (share > 0 ? Math.Min(e.TaskLimit, share) : e.TaskLimit)
                : share;
            e.Downloader?.UpdateLimit(effective);
        }
    }

    public static bool Handles(string url) =>
        url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
        url.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase);

    public static bool IsIdmGid(string gid) => gid.StartsWith("idm-", StringComparison.Ordinal);

    /// <summary>
    /// gid 与 taskNumber 由 DownloadEngine 统一分配（与 aria2 路径同源）：
    ///   gid = "idm-" + taskNumber，taskNumber 是 yyyyMMddHHmmssfff 且由 tasks.json 最大值初始化，
    ///   因此跨重启不会撞车，也不会覆盖历史行。
    /// </summary>
    public DownloadTaskInfo Add(string gid, long taskNumber, NewTaskRequest req)
    {
        string url = req.Urls[0];

        var info = new DownloadTaskInfo
        {
            Gid = gid,
            TaskNumber = taskNumber,
            Name = req.FileName ?? GuessName(url),
            Dir = req.Directory ?? _defaultDirOverride ?? _defaultDir,
            Urls = req.Urls,
            Referer = req.Referer,
            State = TaskState.Waiting,
            AddedAt = DateTime.Now,
            IsBt = false,
            Split = req.Connections > 0 ? req.Connections : _defaultConnections,
        };

        var entry = new Entry { Gid = gid, Info = info, Headers = req.Headers };
        _tasks[gid] = entry;

        if (ActiveCount() >= _maxConcurrent)
        {
            // 排队：状态保持 Waiting，等前面的任务结束后再启动
            entry.State = IdmTaskState.Waiting;
            info.State = TaskState.Waiting;
            _pending.Enqueue(gid);
            _log($"[idm] {gid} 排队中（当前活动 {ActiveCount()}/{_maxConcurrent}）");
        }
        else
        {
            Start(entry, req);
        }
        return info;
    }

    private void Start(Entry entry, NewTaskRequest req)
    {
        entry.Cts?.Dispose();
        entry.Cts = new CancellationTokenSource();
        var ct = entry.Cts.Token;

        // 每次运行一个新 downloader：文件句柄随 runner 结束而释放，暂停→继续才不会互相踩
        var downloader = new IdmHttpDownloader(_log);
        downloader.UpdateLimit(entry.TaskLimit);
        entry.Downloader = downloader;

        var opts = new IdmDownloadOptions
        {
            Url = req.Urls[0],
            Mirrors = req.Urls.Count > 1 ? req.Urls.Skip(1).ToList() : null,
            Directory = req.Directory ?? _defaultDirOverride ?? _defaultDir,
            FileName = req.FileName,
            MaxConnections = req.Connections > 0 ? req.Connections : _defaultConnections,
            Referer = req.Referer,
            SpeedLimit = req.SpeedLimit,
            PartsDirectory = _partsDir,
            PartKey = entry.Gid,   // 按 gid 命名断点：同名不同 URL 的两个任务不会互相踩
            OverwriteTarget = req.AllowOverwriteExisting,   // 与 aria2 的 allow-overwrite 同语义，默认 false
            // 只在 https 上生效：download 侧对明文 http 会强制回 1.1（h2c 需要精确要求，对普通站点会连不上）
            RequestVersion = _http2 ? HttpVersion.Version20 : null,
            Headers = req.Headers ?? entry.Headers,
        };
        entry.Headers ??= req.Headers;

        entry.State = IdmTaskState.Active;
        entry.Info.State = TaskState.Active;

        entry.Runner = Task.Run(async () =>
        {
            try
            {
                await downloader.DownloadAsync(opts, p =>
                {
                    entry.Total = p.TotalLength;
                    entry.Completed = p.CompletedLength;
                    entry.Speed = p.Speed;
                    entry.Connections = p.Connections;
                    // 进度必须**同时**写进 Info：Info 才是接口/界面读到的那份。
                    // 只写 entry 再由 Snapshot() 惰性同步的话，会有一个致命窗口——
                    // 收尾时 entry.Info.State 立刻变成 Completed，而 Info.CompletedLength
                    // 还停在上一次轮询的数字，于是"已完成 98.31%"（实测 10MB 文件差 177KB）；
                    // 任务比第一轮轮询还快时更糟：已完成，但显示 0 B / 0 B。
                    entry.Info.TotalLength = p.TotalLength;
                    entry.Info.CompletedLength = p.CompletedLength;
                    entry.Info.DownloadSpeed = p.Speed;
                    entry.Info.Connections = p.Connections;
                    entry.Info.FilePath = p.FilePath;
                    // 落盘名可能因防覆盖被改成 "name (1).ext"：列表/历史显示的必须是真实文件名
                    if (!string.IsNullOrEmpty(p.FilePath)) entry.Info.Name = Path.GetFileName(p.FilePath);
                }, ct).ConfigureAwait(false);

                SyncInfoFromEntry(entry);
                entry.State = IdmTaskState.Completed;
                entry.Info.State = TaskState.Completed;
                entry.Info.FinishedAt = DateTime.Now;
            }
            catch (OperationCanceledException)
            {
                SyncInfoFromEntry(entry);
                entry.State = IdmTaskState.Paused;
                entry.Info.State = TaskState.Paused;
            }
            catch (Exception ex)
            {
                SyncInfoFromEntry(entry);
                entry.State = IdmTaskState.Failed;
                entry.Info.State = TaskState.Failed;
                entry.Error = ex.Message;
                entry.Info.ErrorMessage = ex.Message;
                entry.Info.ErrorCode = 1;
                _log($"[idm] 任务 {entry.Gid} 失败: {ex.Message}");
            }
            finally
            {
                entry.Speed = 0;
                entry.Connections = 0;
                try { await downloader.DisposeAsync().ConfigureAwait(false); } catch { }
                PumpQueue();      // 让出名额给排队任务
                ApplyFairShare(); // 活动数变了，全局额度要重新平分
            }
        }, ct);
    }

    /// <summary>
    /// 把 entry 上的实时数字刷进 Info（接口/界面读的那份）。
    /// 终态切换前必须调一次：否则会短暂报出"已完成 + 上一拍的进度"。
    /// </summary>
    private static void SyncInfoFromEntry(Entry e)
    {
        e.Info.TotalLength = e.Total;
        e.Info.CompletedLength = e.Completed;
        e.Info.DownloadSpeed = e.Speed;
        e.Info.Connections = e.Connections;
        if (!string.IsNullOrEmpty(e.Info.FilePath)) e.Info.Name = Path.GetFileName(e.Info.FilePath);
    }

    private int ActiveCount() => _tasks.Values.Count(t => t.State == IdmTaskState.Active);

    /// <summary>并发上限热更新；放宽时立刻补位。</summary>
    public void SetMaxConcurrent(int value)
    {
        _maxConcurrent = Math.Max(1, value);
        PumpQueue();
    }

    /// <summary>用任务自身信息重建一次启动请求（排队补位 / 继续全部共用）。</summary>
    private static NewTaskRequest RequestOf(Entry e) => new()
    {
        Urls = e.Info.Urls,
        Directory = e.Info.Dir,
        FileName = e.Info.Name,
        Referer = e.Info.Referer,
        Connections = e.Info.Split,
        Headers = e.Headers,
    };

    /// <summary>有任务结束时补位。排队项用首次添加时记录的请求重建。</summary>
    private void PumpQueue()
    {
        while (ActiveCount() < _maxConcurrent && _pending.Count > 0)
        {
            string gid = _pending.Dequeue();
            if (!_tasks.TryGetValue(gid, out var e)) continue;
            if (e.State != IdmTaskState.Waiting) continue;
            Start(e, RequestOf(e));
        }
    }

    public List<DownloadTaskInfo> Snapshot()
    {
        foreach (var e in _tasks.Values)
        {
            e.Info.TotalLength = e.Total;
            e.Info.CompletedLength = e.Completed;
            e.Info.DownloadSpeed = e.Speed;
            e.Info.Connections = e.Connections;
        }
        return _tasks.Values.Select(e => e.Info).ToList();
    }

    public long TotalSpeed => _tasks.Values.Sum(t => t.Speed);

    /// <summary>该 gid 是否由本管理器持有（区分"形状合法但不存在"与"存在"）。</summary>
    public bool Contains(string gid) => _tasks.ContainsKey(gid);

    public DownloadTaskInfo? Get(string gid)
    {
        if (!_tasks.TryGetValue(gid, out var e)) return null;
        // 与 Snapshot 同步一次：否则 GetTaskAsync 拿到的是"上次轮询时的旧进度"
        e.Info.TotalLength = e.Total;
        e.Info.CompletedLength = e.Completed;
        e.Info.DownloadSpeed = e.Speed;
        e.Info.Connections = e.Connections;
        return e.Info;
    }

    public void Pause(string gid)
    {
        if (!_tasks.TryGetValue(gid, out var e) || e.State != IdmTaskState.Active) return;
        e.Cts?.Cancel();
        e.Info.State = TaskState.Paused;
    }

    /// <summary>
    /// 暂停全部（对应 UI 的「全部暂停」）。排队中的任务也一并转入暂停，
    /// 否则 PumpQueue 会在别的任务结束时把它拉起来——用户看到的就是"暂停了又自己跑了"。
    /// 返回实际暂停的数量。
    /// </summary>
    public int PauseAll()
    {
        // 必须先处理排队项并清空队列，再去取消活动任务：
        // runner 的 finally 会调用 PumpQueue()，此时若队列里还有人、活动数又降到 0，
        // 它会立刻把"刚被暂停的排队任务"重新拉起来 —— 用户看到的就是"暂停了又自己跑"。
        int n = 0;
        _pending.Clear();
        foreach (var e in _tasks.Values)
        {
            if (e.State == IdmTaskState.Waiting)
            {
                e.State = IdmTaskState.Paused;
                e.Info.State = TaskState.Paused;
                n++;
            }
        }

        foreach (var e in _tasks.Values)
        {
            if (e.State != IdmTaskState.Active) continue;
            e.Cts?.Cancel();
            e.State = IdmTaskState.Paused;
            e.Info.State = TaskState.Paused;
            n++;
        }
        return n;
    }

    /// <summary>继续全部（含重启恢复出来的暂停任务）；超出并发上限的排队等待。返回实际启动的数量。</summary>
    public async Task<int> ResumeAllAsync()
    {
        int n = 0;
        foreach (var e in _tasks.Values)
        {
            if (e.State != IdmTaskState.Paused) continue;
            if (ActiveCount() >= _maxConcurrent)
            {
                e.State = IdmTaskState.Waiting;
                e.Info.State = TaskState.Waiting;
                _pending.Enqueue(e.Gid);
                continue;
            }
            await SettleRunnerAsync(e).ConfigureAwait(false);   // 同 Resume：不能抢在旧 runner 退出前启动
            Start(e, RequestOf(e));
            n++;
        }
        if (n > 0) _log($"[idm] 继续全部：启动 {n} 个任务");
        return n;
    }

    /// <summary>
    /// 继续一个暂停的任务。必须先等上一次 runner 真正退出：它退出前仍持有
    /// `.kdpart` 的文件句柄，急着启动新 runner 会直接失败
    /// （真机表现：「失败: 文件正被另一个进程使用」）。
    /// 等待同时也保证了"暂停时保存的断点"已经落盘，续传才不会从 0 重来。
    /// </summary>
    public async Task<bool> ResumeAsync(string gid, NewTaskRequest req)
    {
        if (!_tasks.TryGetValue(gid, out var e)) return false;
        if (e.State is IdmTaskState.Completed) return false;
        await SettleRunnerAsync(e).ConfigureAwait(false);
        Start(e, req);
        return true;
    }

    /// <summary>取消并等待该任务的 runner 退出（拿不到句柄/断点未落盘的问题一次解决）。</summary>
    private static async Task SettleRunnerAsync(Entry e)
    {
        e.Cts?.Cancel();
        var runner = e.Runner;
        if (runner is null || runner.IsCompleted) return;
        try { await runner.ConfigureAwait(false); } catch { /* 取消导致的异常忽略 */ }
    }

    public bool Remove(string gid, bool deleteFile)
    {
        if (!_tasks.TryRemove(gid, out var e)) return false;
        e.Cts?.Cancel();
        if (deleteFile && !string.IsNullOrEmpty(e.Info.FilePath))
        {
            try { if (File.Exists(e.Info.FilePath)) File.Delete(e.Info.FilePath); } catch { }
        }
        // 断点与状态文件属于任务私有数据：任务被删掉后不该继续占着磁盘
        string part = PartPath(gid, e.Info.Dir);
        try { if (File.Exists(part)) File.Delete(part); } catch { }
        try { if (File.Exists(part + ".state.json")) File.Delete(part + ".state.json"); } catch { }
        // 跨卷时断点放在目标盘的隐藏目录里，空了就一起收掉
        IdmHttpDownloader.TryRemoveEmptyDirectory(Path.GetDirectoryName(part) ?? "");
        return true;
    }

    /// <summary>
    /// 任务断点路径（与 DownloadAsync 内的命名规则必须一致）。
    /// 必须把目标目录传进来：跨卷时断点会挪到目标盘的隐藏子目录，否则这里会指向一个不存在的路径，
    /// 删除任务时留下孤儿断点、重启恢复时又判断为"断点不存在"而丢掉任务。
    /// </summary>
    private string PartPath(string gid, string? targetDir)
        => Path.Combine(IdmHttpDownloader.ResolvePartsDirectory(_partsDir, targetDir ?? _defaultDir), $"{gid}.kdpart");

    /// <summary>
    /// 重启恢复：把上次没下完的任务按「已暂停」重建进任务池。
    /// 刻意不自动开跑 —— 与 aria2 会话恢复一致（重启后不该偷偷占带宽），用户点「继续」才续传。
    /// 只有断点文件仍在时才恢复；否则那只是个永远失败的壳子，不如让用户重新添加。
    /// </summary>
    public int Restore(IEnumerable<TaskMeta> metas)
    {
        int restored = 0;
        foreach (var m in metas)
        {
            if (m.FinishedAt is not null) continue;
            if (!IsIdmGid(m.Gid) || _tasks.ContainsKey(m.Gid)) continue;

            string part = PartPath(m.Gid, m.Dir ?? _defaultDir);
            if (!File.Exists(part))
            {
                // 跨卷改造前断点统一在 %APPDATA%：先搬过来，否则这个暂停中的任务会被当成
                // "断点丢了"直接丢弃，用户昨天下的 90% 凭空消失。
                // 只发生一次（之后就在目标盘了），启动时没有句柄占用，搬是安全的。
                string legacy = Path.Combine(_partsDir, $"{m.Gid}.kdpart");
                if (File.Exists(legacy))
                {
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(part)!);
                        File.Move(legacy, part);
                        if (File.Exists(legacy + ".state.json") && !File.Exists(part + ".state.json"))
                            File.Move(legacy + ".state.json", part + ".state.json");
                        _log($"[idm] 断点已迁移到目标盘: {m.Gid}");
                    }
                    catch (Exception ex)
                    {
                        _log($"[idm] 断点迁移失败（该任务将无法续传）: {ex.Message}");
                    }
                }
            }
            if (!File.Exists(part)) continue;

            var st = IdmHttpDownloader.TryLoadState(part + ".state.json");
            long total = st?.TotalLength ?? 0;
            long completed = st is null
                ? 0
                : st.Segments.Sum(s => Math.Min(s.Written, Math.Max(0, s.End - s.Start)));

            var info = new DownloadTaskInfo
            {
                Gid = m.Gid,
                TaskNumber = m.TaskNumber,
                Name = string.IsNullOrWhiteSpace(m.Name)
                    ? GuessName(m.Urls.FirstOrDefault() ?? "")
                    : m.Name,
                Dir = m.Dir ?? _defaultDir,
                Urls = m.Urls,
                Referer = m.Referer,
                State = TaskState.Paused,
                AddedAt = m.AddedAt,
                IsBt = false,
                Split = _defaultConnections,
                TotalLength = total,
                CompletedLength = completed,
            };

            // 重启恢复：请求头从元数据带回来，否则需要鉴权的下载会 403

            _tasks[m.Gid] = new Entry
            {
                Gid = m.Gid,
                Info = info,
                State = IdmTaskState.Paused,
                Total = total,
                Completed = completed,
                Headers = m.Headers,
            };
            restored++;
        }

        if (restored > 0) _log($"[idm] 已恢复 {restored} 个未完成任务（状态：已暂停）");
        return restored;
    }

    private static string GuessName(string url)
    {
        try
        {
            var name = Path.GetFileName(new Uri(url).AbsolutePath);
            return string.IsNullOrWhiteSpace(name) ? "download" : name;
        }
        catch { return "download"; }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var e in _tasks.Values)
        {
            e.Cts?.Cancel();
            if (e.Runner is not null)
            {
                try { await e.Runner.ConfigureAwait(false); } catch { }
            }
            if (e.Downloader is { } d) { try { await d.DisposeAsync().ConfigureAwait(false); } catch { } }
        }
        _tasks.Clear();
    }
}
