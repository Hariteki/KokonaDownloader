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
    /// <summary>下载前的"真实文件名"预解析器（第十轮，见测试报告 §16）。</summary>
    private readonly RemoteNameResolver _nameResolver;
    /// <summary>引擎自愈锁：多个任务同时撞上"引擎已退出"时只重启一次。</summary>
    private readonly SemaphoreSlim _engineRestartLock = new(1, 1);
    private readonly Action<string> _log;
    private CancellationTokenSource? _pollCts;
    private Task? _pollTask;
    private readonly ConcurrentDictionary<string, TaskState> _lastStates = new();
    /// <summary>上一轮轮询的任务指纹（FNV-32 无分配整数，见 PollLoopAsync 诊断打点）。</summary>
    private int _lastPollSig;
    /// <summary>本会话内新建任务的 Gid 标记：轮询首次上报时消费并随事件携带 IsNewTask。</summary>
    private readonly ConcurrentDictionary<string, byte> _newTaskGids = new();

    /// <summary>
    /// 待校验的磁力添加（第十一轮，见测试报告 §16-G #11 / §16-H）：aria2 对"同 infohash 已在引擎
    /// 注册"的新 addUri 会先正常返回 gid、随后**静默丢弃**该任务，gid 在 tellStatus/tell* 里查无此人，
    /// 界面就留一条永不报错的 0% 幻影行。添加时登记 gid，轮询里确认它是否真被 aria2 注册过；
    /// 超过期限仍未出现则标失败并写明原因。键为 gid。
    /// </summary>
    private readonly ConcurrentDictionary<string, PendingMagnetAdd> _pendingMagnetAdds = new();

    /// <summary>磁力添加后的存活校验期限：超过它仍未出现在 aria2 的 tell* 列表里即判定被静默丢弃。</summary>
    private const int MagnetLivenessTimeoutMs = 6000;

    private sealed record PendingMagnetAdd(string Magnet, long TaskNumber, DateTime Deadline);

    /// <summary>最近一次轮询得到的任务快照：UI 定时刷新与"下载前重复预检"直接复用它，
    /// 省掉各自再打一轮 RPC（界面原本每 900ms 自己拉 4 次、每次下载前又拉 3 次）。</summary>
    private volatile List<DownloadTaskInfo> _latestSnapshot = new();
    /// <summary>快照发布时间（DateTime.Ticks，0 = 立即失效）。用 long + Volatile 保证跨线程读写原子。</summary>
    private long _latestSnapshotTicks;
    /// <summary>与任务快照同一轮发布的全球统计（GET /api/stats 直接复用它，省一次独立 RPC）。</summary>
    private volatile GlobalStat? _latestStats;
    /// <summary>本地历史（已完成 / 失败任务的终态快照，gid → 行）。
    /// aria2 的 --save-session 实测不会写入已完成任务（探针：4 个 complete 任务，会话文件 0 字节），
    /// 所以重启后"已完成"整段列表必然从 aria2 消失。客户端把终态关键字段留档在 tasks.json，
    /// 由这里合成历史行，与 aria2 的实时任务合并后发布给界面与 API，两边才能继续一致。
    /// 见第八轮测试报告 §14-C 缺陷 4。</summary>
    private readonly ConcurrentDictionary<string, DownloadTaskInfo> _history = new();
    /// <summary>本地历史上限：超出后按完成时间丢弃最旧的行（含其持久化元数据），防止 tasks.json 无界增长。</summary>
    private const int MaxHistoryRows = 2000;
    /// <summary>本会话是否已回收过"aria2 里不存在、又没有终态快照"的失效元数据（首轮轮询后做一次）。</summary>
    private int _staleMetaPruned;
    /// <summary>读接口（列表/统计）允许复用快照的最大年龄。
    /// 必须**严格大于空闲轮询间隔** <see cref="IdlePollIntervalMs"/>：空闲态（没有下载中/排队任务）
    /// 轮询会退避到 2500 ms 一次，若窗口取 2000 ms，则空闲态下每次读都判成"快照太旧"而退回实时 RPC，
    /// 复用等于完全没生效——实测 200 次 GET /api/tasks 期间 aria2 CPU 与旧版持平（62 ms vs 78 ms）。
    /// 窗口比轮询间隔宽并不会让数据更旧：快照内容本来就出自最近一轮轮询（忙时 800 ms、闲时 2500 ms），
    /// 这个窗口只是"这一轮还算不算数"的判定线；而且本地一旦发生增删/暂停/限速等指令，快照立即作废。
    /// </summary>
    private const int ReadSnapshotMaxAgeMs = IdlePollIntervalMs + 500;

    /// <summary>空闲（没有下载中/排队任务）时的轮询间隔：退避以降低常驻开销。</summary>
    private const int IdlePollIntervalMs = 2500;
    /// <summary>infohash → (Gid, 任务名) 索引：每轮询重建，添加任务前做重复预检。</summary>
    private volatile ConcurrentDictionary<string, (string Gid, string Name)> _btHashIndex = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _startLock = new();
    private bool _started;
    /// <summary>最近一次分配的任务编号（时间戳，单调递增），时钟回拨/同毫秒并发时用它保证唯一。</summary>
    private long _lastTaskNumber;
    /// <summary>**运行期生效**的默认下载目录：设置里改了目录不必重启 aria2，下一批新任务就用新目录
    /// （第八轮 P1-1：以前只在全局 --dir 里用启动时的值，导致"改了目录但文件还在老地方"，
    /// 并且同名保护会去扫错的目录，配合全局 --allow-overwrite=true 直接覆盖旧文件）。</summary>
    private volatile string _defaultDir;
    /// <summary>运行期生效的默认线程数（split）：每次添加都显式下发，改设置不必重启引擎。</summary>
    private int _defaultConnections;
    /// <summary>运行期生效的最大并发任务数（changeGlobalOption 热更新，见 SetMaxConcurrentDownloadsAsync）。</summary>
    private int _maxConcurrentDownloads;

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
        _nameResolver = new RemoteNameResolver(_log);
        // 用持久化元数据里的最大编号初始化：系统时钟回拨后重启也不会发出重复编号
        _lastTaskNumber = _store.All().Select(m => m.TaskNumber).DefaultIfEmpty(0).Max();
        _defaultDir = config.DefaultDownloadDir;
        _defaultConnections = config.DefaultConnections;
        _maxConcurrentDownloads = config.MaxConcurrentDownloads;
        // 本地历史：aria2 不会把已完成任务带回列表（见 _history 注释），启动时直接从元数据恢复
        foreach (var m in _store.Finished())
            if (HistoryRow(m) is { } row) _history[row.Gid] = row;
        if (!_history.IsEmpty) _log($"本地历史已恢复 { _history.Count} 条已完成/失败记录");
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
        var dir = string.IsNullOrWhiteSpace(req.Directory) ? _defaultDir : req.Directory;
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
            ExtraOptions = req.ExtraOptions,
            AllowOverwriteExisting = req.AllowOverwriteExisting
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
                ExtraOptions = req.ExtraOptions,
                AllowOverwriteExisting = req.AllowOverwriteExisting
            };
        }
        return req;
    }

    #region 运行期默认值 / 本地历史 / 失败残留隔离

    /// <summary>当前生效的默认下载目录（可能是设置里改过的值，不一定是 aria2 启动时的 --dir）。</summary>
    public string DefaultDownloadDir => _defaultDir;
    /// <summary>当前生效的默认线程数。</summary>
    public int DefaultConnections => _defaultConnections;

    /// <summary>
    /// 设置变更后热更新运行期默认值（第八轮 P1-1）。aria2 的 --dir/--split 是启动时快照，
    /// 只改 settings.json 不重启的话，新任务仍会落到老目录里去；这里让引擎在**每次添加**时
    /// 显式补上当前默认值，改目录/线程数立刻对后续任务生效（已在下的任务保持原位）。
    /// </summary>
    public void UpdateRuntimeDefaults(string? defaultDownloadDir = null, int? defaultConnections = null)
    {
        if (!string.IsNullOrWhiteSpace(defaultDownloadDir) &&
            !string.Equals(defaultDownloadDir.Trim(), _defaultDir, StringComparison.Ordinal))
        {
            var dir = defaultDownloadDir.Trim();
            try
            {
                Directory.CreateDirectory(dir);
                _defaultDir = dir;
                _log($"默认下载目录已热更新为 {dir}（后续新任务生效，已存在的任务保持原目录）");
            }
            catch (Exception ex)
            {
                _log($"默认下载目录热更新失败（保持 {DefaultDownloadDir}）: {ex.Message}");
            }
        }
        if (defaultConnections is { } c && c > 0 && c != _defaultConnections)
        {
            _defaultConnections = c;
            _log($"默认线程数已热更新为 {c}");
        }
        InvalidateSnapshot();
    }

    /// <summary>热更新最大并发任务数（aria2 支持经 RPC 改这项，无需重启）。</summary>
    public async Task SetMaxConcurrentDownloadsAsync(int value, CancellationToken ct = default)
    {
        if (value <= 0 || value == _maxConcurrentDownloads) return;
        await _client.ChangeGlobalOptionAsync(
            new Dictionary<string, string> { ["max-concurrent-downloads"] = value.ToString() }, ct).ConfigureAwait(false);
        _maxConcurrentDownloads = value;
        _log($"最大并发任务数已热更新为 {value}");
    }

    /// <summary>
    /// 补齐"运行期默认值"：目录与线程数一律显式下发（不写就会回落到 aria2 启动时的 --dir/--split），
    /// 并按任务类型决定 allow-overwrite。BT/磁力必须允许覆盖（要复用目录里的同名文件做分片校验）；
    /// HTTP/FTP 一律 false，让 aria2 自动改名，绝不让重复下载静默覆盖用户已有文件。
    /// </summary>
    private NewTaskRequest WithRuntimeDefaults(NewTaskRequest req, bool isBt)
    {
        var dir = string.IsNullOrWhiteSpace(req.Directory) ? _defaultDir : req.Directory!.Trim();
        if (!string.IsNullOrEmpty(dir))
        {
            try { Directory.CreateDirectory(dir); }
            catch (Exception ex) { _log($"创建下载目录失败 {dir}: {ex.Message}"); }
        }
        return new NewTaskRequest
        {
            Urls = req.Urls,
            Directory = string.IsNullOrEmpty(dir) ? null : dir,
            FileName = req.FileName,
            // 线程数只对 HTTP/FTP 有意义（BT 的分片由协议决定，不该下发 split/min-split-size）
            Connections = req.Connections > 0 ? req.Connections : (isBt ? 0 : _defaultConnections),
            SpeedLimit = req.SpeedLimit,
            Referer = req.Referer,
            Headers = req.Headers,
            ExtraOptions = req.ExtraOptions,
            AllowOverwriteExisting = isBt
        };
    }

    /// <summary>由持久化元数据合成一行本地历史（aria2 里已经没有这个任务了）。无终态时返回 null。</summary>
    private static DownloadTaskInfo? HistoryRow(TaskMeta? m)
    {
        if (m?.FinishedAt == null) return null;
        var state = m.FinalState switch
        {
            "Completed" => TaskState.Completed,
            "Failed" => TaskState.Failed,
            _ => (TaskState?)null
        };
        if (state == null) return null;
        var name = !string.IsNullOrWhiteSpace(m.Name) ? m.Name
            : m.FilePath is { Length: > 0 } p ? Path.GetFileName(p)
            : m.Urls.FirstOrDefault() ?? m.Gid;
        return new DownloadTaskInfo
        {
            Gid = m.Gid,
            TaskNumber = m.TaskNumber,
            Name = name,
            Dir = m.Dir,
            FilePath = m.FilePath,
            Urls = m.Urls.ToList(),
            Referer = m.Referer,
            State = state.Value,
            TotalLength = m.TotalLength,
            CompletedLength = m.TotalLength > 0 ? Math.Min(m.CompletedLength, m.TotalLength) : m.CompletedLength,
            ErrorCode = m.ErrorCode,
            ErrorMessage = m.ErrorMessage,
            IsBt = m.IsBt,
            AddedAt = m.AddedAt,
            FinishedAt = m.FinishedAt
        };
    }

    /// <summary>把刚结束的实时任务快照成历史行（速度/连接数清零：历史行不再"下载中"）。</summary>
    private static DownloadTaskInfo HistorySnapshot(DownloadTaskInfo t) => new()
    {
        Gid = t.Gid,
        TaskNumber = t.TaskNumber,
        Name = t.Name,
        Dir = t.Dir,
        FilePath = t.FilePath,
        Urls = t.Urls.ToList(),
        Referer = t.Referer,
        State = t.State,
        TotalLength = t.TotalLength,
        CompletedLength = t.TotalLength > 0 ? Math.Min(t.CompletedLength, t.TotalLength) : t.CompletedLength,
        ErrorCode = t.ErrorCode,
        ErrorMessage = t.ErrorMessage,
        IsBt = t.IsBt,
        InfoHash = t.InfoHash,
        NumPieces = t.NumPieces,
        AddedAt = t.AddedAt,
        FinishedAt = t.FinishedAt
    };

    /// <summary>把本地历史并入任务列表：aria2 里还活着的同 gid 任务优先（历史只是兜底）。</summary>
    private List<DownloadTaskInfo> WithHistory(List<DownloadTaskInfo> live)
    {
        if (_history.IsEmpty) return live;
        var liveIds = new HashSet<string>(live.Select(t => t.Gid));
        var list = new List<DownloadTaskInfo>(live.Count + _history.Count);
        list.AddRange(live);
        foreach (var h in _history.Values)
            if (!liveIds.Contains(h.Gid)) list.Add(h);
        return list;
    }

    /// <summary>记录一条本地历史（任务结束、或隔离后改写路径时调用），并按上限修剪。</summary>
    private void RememberHistory(DownloadTaskInfo t)
    {
        _history[t.Gid] = HistorySnapshot(t);
        TrimHistory();
    }

    private void TrimHistory()
    {
        if (_history.Count <= MaxHistoryRows) return;
        var drop = _history.Values
            .OrderBy(t => t.FinishedAt ?? DateTime.MinValue)
            .Take(_history.Count - MaxHistoryRows)
            .ToList();
        foreach (var d in drop) _history.TryRemove(d.Gid, out _);
        var n = _store.RemoveMany(drop.Select(d => d.Gid));
        if (n > 0) _log($"本地历史超过 {MaxHistoryRows} 条，已丢弃最旧 {n} 条记录");
    }

    /// <summary>回收"aria2 里不存在、又没留下终态快照"的失效元数据（磁力元数据阶段、异常退出等残留）。
    /// 每个会话只做一次，且在首轮轮询之后，避免把刚添加、aria2 还没来得及上报的任务误删。
    /// 注意：有终态快照的元数据是本地历史的数据源，绝不能当成垃圾回收。</summary>
    private void PruneStaleMetasOnce(List<DownloadTaskInfo> live)
    {
        if (Interlocked.Exchange(ref _staleMetaPruned, 1) != 1)
        {
            try
            {
                var liveIds = new HashSet<string>(live.Select(t => t.Gid));
                var cutoff = DateTime.Now.AddMinutes(-10);
                var stale = _store.All()
                    .Where(m => m.FinishedAt == null && string.IsNullOrEmpty(m.FinalState)
                                && m.AddedAt < cutoff && !liveIds.Contains(m.Gid))
                    .Select(m => m.Gid)
                    .ToList();
                var n = _store.RemoveMany(stale);
                if (n > 0) _log($"回收失效任务元数据 {n} 条（aria2 中已不存在且无终态）");
            }
            catch (Exception ex) { _log($"回收失效元数据失败: {ex.Message}"); }
        }
    }

    /// <summary>
    /// aria2 里"续传做不到"的失败（第八轮 P2-3）：服务器不支持 Range 时，aria2 会把整包内容
    /// 重复写进同一个文件后报失败，留下**大小正确但内容损坏**的文件，看起来像下载成功。
    /// 判定以错误码为准：<c>8 = Invalid range header</c>（aria2 的 ER_INVALID_RANGE_HEADER）；
    /// 另附几条文案兜底（aria2 各版本措辞不同，且部分只填 errorReason 不填 errorMessage）。
    /// 只对这几条动手，普通网络中断的失败保留断点续传能力。
    /// </summary>
    public const int Aria2ErrorInvalidRangeHeader = 8;

    private static readonly string[] UnresumableFailureMarkers =
    {
        "Invalid range header",
        "Unable to continue download",
        "cannot be continued"
    };

    /// <summary>是否为"无法续传"类失败（可安全隔离残留）——按错误文案判断。</summary>
    public static bool IsUnresumableFailure(string? errorMessage) =>
        errorMessage != null && UnresumableFailureMarkers.Any(
            m => errorMessage.Contains(m, StringComparison.OrdinalIgnoreCase));

    /// <summary>是否为"无法续传"类失败：错误码 8，或文案命中上述几条。</summary>
    public static bool IsUnresumableFailure(DownloadTaskInfo t) =>
        t.ErrorCode == Aria2ErrorInvalidRangeHeader || IsUnresumableFailure(t.ErrorMessage);

    /// <summary>
    /// 把不可续传的失败残留改名为 <c>*.partial</c>，并清掉 aria2 侧的失败结果（第八轮 P2-3）。
    /// 为什么必须连结果一起清：失败任务仍留在 aria2 的 stopped 列表里，用户点"继续"时 aria2 会按
    /// 内存中"这些分片已下载"的状态往（此时已不存在的）文件里继续写，结果得到一个满是空洞的假完整文件——
    /// 比原来的损坏文件更糟。清掉结果后本地历史仍然保留这一行（含错误原因），用户可以正常"重新下载"。
    /// </summary>
    private async Task QuarantineUnresumableFailureAsync(DownloadTaskInfo t, CancellationToken ct)
    {
        var moved = 0;
        try
        {
            if (string.IsNullOrEmpty(t.FilePath) || !File.Exists(t.FilePath)) return;
            // aria2 上报的 path 可能带正斜杠（C:/Users/…）：先归一成 .NET 规范形式，
            // 隔离后的路径才会以统一写法进入本地历史（UI 的"打开文件夹"、重新下载都读它）
            t.FilePath = Path.GetFullPath(t.FilePath);
            var target = NextPartialPath(t.FilePath!);
            MoveWithSidecar(t.FilePath!, target);
            moved = 1;
            var old = t.FilePath;
            t.FilePath = target;
            t.Name = Path.GetFileName(target);
            _log($"失败任务残留已隔离为 {Path.GetFileName(target)}（原 {Path.GetFileName(old)}，原因：{t.ErrorMessage}）");
            _store.UpdateFinished(t); // 让历史行记住隔离后的路径
        }
        catch (Exception ex) { _log($"隔离失败残留异常: {ex.Message}"); return; }
        if (moved == 0) return;
        // 结果必须清掉（见方法注释）；清失败不影响隔离本身
        try { await _client.RemoveDownloadResultAsync(t.Gid, ct).ConfigureAwait(false); }
        catch (Exception ex) { _log($"清除不可续传失败结果异常 gid={t.Gid}: {ex.Message}"); }
    }

    /// <summary>目标残留的隔离名：x.bin → x.bin.partial；已被占用则 x.bin.partial.1…</summary>
    private static string NextPartialPath(string path)
    {
        var first = path + ".partial";
        if (!File.Exists(first)) return first;
        for (var i = 1; i < 1000; i++)
        {
            var cand = $"{first}.{i}";
            if (!File.Exists(cand)) return cand;
        }
        return $"{path}.{DateTime.Now:yyyyMMddHHmmssfff}.partial";
    }

    /// <summary>连同 aria2 的 .aria2 断点控制文件一起改名：留着它会让 aria2 以为旧文件还能续传。</summary>
    private static void MoveWithSidecar(string path, string target)
    {
        File.Move(path, target, overwrite: false);
        var sidecar = path + ".aria2";
        if (File.Exists(sidecar)) File.Move(sidecar, target + ".aria2", overwrite: false);
    }

    #endregion

    /// <summary>轮询诊断指纹（L-8；第四轮 N-2 把它从轮询循环里抽出来以便单测）：
    /// 将"每个任务的 gid + 状态"与"停止任务数"折叠成一个 FNV-32 整数，
    /// 用于判断这一轮与上一轮是否值得再写一条诊断日志（无字符串分配）。
    /// 语义要求：内容相同 → 指纹相同；任一 gid 增减、状态变化、或 NumStopped 变化 → 指纹变化。
    /// 只服务于诊断日志，不参与任何业务判定；FNV-32 理论上可能碰撞，后果仅是少写一条日志。
    /// （FNV-64 的基准常量超出 long.MaxValue，故取 32 位。）</summary>
    public static int ComputePollDiagFingerprint(IEnumerable<DownloadTaskInfo> tasks, GlobalStat stats)
    {
        var sig = unchecked((int)2166136261u); // FNV-32 offset basis（超出 int.MaxValue，取低 32 位）
        foreach (var t in tasks)
        {
            sig = unchecked(sig * 16777619 + t.Gid.GetHashCode()); // FNV-32 prime
            sig = unchecked(sig * 16777619 + (int)t.State);
        }
        return unchecked(sig * 16777619 + stats.NumStopped);
    }

    /// <summary>启动 aria2 子进程并拉起轮询循环。重复调用安全（_startLock + _started）。</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        lock (_startLock)
        {
            if (_started) return;
            _started = true;
        }
        _process.Start(FinishedSessionEntries());
        await _process.WaitForReadyAsync(_client, ct: ct).ConfigureAwait(false);
        _pollCts = new CancellationTokenSource();
        _pollTask = Task.Run(() => PollLoopAsync(_pollCts.Token), CancellationToken.None);
        _log("下载引擎已启动");
    }

    /// <summary>
    /// 客户端确认"早已下载完成、且文件还在"的条目：这些留在 aria2 会话文件里的记录一旦被
    /// --input-file 读回去，就会变成一个进度归零的新任务（新 gid、排队中 0 B），看起来像历史、
    /// 实际可能把文件重下一遍。判据保守：只认非 BT、终态 Completed、且落盘文件仍然存在。
    /// </summary>
    private ICollection<(string Url, string? Dir)>? FinishedSessionEntries()
    {
        try
        {
            var list = new List<(string Url, string? Dir)>();
            foreach (var m in _store.Finished())
            {
                if (m.FinalState != "Completed" || m.IsBt) continue;
                if (string.IsNullOrEmpty(m.FilePath) || !File.Exists(m.FilePath)) continue;
                foreach (var u in m.Urls)
                    if (!string.IsNullOrWhiteSpace(u)) list.Add((u, m.Dir));
            }
            return list.Count > 0 ? list : null;
        }
        catch (Exception ex)
        {
            _log($"扫描会话文件中可复活的已完成任务失败: {ex.Message}");
            return null;
        }
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var idle = false;
            // 第十轮：aria2 子进程可能崩溃或被外部杀掉（实测一旦掉线，之后每次添加都变成 HTTP 500 且永不恢复）。
            // 轮询循环兼任看门狗：发现子进程不在了就自愈重启，本轮跳过。
            if (!_process.IsRunning)
            {
                try { await RestartEngineIfNeededAsync("轮询看门狗检测到 aria2 子进程已退出", ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { _log($"引擎看门狗重启失败: {ex.Message}"); }
            }
            try
            {
                // 强类型 multicall（文档态）：整轮响应只解析一次，不建 JsonNode、不产生 LOH 字符串。
                // 1000 任务时实测每轮分配 3,995 KB → 1,445 KB（详见测试报告 §12-B 表 1）。
                using var call = await _client.MultiCallBufferedAsync(new[]
                {
                    ("aria2.tellActive", Array.Empty<object?>()),
                    ("aria2.tellWaiting", new object?[] { 0, 1000 }),
                    ("aria2.tellStopped", new object?[] { 0, 1000 }),
                    ("aria2.getGlobalStat", Array.Empty<object?>())
                }, ct).ConfigureAwait(false);

                var raws = new List<Aria2TaskStatus>();
                for (var i = 0; i < Math.Min(3, call.Count); i++)
                    raws.AddRange(call.DeserializeList<Aria2TaskStatus>(i));

                // 磁力"幻影行"校验：确认最近添加的磁力是否真被 aria2 注册（必须在 followedBy 处理之前）。
                ResolvePendingMagnetAdds(raws);

                // 兜底处理：预检存在时间窗（元数据阶段哈希未知），漏网的重复添加会以 "already registered"
                // 错误任务的形式出现。清理掉 aria2 里的噪音结果，同时把这一行标成失败并写明中文原因——
                // 修复前它是直接删行，用户在界面上既看不到任务也看不到"为什么没加上"。
                var dupFailGids = raws
                    .Where(r => r.Status == "error" &&
                                r.ErrorMessage?.Contains("already registered", StringComparison.OrdinalIgnoreCase) == true)
                    .Select(r => r.Gid).ToHashSet();
                if (dupFailGids.Count > 0)
                {
                    foreach (var g in dupFailGids)
                    {
                        _log($"磁力/种子添加被引擎以 already registered 错误拒绝: gid={g}");
                        try { await _client.RemoveDownloadResultAsync(g, ct).ConfigureAwait(false); } catch { }
                        var dupMeta = _store.GetMeta(g);
                        FailDuplicateBtAdd(g,
                            dupMeta?.SourceMagnet ?? dupMeta?.Urls.FirstOrDefault(u => u.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)),
                            dupMeta?.TaskNumber ?? NextTaskNumber());
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
                var gstat = call.Count > 3 ? call.Deserialize<Aria2GlobalStat>(3) : null;
                var stats = new GlobalStat
                {
                    DownloadSpeed = gstat?.DownloadSpeed ?? 0,
                    UploadSpeed = gstat?.UploadSpeed ?? 0,
                    NumActive = gstat?.NumActive ?? 0,
                    NumWaiting = gstat?.NumWaiting ?? 0,
                    NumStopped = gstat?.NumStopped ?? 0
                };

                // 诊断打点：任务集合或统计变化时记录一次（空轮询静默）。
                // 每轮比较用无分配的整数指纹（见 ComputePollDiagFingerprint）；
                // 只有真的检测到变化才拼完整签名字符串用于日志——原先每轮都拼，
                // 1000 任务时每轮产生数十 KB 字符串垃圾（每秒一次）。
                var sig = ComputePollDiagFingerprint(tasks, stats);
                if (sig != _lastPollSig)
                {
                    _lastPollSig = sig;
                    if (stats.NumActive + stats.NumWaiting + stats.NumStopped > 0)
                    {
                        var taskSig = string.Join(",", tasks.Select(t => $"{t.Gid[..Math.Min(8, t.Gid.Length)]}:{t.State}"));
                        _log($"轮询 active={stats.NumActive} waiting={stats.NumWaiting} stopped={stats.NumStopped} 任务=[{taskSig}]");
                    }
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
                        // 不可续传的失败：把"看着完整其实损坏"的残留隔离成 *.partial 并清掉 aria2 结果（P2-3）
                        if (t.State == TaskState.Failed && !t.IsBt && IsUnresumableFailure(t))
                            await QuarantineUnresumableFailureAsync(t, ct).ConfigureAwait(false);
                        // 本地历史：aria2 之后可能再也没了这个任务（重启、或上一步清结果），
                        // 先把终态留档，列表与 UI 才不会"下载完成却凭空消失"（P2-4）
                        if (t.State is TaskState.Completed or TaskState.Failed)
                            RememberHistory(t);
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

                // 发布快照：UI 与重复预检后续都从这里取，不再各自 RPC。
                // 统计与列表出自同一轮，故共用发布时间戳（读接口据此判定两者新鲜度）。
                // 列表并入本地历史：aria2 不会把已完成任务写进 --save-session（实测会话文件 0 字节），
                // 重启后这些行只能由客户端补回，否则 UI 与 GET /api/tasks 会一起"失忆"（P2-4）。
                PruneStaleMetasOnce(tasks);
                var published = WithHistory(tasks);
                _latestSnapshot = published;
                _latestStats = stats;
                Volatile.Write(ref _latestSnapshotTicks, DateTime.Now.Ticks);

                // 状态字典清理：消失的任务（被删除/被清理）不该一直留在内存里
                if (_lastStates.Count > tasks.Count)
                {
                    var alive = new HashSet<string>(tasks.Select(t => t.Gid));
                    foreach (var gid in _lastStates.Keys)
                        if (!alive.Contains(gid)) _lastStates.TryRemove(gid, out _);
                }

                EngineEvent?.Invoke(this, new EngineEventArgs { Type = "StatsUpdated", Stats = stats, Tasks = published });

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
            // 续传失败这类错误 aria2 只填 errorReason（errorMessage 为空），兜底后 UI/历史才有得解释"为什么失败"
            ErrorMessage = string.IsNullOrEmpty(raw.ErrorMessage) ? raw.ErrorReason : raw.ErrorMessage,
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

    /// <summary>登记一次磁力添加的存活校验（见 <see cref="_pendingMagnetAdds"/>）。</summary>
    private void TrackMagnetLiveness(string gid, string magnet, long taskNumber)
        => _pendingMagnetAdds[gid] = new PendingMagnetAdd(
            magnet, taskNumber, DateTime.Now.AddMilliseconds(MagnetLivenessTimeoutMs));

    /// <summary>
    /// 校验待确认的磁力添加：出现在本轮（或此前任一轮）tell* 结果里 = aria2 真的注册了它，解除跟踪；
    /// 超过 <see cref="MagnetLivenessTimeoutMs"/> 仍未出现 = 被静默丢弃（同 infohash 已注册），标失败。
    /// 必须在 followedBy 处理**之前**调用：元数据任务一旦被消费，它的 gid 就会从 aria2 列表里消失。
    /// </summary>
    private void ResolvePendingMagnetAdds(List<Aria2TaskStatus> raws)
    {
        if (_pendingMagnetAdds.IsEmpty) return;
        var seen = new HashSet<string>(raws.Select(r => r.Gid), StringComparer.OrdinalIgnoreCase);
        foreach (var (gid, pending) in _pendingMagnetAdds)
        {
            if (seen.Contains(gid))
            {
                _pendingMagnetAdds.TryRemove(gid, out _);
                continue;
            }
            if (DateTime.Now < pending.Deadline) continue;
            if (!_pendingMagnetAdds.TryRemove(gid, out _)) continue;
            _log($"添加 {MagnetLivenessTimeoutMs}ms 后仍未出现在 tell* 中（引擎静默丢弃）: gid={gid} {pending.Magnet}");
            FailDuplicateBtAdd(gid, pending.Magnet, pending.TaskNumber);
        }
    }

    /// <summary>
    /// 把"同 infohash 已在引擎注册"的重复添加标成失败（写本地历史 + 推事件），原因用中文写给用户。
    /// 两条路径都汇到这里：
    ///  1. aria2 直接生成 "already registered" 错误任务（<paramref name="gid"/> 出现在 tellStopped）；
    ///  2. aria2 **静默丢弃** addUri，gid 在 tell* 里查无此人（由 <see cref="ResolvePendingMagnetAdds"/> 判定）。
    /// 修复前这两条分别表现为"行悄悄消失"与"界面留一条永不报错的 0% 幻影行"（测试报告 §16-G #11）。
    /// </summary>
    private void FailDuplicateBtAdd(string gid, string? source, long taskNumber)
    {
        var isMagnet = source?.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) == true;
        var reason = $"同一{(isMagnet ? "磁力" : "种子")}已在下载或做种中：引擎未注册本次添加，无需重复添加";
        _log($"重复 BT 任务未被引擎注册，判定为重复添加: gid={gid} {source}");
        var meta = _store.GetMeta(gid);
        var info = new DownloadTaskInfo
        {
            Gid = gid,
            TaskNumber = taskNumber,
            Name = string.IsNullOrWhiteSpace(meta?.Name) ? (source ?? gid) : meta!.Name,
            Urls = meta is { Urls.Count: > 0 } ? meta.Urls.ToList() : new List<string> { source ?? gid },
            State = TaskState.Failed,
            IsBt = true,
            Dir = meta?.Dir,
            ErrorMessage = reason,
            AddedAt = meta?.AddedAt ?? DateTime.Now,
            FinishedAt = DateTime.Now
        };
        try { _store.UpdateFinished(info); }
        catch (Exception ex) { _log($"写入失效 BT 任务终态失败: {ex.Message}"); }
        RememberHistory(info);   // 本地历史：列表与 API 都会带上这一行（失败原因在 errorMessage）
        InvalidateSnapshot();
        EngineEvent?.Invoke(this, new EngineEventArgs { Type = "TaskChanged", Task = info });
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
        // 这里必须走**实时**列表：快照路径（GetAllTasksAsync）允许 ReadSnapshotMaxAgeMs（3s）年龄，
        // 比判重窗口更宽，否则 1.2~3s 之间的旧快照会被二次命中，正是要避免的"拿过旧数据判重"。
        var all = TryGetRecentSnapshot(1200) ?? await GetAllTasksLiveAsync(ct).ConfigureAwait(false);
        return all
            .Where(t => t.State is not (TaskState.Completed or TaskState.Failed or TaskState.Removed))
            .Where(t => t.Urls.Any(u => targets.Contains(u)))
            .ToList();
    }

    /// <summary>
    /// 没有显式文件名时，先用一次 1 字节请求把响应头里的真实名字解出来，再作为 out 下发。
    /// 只在 <see cref="RemoteNameResolver.ShouldPreresolve"/> 认可的链接上做；解析失败原样返回，
    /// 行为与修复前一致（详见 <see cref="RemoteNameResolver"/> 与测试报告 §16）。
    /// </summary>
    private async Task<NewTaskRequest> WithPreresolvedFileNameAsync(NewTaskRequest req, CancellationToken ct)
    {
        var url = req.Urls.FirstOrDefault(u => !u.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase));
        if (!RemoteNameResolver.ShouldPreresolve(url, req.FileName) || url == null) return req;
        string? name;
        try
        {
            name = await _nameResolver.ResolveAsync(url, req.Referer, req.Headers, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log($"文件名预解析异常（忽略，交给 aria2）: {ex.Message}");
            return req;
        }
        if (string.IsNullOrEmpty(name)) return req;
        _log($"预解析得到文件名「{name}」，将作为 out 下发（原先会落回 URL 末段或由 aria2 解析）");
        return req.WithFileName(name);
    }

    /// <summary>
    /// aria2 子进程可能因外部杀进程、崩溃或端口被占而消失（第八轮之前的实测：一旦掉线，
    /// 之后每次添加都会把 SocketException 直接抛成 HTTP 500，且永不恢复）。
    /// 这里在添加前/失败后做一次自愈重启，重启仍失败才抛出带中文说明的异常。
    /// </summary>
    private async Task RestartEngineIfNeededAsync(string reason, CancellationToken ct = default)
    {
        await _engineRestartLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_process.IsRunning)
            {
                // 进程还在但 RPC 连不上：多半端口未就绪或 RPC 未监听，交由上层报错，不重复重启
                _log($"引擎重启跳过（aria2 进程仍在运行）：{reason}");
                return;
            }
            _log($"检测到下载引擎已退出，正在自动重启：{reason}");
            _lastStates.Clear();
            // 重启后 gid 全部失效：待校验的磁力添加不可能再出现在 tell* 里，不清掉会误判成"被静默丢弃"
            _pendingMagnetAdds.Clear();
            _process.Start(FinishedSessionEntries());
            await _process.WaitForReadyAsync(_client, 8000, ct).ConfigureAwait(false);
            _log("下载引擎已自动重启");
        }
        catch (Exception ex)
        {
            _log($"下载引擎自动重启失败: {ex.Message}");
            throw;
        }
        finally
        {
            _engineRestartLock.Release();
        }
    }

    /// <summary>是否为"连不上 aria2 RPC"这类可自愈的传输层异常（区别于参数错误、磁盘错误等业务异常）。</summary>
    private static bool IsEngineUnreachable(Exception ex) =>
        ex is System.Net.Http.HttpRequestException or System.Net.Sockets.SocketException or IOException
             || ex.Message.Contains("积极拒绝", StringComparison.Ordinal)
             || ex.Message.Contains("refused", StringComparison.OrdinalIgnoreCase)
             || ex.Message.Contains("连接", StringComparison.Ordinal)
             || ex.Message.Contains("Unable to connect", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 引擎自愈守卫（第十一轮把守卫从"只有 addUri 路径"上移到所有 RPC 调用点）：
    /// 传输层异常（掉线/被杀/端口未就绪）时先重启引擎再重试**一次**，仍失败则抛中文说明的异常。
    /// 原先暂停/恢复/删除遇到引擎掉线会把 SocketException 直接抛到 HTTP 层（测试报告 §16-H 遗留项）。
    /// </summary>
    /// <param name="action">动作的中文名，用于日志与错误文案（如"暂停任务"）。</param>
    /// <param name="op">实际的 RPC 调用；同一次调用会被执行最多两次（重试时用同一个委托）。</param>
    private async Task<T> WithEngineGuardAsync<T>(string action, Func<CancellationToken, Task<T>> op, CancellationToken ct)
    {
        try
        {
            return await op(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsEngineUnreachable(ex) && !ct.IsCancellationRequested)
        {
            try
            {
                await RestartEngineIfNeededAsync(ex.Message).ConfigureAwait(false);
            }
            catch (Exception restartEx)
            {
                throw new InvalidOperationException($"下载引擎不可用且自动重启失败：{restartEx.Message}", restartEx);
            }
            try
            {
                var result = await op(ct).ConfigureAwait(false);
                _log($"引擎重启后重试{action}成功");
                return result;
            }
            catch (Exception retryEx)
            {
                throw new InvalidOperationException($"下载引擎不可用，重启后仍无法{action}：{retryEx.Message}", retryEx);
            }
        }
    }

    /// <summary>无返回值版本的引擎自愈守卫。</summary>
    private async Task WithEngineGuardAsync(string action, Func<CancellationToken, Task> op, CancellationToken ct)
        => await WithEngineGuardAsync<object?>(action, async c =>
        {
            await op(c).ConfigureAwait(false);
            return null;
        }, ct).ConfigureAwait(false);

    /// <summary>带自愈的 addUri：连接类异常先重启引擎再重试一次，仍失败则换成能看懂的中文错误。</summary>
    private Task<string> AddUriWithGuardAsync(NewTaskRequest req, CancellationToken ct)
        => WithEngineGuardAsync("添加任务", c => _client.AddUriAsync(req, c), ct);

    /// <summary>批量版的自愈 addUri（与 <see cref="AddUriWithGuardAsync"/> 同一策略）。</summary>
    private Task<List<string>> AddUriBatchWithGuardAsync(List<NewTaskRequest> reqs, CancellationToken ct)
        => WithEngineGuardAsync("批量添加任务", c => _client.AddUriBatchAsync(reqs, c), ct);

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
            // 第十轮：URL 末段是 token/编号时先预解析响应头拿到真名（见 WithPreresolvedFileNameAsync）
            req = await WithPreresolvedFileNameAsync(req, ct).ConfigureAwait(false);
            // 先剔除"只是把 URL 末段重复一遍"的伪文件名，再做重名冲突处理
            req = DropUrlDerivedFileName(req);
            // 相同文件已存在时自动重命名（追加编号），新任务不覆盖历史下载
            req = ApplyUniqueFileName(req);
        }
        // 目录/线程数一律按**当前设置**显式下发，allow-overwrite 按任务类型定：
        // 见 WithRuntimeDefaults（第八轮 P1-1 / P1-2）
        req = WithRuntimeDefaults(req, isMagnet);
        var taskNumber = NextTaskNumber();
        var gid = await AddUriWithGuardAsync(req, ct).ConfigureAwait(false);
        _log($"addUri 成功 gid={gid} 编号={taskNumber} 磁力={isMagnet}");
        if (!isMagnet) _newTaskGids[gid] = 0; // 磁力首 gid 是元数据任务，不弹进度窗；真实任务由 followedBy 阶段标记
        // 显式重新添加视为撤销墓碑，保证之后可以正常恢复/续传
        _tombstones.Unmark(req.Urls, req.Directory ?? _defaultDir);
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
        // 磁力：登记存活校验。aria2 对"同 infohash 已注册"的 addUri 会收下 gid 后静默丢弃，
        // 该 gid 在 tell* 里查无此人，只能靠轮询确认（见 ResolvePendingMagnetAdds）。
        if (isMagnet)
            TrackMagnetLiveness(gid,
                req.Urls.First(u => u.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)), taskNumber);
        Aria2TaskStatus? status;
        try { status = await _client.TellStatusAsync(gid, ct).ConfigureAwait(false); }
        catch (Aria2RpcException) when (isMagnet)
        {
            // 已被静默丢弃：tellStatus 报 "No such download"。不在这里抛错，
            // 让轮询的存活校验统一把它标成"同一磁力已在下载/做种中"，用户才看得到原因。
            status = null;
        }
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
        // BT 必须 allow-overwrite=true：种子要复用目录里的同名文件做分片校验
        req2 = WithRuntimeDefaults(req2, isBt: true);
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
            var isMagnet = list[i].Urls.Any(u => u.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase));
            if (!isMagnet)
            {
                // 与单任务路径一致：URL 末段不可用时先预解析响应头拿真名
                list[i] = await WithPreresolvedFileNameAsync(list[i], ct).ConfigureAwait(false);
                // 与单任务路径一致：先剔除"只是把 URL 末段重复一遍"的伪文件名，再做重名冲突处理
                list[i] = DropUrlDerivedFileName(list[i]);
                list[i] = ApplyUniqueFileName(list[i]);
            }
            // 与单任务路径一致：目录/线程数按当前设置显式下发，allow-overwrite 按任务类型定
            list[i] = WithRuntimeDefaults(list[i], isMagnet);
        }
        var numbers = list.Select(_ => NextTaskNumber()).ToList();
        var gids = await AddUriBatchWithGuardAsync(list, ct).ConfigureAwait(false);
        var results = new List<DownloadTaskInfo>();
        for (var i = 0; i < gids.Count && i < list.Count; i++)
        {
            if (string.IsNullOrEmpty(gids[i])) continue;
            if (!list[i].Urls.Any(u => u.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)))
                _newTaskGids[gids[i]] = 0; // 磁力首 gid 是元数据任务，不弹进度窗
            // 显式重新添加视为撤销墓碑，保证之后可以正常恢复/续传
            _tombstones.Unmark(list[i].Urls, list[i].Directory ?? _defaultDir);
            _store.AddMeta(gids[i], new TaskMeta
            {
                Gid = gids[i],
                TaskNumber = numbers[i],
                // 与单任务路径一致：未指定文件名时用 URL 末段作列表显示占位（aria2 上报真实路径后更新）
                Name = list[i].FileName ?? GuessFileNameFromUrl(list[i].Urls.FirstOrDefault()) ?? string.Empty,
                Urls = list[i].Urls,
                Referer = list[i].Referer,
                AddedAt = DateTime.Now
            });
            // 与单任务路径一致：磁力登记存活校验（见 ResolvePendingMagnetAdds）
            var magnetUrl = list[i].Urls.FirstOrDefault(u => u.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase));
            if (magnetUrl != null) TrackMagnetLiveness(gids[i], magnetUrl, numbers[i]);
            results.Add(new DownloadTaskInfo { Gid = gids[i], TaskNumber = numbers[i], Name = list[i].FileName ?? list[i].Urls.First() });
        }
        InvalidateSnapshot();
        return results;
    }

    // 暂停/继续之后立即作废快照：读接口现在会优先复用快照（见 GetAllTasksAsync），
    // 不作废就会让"扩展刚发指令就立刻回查"拿到指令前的旧状态。
    public async Task PauseAsync(string gid, CancellationToken ct = default)
    {
        await WithEngineGuardAsync("暂停任务", c => _client.PauseAsync(gid, c), ct).ConfigureAwait(false);
        InvalidateSnapshot();
    }

    public async Task ResumeAsync(string gid, CancellationToken ct = default)
    {
        await WithEngineGuardAsync("继续任务", c => _client.UnpauseAsync(gid, c), ct).ConfigureAwait(false);
        InvalidateSnapshot();
    }

    public async Task PauseAllAsync(CancellationToken ct = default)
    {
        await WithEngineGuardAsync("暂停全部任务", c => _client.PauseAllAsync(c), ct).ConfigureAwait(false);
        InvalidateSnapshot();
    }

    public async Task ResumeAllAsync(CancellationToken ct = default)
    {
        await WithEngineGuardAsync("继续全部任务", c => _client.UnpauseAllAsync(c), ct).ConfigureAwait(false);
        InvalidateSnapshot();
    }

    /// <summary>删除任务。对已完成/失败的任务调用 removeDownloadResult 清理。</summary>
    public async Task RemoveAsync(string gid, bool deleteFile = false, CancellationToken ct = default)
    {
        Aria2TaskStatus? status = null;
        try { status = await _client.TellStatusAsync(gid, ct).ConfigureAwait(false); } catch { }

        // 登记删除墓碑：会话文件每 10 秒落盘，异常退出时已删任务仍留在其中，
        // 下次启动 --input-file 会把任务复活；记下 URL+目录哈希，启动前过滤
        var meta = _store.GetMeta(gid);
        try
        {
            var tombUrls = new List<string>();
            if (status?.Files != null)
                tombUrls.AddRange(status.Files
                    .SelectMany(f => f.Uris ?? Enumerable.Empty<Aria2Uri>())
                    .Select(u => u.Uri)
                    .Where(u => !string.IsNullOrWhiteSpace(u)));
            if (meta != null)
                tombUrls.AddRange(meta.Urls.Where(u => !string.IsNullOrWhiteSpace(u)));
            // 磁力任务以来源磁力链接登记墓碑（会话文件中保存的就是磁力 URI）
            if (meta?.SourceMagnet != null) tombUrls.Add(meta.SourceMagnet);
            if (tombUrls.Count > 0)
                _tombstones.Mark(tombUrls.Distinct(), status?.Dir ?? meta?.Dir ?? _defaultDir);
        }
        catch (Exception ex) { _log($"登记删除墓碑失败: {ex.Message}"); }

        try
        {
            if (status?.Status is "active" or "waiting" or "paused")
            {
                await WithEngineGuardAsync("删除任务", c => _client.RemoveAsync(gid, c), ct).ConfigureAwait(false);
                // aria2.remove 只把任务转入 stopped 结果（做种中的完成任务会残留为 complete），
                // 稍候重试清除该结果，确保列表行消失；失败次数用尽则交由下次手动删除
                for (var i = 0; i < 6; i++)
                {
                    try { await Task.Delay(250, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
                    try
                    {
                        await WithEngineGuardAsync("清理任务结果", c => _client.RemoveDownloadResultAsync(gid, c), ct).ConfigureAwait(false);
                        break;
                    }
                    catch (Aria2RpcException) { /* 仍在转移为 stopped 结果，重试 */ }
                    catch (Exception) { break; } // 引擎不可用（守卫已尝试重启）：不再重试，交由轮询看门狗
                }
            }
            else
                await WithEngineGuardAsync("清理任务结果", c => _client.RemoveDownloadResultAsync(gid, c), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is Aria2RpcException or InvalidOperationException)
        {
            // 任务可能已消失（Aria2RpcException），或引擎掉线且自愈重启也失败（InvalidOperationException）。
            // 两种情况都不该挡住下面的本地清理：用户要的结果是"这一行消失"。
            _log($"删除任务时引擎未成功执行（继续本地清理）: {ex.Message}");
        }

        if (deleteFile)
        {
            // aria2 已经没有这个任务的记录时（本地历史行、或结果已被清除过），
            // 只能按元数据里记住的落盘路径删——否则"删除并删除文件"会静默留下文件
            var paths = status?.Files?.Select(f => f.Path)
                        ?? (string.IsNullOrEmpty(meta?.FilePath) ? null : new[] { meta!.FilePath });
            if (paths != null)
            {
                foreach (var p in paths)
                {
                    try { if (!string.IsNullOrEmpty(p) && File.Exists(p)) File.Delete(p); }
                    catch (Exception ex) { _log($"删除文件失败 {p}: {ex.Message}"); }
                }
            }
        }
        _store.RemoveMeta(gid);
        _history.TryRemove(gid, out _); // 本地历史同步遗忘：删除后不该在下一轮又被补回列表
        _lastStates.TryRemove(gid, out _);
        InvalidateSnapshot(); // 集合已变：避免"刚删掉又立刻重下"被判成重复任务而拒收
    }

    /// <summary>重新下载：用原任务的 URL 与参数新建任务。
    /// aria2 已经不记得这个 gid 时（本地历史行、失败结果被清除后）回落到本地留档，
    /// 让用户至少还能"重新下载"——本地历史若没有这条兜底，失败任务清掉 aria2 结果后就再也点不动了。</summary>
    public async Task<DownloadTaskInfo> RedownloadAsync(string gid, CancellationToken ct = default)
    {
        // aria2 可能已经不认识这个 gid（失败结果被清、或重启后本地历史行）：
        // tellStatus 对未知 gid 是 RPC 错误而不是 null，必须接住再回落
        DownloadTaskInfo? info;
        try { info = await GetTaskAsync(gid, ct).ConfigureAwait(false); }
        catch (Aria2RpcException) { info = null; }
        catch (Exception ex) when (IsEngineUnreachable(ex) && !ct.IsCancellationRequested)
        {
            // 引擎掉线（传输层异常）：先自愈重启再读一次；仍读不到就回落本地历史。
            // 真正的添加动作在末尾，由 AddTaskAsync 的守卫兜底。
            try
            {
                await RestartEngineIfNeededAsync(ex.Message).ConfigureAwait(false);
                try { info = await GetTaskAsync(gid, ct).ConfigureAwait(false); }
                catch (Aria2RpcException) { info = null; }
            }
            catch (Exception restartEx)
            {
                _log($"重新下载前读取任务失败且引擎自愈未成功，回落本地历史：{restartEx.Message}");
                info = null;
            }
        }
        info ??= HistoryFallback(gid);
        if (info == null) throw new InvalidOperationException($"任务 {gid} 不存在");
        if (info.Urls.Count == 0) throw new InvalidOperationException("任务没有可用的下载链接");
        var req = new NewTaskRequest
        {
            Urls = info.Urls,
            Directory = info.Dir,
            // 隔离过的残留（x.bin.partial）重下时应落回原名 x.bin，而不是又下一个 .partial
            FileName = StripPartialSuffix(info.Name),
            Connections = info.Split > 0 ? info.Split : _defaultConnections,
            SpeedLimit = info.SpeedLimit,
            Referer = info.Referer
        };
        return await AddTaskAsync(req, ct).ConfigureAwait(false);
    }

    /// <summary>从本地历史/元数据兜底还原一个 aria2 已不认识的 gid。</summary>
    private DownloadTaskInfo? HistoryFallback(string gid) =>
        _history.TryGetValue(gid, out var row) ? row : HistoryRow(_store.GetMeta(gid));

    /// <summary>去掉隔离后缀：x.bin.partial[.N] → x.bin。</summary>
    private static string? StripPartialSuffix(string? name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        var idx = name!.IndexOf(".partial", StringComparison.Ordinal);
        if (idx <= 0) return name;
        var suffix = name.Substring(idx + ".partial".Length);
        // 只认 ".partial" / ".partial.数字"，避免把真叫 "a.partial notes.txt" 的名字改坏
        if (suffix.Length == 0 || (suffix[0] == '.' && suffix.Skip(1).All(char.IsDigit)))
            return name.Substring(0, idx);
        return name;
    }

    public async Task<DownloadTaskInfo?> GetTaskAsync(string gid, CancellationToken ct = default)
    {
        var status = await _client.TellStatusAsync(gid, ct).ConfigureAwait(false);
        return status != null ? ToTaskInfo(status) : null;
    }

    /// <summary>
    /// 取全部任务。**优先复用最近一轮轮询快照**（不超过 <see cref="ReadSnapshotMaxAgeMs"/> 毫秒、
    /// 且本地未发生任务增删时）：浏览器扩展的 GET /api/tasks 由此不再自己打一轮 multicall
    /// （1000 任务规模下那一轮是 458 KB 响应 + 约 30 ms 解析）。
    /// 快照不可用（还没轮询过 / 太旧 / 刚增删过任务）时退回实时 RPC，语义与旧版一致。
    /// </summary>
    public async Task<List<DownloadTaskInfo>> GetAllTasksAsync(CancellationToken ct = default)
    {
        if (TryGetRecentSnapshot(ReadSnapshotMaxAgeMs) is { } snapshot)
            return OrderTasks(new List<DownloadTaskInfo>(snapshot));
        return OrderTasks(WithHistory(await GetAllTasksLiveAsync(ct).ConfigureAwait(false)));
    }

    /// <summary>实时向 aria2 拉一次全量列表（不走快照）：判重预检等需要"此刻真实状态"的调用用它。</summary>
    private async Task<List<DownloadTaskInfo>> GetAllTasksLiveAsync(CancellationToken ct = default)
    {
        var (active, waiting, stopped) = await _client.TellAllAsync(ct: ct).ConfigureAwait(false);
        return active.Concat(waiting).Concat(stopped).Select(ToTaskInfo).ToList();
    }

    /// <summary>统一排序：已完成/失败优先、按添加时间倒序。快照路径与实时路径共用，保证顺序一致。</summary>
    private static List<DownloadTaskInfo> OrderTasks(List<DownloadTaskInfo> all)
        => all.OrderByDescending(t => t.State is TaskState.Completed or TaskState.Failed ? 1 : 0)
              .ThenByDescending(t => t.AddedAt).ToList();

    /// <summary>全局统计。同样优先复用轮询快照里的那一份（与列表同一轮产生）。</summary>
    public async Task<GlobalStat> GetGlobalStatAsync(CancellationToken ct = default)
    {
        if (TryGetRecentStats(ReadSnapshotMaxAgeMs) is { } stats) return stats;
        return await _client.GetGlobalStatAsync(ct).ConfigureAwait(false);
    }

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

    /// <summary>与 TryGetRecentSnapshot 同一轮、同一时间戳发布的全局统计。</summary>
    private GlobalStat? TryGetRecentStats(int maxAgeMs)
    {
        var published = Volatile.Read(ref _latestSnapshotTicks);
        if (published == 0) return null;
        return (DateTime.Now.Ticks - published) <= maxAgeMs * TimeSpan.TicksPerMillisecond ? _latestStats : null;
    }

    /// <summary>
    /// 让快照立即失效（下一次读会退回实时 RPC，并由下一轮轮询重新发布）。
    /// 必须在这些时机做：
    ///  - 本地新增/删除任务后：否则"刚添加完立刻再发一次同样的链接"这种重复预检会拿着**添加之前**的快照去判重，
    ///    漏判成"不是重复"从而真的建出第二个任务（测试 重复链接返回duplicate并跳过添加 抓到的正是这个）；
    ///  - 暂停/继续/限速等改状态的指令之后：读接口现在优先复用快照（见 GetAllTasksAsync），
    ///    不作废就会让调用方拿到指令发出前的旧状态。
    /// </summary>
    private void InvalidateSnapshot() => Volatile.Write(ref _latestSnapshotTicks, 0);

    public async Task SetGlobalSpeedLimitAsync(long bytesPerSec, CancellationToken ct = default)
    {
        await _client.SetGlobalSpeedLimitAsync(bytesPerSec, ct).ConfigureAwait(false);
        InvalidateSnapshot();
    }

    public async Task SetTaskSpeedLimitAsync(string gid, long bytesPerSec, CancellationToken ct = default)
    {
        await _client.SetTaskSpeedLimitAsync(gid, bytesPerSec, ct).ConfigureAwait(false);
        InvalidateSnapshot();
    }

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
        // 轮询已停，此刻落盘不会再与后续写入竞争：本地历史（终态快照）必须随优雅停止写稳，
        // 否则最多只会丢 500ms 防抖窗口内的完成记录。aria2 侧停止失败也不能跳过这一步。
        try { await _process.StopAsync(_client).ConfigureAwait(false); }
        finally { try { _store.SaveNow(); } catch { } }
        _log("下载引擎已停止");
    }

    public async ValueTask DisposeAsync()
    {
        try { await StopAsync().ConfigureAwait(false); } catch { }
        // StopAsync 已等待轮询任务结束，此时释放其取消令牌是安全的（CA2213：自有可释放字段必须释放）
        try { _pollCts?.Dispose(); } catch { }
        _pollCts = null;
        _client.Dispose();
        _process.Dispose();
        _engineRestartLock.Dispose();
    }
}
