using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace KokonaDownloader.Core.Engine.Idm;

public enum IdmTaskState { Waiting, Active, Paused, Completed, Failed }

public sealed record IdmDownloadOptions
{
    public required string Url { get; init; }
    /// <summary>
    /// 备用地址（同一文件的镜像）。主地址连接失败/不返回 206 时自动切换，
    /// 与 aria2 的多 URI 任务同语义。只接受与主地址同协议的条目。
    /// </summary>
    public IReadOnlyList<string>? Mirrors { get; init; }
    public required string Directory { get; init; }
    public string? FileName { get; init; }
    /// <summary>
    /// 断点目录（.kdpart 与状态文件）。为空时退回 Directory（旧行为）。
    /// 生产上由 DownloadEngine 指向 %APPDATA%\KokonaDownloader\parts，避免下载目录里出现中间文件。
    /// </summary>
    public string? PartsDirectory { get; init; }
    /// <summary>
    /// 断点文件主键（生产上就是 gid）。必须稳定且唯一：同名不同 URL 的两个下载才不会互相踩。
    /// 为空时退回净化后的文件名。
    /// </summary>
    public string? PartKey { get; init; }
    /// <summary>最大并发连接数（IDM 默认 8）。</summary>
    public int MaxConnections { get; init; } = 8;
    /// <summary>允许切分的最小剩余量；小于它就值得再开连接了。</summary>
    public long MinSplitSize { get; init; } = 256 * 1024;
    /// <summary>IDM 默认伪装成 Win7 上的 IE11，这里保持一致。</summary>
    public string UserAgent { get; init; } =
        "Mozilla/5.0 (Windows NT 6.1; Trident/7.0; rv:11.0) like Gecko";
    public string? Referer { get; init; }
    public string? Cookies { get; init; }
    /// <summary>
    /// 额外请求头，语义与 aria2 的 <c>header</c> 选项一致：每项是 "Name: Value"。
    /// 浏览器扩展把 Cookie/UA/Authorization 放在这里，丢掉就会 403。
    /// </summary>
    public IReadOnlyList<string>? Headers { get; init; }
    public long SpeedLimit { get; init; }
    /// <summary>
    /// 目标文件已存在时是否覆盖。默认 false：与 aria2 路径（allow-overwrite=false）一致，
    /// 自动改成 "name (1).ext" 而不是把用户原有文件抹掉。
    /// </summary>
    public bool OverwriteTarget { get; init; }

    /// <summary>
    /// 期望的 HTTP 版本（null = 自动：https 试 2.0，http 用 1.1）。
    /// h2 的意义是**单连接多路复用**：多分段不再各占一个 TCP 连接，
    /// 高延迟/TLS 线路上省掉多次握手与慢启动；但单连接遇到丢包会互相拖累，
    /// 所以策略是"能协商就用，协商不到就退回 1.1"，绝不强求。
    /// </summary>
    public Version? RequestVersion { get; init; }

    /// <summary>版本协商策略，默认"协商不到就降级"。</summary>
    public HttpVersionPolicy VersionPolicy { get; init; } = HttpVersionPolicy.RequestVersionOrLower;

    /// <summary>
    /// 断点文件是否标记为稀疏（默认开）：这样"逻辑长度 = 总大小"但磁盘占用只随实际下载增长，
    /// 不会出现"一下载 10G 的文件，盘里瞬间少 10G"。不支持稀疏的文件系统自动退回按需增长。
    /// </summary>
    public bool SparsePreallocation { get; init; } = true;

    /// <summary>
    /// 多久没有收到任何字节就判定卡死（默认 60 秒）。null 用默认值。
    /// 测试里会调到 1 秒左右，否则一个用例要等一分钟。
    /// </summary>
    public TimeSpan? StallTimeout { get; init; }

    /// <summary>
    /// 连续多少次连接失败就放弃（默认 12）。任何字节增长都会把计数清零，
    /// 所以正常下载不受影响；只有"每次连上都立刻失败"才会触发。
    /// </summary>
    public int MaxRetries { get; init; } = 12;
}

public sealed record IdmProgress(
    long TotalLength,
    long CompletedLength,
    int Connections,
    long Speed,
    IdmTaskState State,
    long Segments,
    string? FilePath);

/// <summary>续传状态，落盘为 .kdstate.json；分段区间原样保存。</summary>
internal sealed class IdmStateFile
{
    public string Url { get; set; } = "";
    public string FilePath { get; set; } = "";
    public long TotalLength { get; set; }
    public bool RangesSupported { get; set; }
    public string? ETag { get; set; }
    public string? LastModified { get; set; }
    public List<ChunkTable.Segment> Segments { get; set; } = new();
}

/// <summary>
/// IDM 协议下载器（HTTP/HTTPS）。
/// 行为对齐 IDM 6.43 实测结果：
///   1. C0 发**不带 Range** 的裸 GET，边收边从响应头学 Content-Length / Accept-Ranges；
///   2. 建 chunk 链表，C0 独占 [0, total)；
///   3. 连接数未满时，把「剩余最多且已有进度」的段的剩余区间对半切开，新连接发 Range 接走尾段；
///   4. 每连接单区间，不发 multipart/byteranges；
///   5. 切点取「剩余区间中点」而不是当前写入位置：切在当前位置会截断在传连接，
///      退化成单连接吞吐（见 MonitorAsync 里的注释与实测数据）。
/// </summary>
public sealed class IdmHttpDownloader : IAsyncDisposable
{
    private readonly HttpClient _http;
    private readonly ChunkTable _chunks = new();
    private readonly List<Task> _workers = new();
    private readonly object _splitLock = new();
    private SafeFileHandle? _handle;
    private long _completed;
    private int _connections;
    private bool _ranges;
    private long _lastSampleTicks;
    private long _lastSampleBytes;
    private long _speed;
    private IdmTaskState _state = IdmTaskState.Waiting;
    private AsyncRateLimiter _limiter = new(0);
    private Action<string>? _log;
    private string _url = "";
    private string? _statePath;
    /// <summary>已完成：检查点循环据此停止写状态文件。</summary>
    private volatile bool _finished;
    /// <summary>远端的强校验器：续传前用它判断"内容是否已经换了"。</summary>
    /// <summary>实际协商到的协议版本（"2.0"/"1.1"），供日志与测试观察。</summary>
    public string? NegotiatedProtocol { get; private set; }
    private string? _etag;
    private string? _lastModified;
    /// <summary>
    /// 中止本次传输（看门狗判定卡死后调用），与"用户暂停"区分开。
    /// 存 Action 而不是 CancellationTokenSource 字段：CTS 由 DownloadAsync 里的 using 释放，
    /// 放字段会触发 CA2213（"持有 IDisposable 却不在 Dispose 里释放"）。
    /// </summary>
    private volatile Action? _abortRun;
    /// <summary>本次运行是否被看门狗判定为卡死（决定失败还是暂停）。</summary>
    private volatile bool _stalled;
    /// <summary>失败原因由看门狗/重试上限给出（卡死是超时，连续失败是 IO 错误）。</summary>
    private Exception? _failError;
    /// <summary>
    /// 未知总长度（chunked / 动态生成的文件）：单连接顺序写，读到 EOF 即完成。
    /// 分段与断点续传都以"已知总长"为前提，这条路径两者都不做。
    /// </summary>
    private bool _unknownLength;
    private volatile bool _reachedEof;
    /// <summary>连续失败次数：只要字节有增长就清零，达到上限立刻失败而不是干等看门狗。</summary>
    private int _consecutiveFailures;
    /// <summary>
    /// 丢掉用户/扩展传来的 User-Agent，改用内置默认值。
    /// 触发条件：探测收到 403 且请求里确实带了自定义 UA。
    /// 现实案例：清华 / 中科大镜像站会拒绝现代浏览器 UA（返回 403），而 curl、IE11 UA 都放行——
    /// 浏览器扩展接管下载时恰好会透传 Edge 的 UA，于是"浏览器里能下、接管后 403"。
    /// </summary>
    private volatile bool _dropUserAgent;
    /// <summary>
    /// 多久没有任何字节增长就判定卡死。默认 60 秒：
    /// 网络抖动/慢启动不该被误杀，但"永远停在 99%"必须有个终点。
    /// </summary>
    private static readonly TimeSpan DefaultStallTimeout = TimeSpan.FromSeconds(60);
    /// <summary>可用地址列表（主地址 + 镜像），失败时轮转。</summary>
    private IReadOnlyList<string> _urls = Array.Empty<string>();
    private int _mirror;

    public IdmHttpDownloader(Action<string>? log = null)
    {
_log = log;
#pragma warning disable CA2000 // 由 HttpClient 持有并在 Dispose 时释放
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            AutomaticDecompression = DecompressionMethods.None,
            MaxConnectionsPerServer = 32,
            // h2 下默认只用一条 TCP 连接做多路复用；服务端 MAX_CONCURRENT_STREAMS 很小时
            // 允许运行时再开连接，避免"8 个分段排队等 2 个流"。
            EnableMultipleHttp2Connections = true,
            ConnectTimeout = TimeSpan.FromSeconds(20),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
#pragma warning restore CA2000
    }

    /// <summary>运行期更新本任务限速（字节/秒，0 = 不限速）。</summary>
    public void UpdateLimit(long bytesPerSecond) => _limiter.UpdateRate(bytesPerSecond);

    public long TotalLength
    {
        get
        {
            // 未知长度（chunked）下载时确实不知道总量，只能报 0；但**下完就知道了**。
            // 不回填的话，任务在界面上永远是 "0 B / 0 B、0%"，明明已成功还像个坏任务
            // （真实站点实测撞到过：254 KB 的文件下载完成，列表里显示 0/0）。
            if (_unknownLength) return IsReallyComplete ? CompletedLength : 0;
            return _chunks.TotalLength;
        }
    }

    /// <summary>
    /// 真正的完成判定：未知长度看"读到 EOF"，其余看分段表是否填满。
    /// 所有"完成/未完成"的判断都必须走这里，避免两套语义打架。
    /// </summary>
    private bool IsReallyComplete => _unknownLength ? _reachedEof : _chunks.IsComplete;
    public long CompletedLength => Interlocked.Read(ref _completed);
    public int Connections => Volatile.Read(ref _connections);
    public string? FilePath { get; private set; }

    /// <summary>下载（含续传）。progress 会被高频调用，调用方自行节流。</summary>
    public async Task<IdmProgress> DownloadAsync(IdmDownloadOptions opts,
        Action<IdmProgress>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(LongSafe(opts.Directory));
        _urls = BuildUrlList(opts);
        _mirror = 0;

        // 目标名未必现在就知道：像 /download?id=123 这种地址，真名只在响应头 Content-Disposition 里。
        // 断点文件按 gid 命名，不依赖文件名，所以这里可以先留空、拿到响应头再定。
        string? target = null;
        if (!string.IsNullOrWhiteSpace(opts.FileName))
            target = Path.Combine(opts.Directory, Sanitize(opts.FileName!));

        string key = Sanitize(opts.PartKey ?? opts.FileName ?? GuessName(opts.Url));
        string preferredParts = opts.PartsDirectory ?? opts.Directory;
        string partsDir = ResolvePartsDirectory(opts.PartsDirectory, opts.Directory);
        // 跨卷时断点会被挪到目标盘，这里记一笔：完成/删除时要把那个临时目录一起收掉
        bool relocatedParts = !string.Equals(partsDir, preferredParts, StringComparison.OrdinalIgnoreCase);
        Directory.CreateDirectory(LongSafe(partsDir));
        if (relocatedParts) TryHideDirectory(partsDir);
        string part = Path.Combine(partsDir, $"{key}.kdpart");
        string statePath = part + ".state.json";

        // 旧版把 .kdpart 写在下载目录里：升级后把断点搬过去，否则用户"昨天下的 90%"会凭空消失
        if (!File.Exists(part))
        {
            string legacy = Path.Combine(opts.Directory, $"{Sanitize(opts.FileName ?? GuessName(opts.Url))}.kdpart");
            if (!string.Equals(legacy, part, StringComparison.OrdinalIgnoreCase) && File.Exists(legacy))
            {
                try
                {
                    File.Move(legacy, part);
                    if (File.Exists(legacy + ".state.json") && !File.Exists(statePath))
                        File.Move(legacy + ".state.json", statePath);
                    _log?.Invoke($"[idm] 断点已迁移到应用数据目录: {Path.GetFileName(part)}");
                }
                catch (Exception ex) { _log?.Invoke($"[idm] 断点迁移失败（将重新下载）: {ex.Message}"); }
            }
        }

        IdmStateFile? state = TryLoadState(statePath);
        long total = 0;
        bool fresh = true;

        if (state is not null && File.Exists(part) && _urls.Contains(state.Url, StringComparer.OrdinalIgnoreCase) && state.TotalLength > 0)
        {
            total = state.TotalLength;
            _ranges = state.RangesSupported;
            _chunks.Restore(total, state.Segments);
            fresh = false;
            _etag = state.ETag;
            _lastModified = state.LastModified;
            // 续传沿用上次解析出的落盘名：否则 URL 末段是 token 的文件会在续传后改名
            if (!string.IsNullOrWhiteSpace(state.FilePath)) target = state.FilePath;
        }

        _handle = File.OpenHandle(part, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
        if (_chunks.TotalLength <= 0)
            RandomAccess.SetLength(_handle, 0);

        // 续传前确认远端没变：同长度换了内容时，拼出来的文件"大小正确但内容是两代混杂的"，
        // 而且不会再有任何报错 —— 只能靠 ETag / Last-Modified / 总长度这三个信号拦下来。
        if (!fresh && !CurrentUrl.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var now = await ProbeValidatorsAsync(opts, ct).ConfigureAwait(false);
                if (ValidatorsChanged(state!, now))
                {
                    _log?.Invoke("[idm] 远端文件已变化（ETag/Last-Modified/总长度），丢弃断点重新下载");
                    fresh = true;
                    _completed = 0;
                    _chunks.Initialize(0);
                    RandomAccess.SetLength(_handle, 0);
                    TryDelete(statePath);
                    _etag = now.ETag;
                    _lastModified = now.LastModified;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // 探测失败不该阻断续传：没有校验器的站点本来就无法判断
                _log?.Invoke($"[idm] 续传前校验失败，按原断点继续: {ex.Message}");
            }
        }

        FilePath = target;
        _url = opts.Url;
        _limiter.UpdateRate(opts.SpeedLimit);   // 复用同一实例，运行期限速变更才不会被新对象覆盖
        _statePath = statePath;
        _lastSampleTicks = Stopwatch.GetTimestamp();
        _state = IdmTaskState.Active;
        // 同一个 downloader 会被"暂停 → 继续"复用，这些一次性状态必须复位
        _finished = false;
        _completed = 0;
        _unknownLength = false;
        _reachedEof = false;
        _consecutiveFailures = 0;
        _failError = null;
        _dropUserAgent = false;
        _life.Restart();

        // 周期性落盘：进程被强杀/崩溃时不会执行 catch 里的保存，只有检查点能保住已下进度。
        // 2 秒一次，代价是每 2 秒写一个几 KB 的 JSON；换来的是"崩了也最多倒退 2 秒"。
        using var checkpointCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var checkpoint = CheckpointLoopAsync(statePath, checkpointCts.Token);

        // 运行期取消源：看门狗取消它表示"卡死"，用户取消的是 ct（暂停，要保住断点）。
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _abortRun = () => { try { runCts.Cancel(); } catch (ObjectDisposedException) { } };
        _stalled = false;

        try
        {
            if (fresh)
            {
                await RunFreshAsync(opts, part, statePath, progress, runCts.Token).ConfigureAwait(false);
            }
            else
            {
                await RunResumeAsync(opts, part, statePath, progress, runCts.Token).ConfigureAwait(false);
            }

            // 取消必须显式转成异常：worker/monitor 的循环条件是 `while (!ct.IsCancellationRequested)`，
            // 取消时它们**正常返回**而不是抛出，于是这里会一路走到 return，调用方把"暂停"当成"下载成功"，
            // 界面显示「已完成 2.12 MB / 6 MB」这种自相矛盾的状态（真机复现过）。
            ct.ThrowIfCancellationRequested();

            // 看门狗可能是在所有 worker 都已"正常返回"之后才收的尾（例如分片全被拒，
            // 每条连接都是 return 而不是抛异常），这时不会有 OCE 传上来，得在这里显式转成失败。
            if (_stalled)
                throw _failError ?? new TimeoutException(
                    $"超过 {(opts.StallTimeout ?? DefaultStallTimeout).TotalSeconds:0} 秒没有收到任何数据，已停止（断点已保留）");

            // 不完整绝不能返回成功：调用方只看"没抛异常"就标已完成，
            // 一旦这条路径被打通（例如监视器提前退出），用户会拿到一个大小对、内容缺的文件。
            if (!IsReallyComplete)
                throw new InvalidOperationException(_unknownLength
                    ? $"下载未完成（已收到 {CompletedLength} 字节），断点已丢弃"
                    : $"下载未完成（{_chunks.CompletedLength} / {_chunks.TotalLength} 字节），断点已保留");

            if (IsReallyComplete)
            {
                // 按需增长模式（不支持稀疏时）文件长度靠最后一次写入撑到 total，这里统一收口，
                // 避免尾段恰好被别处覆盖导致长度差几个字节。
                // 未知长度不需要：顺序写到最后，长度本来就是对的。
                if (!_unknownLength && _handle is not null) RandomAccess.SetLength(_handle, _chunks.TotalLength);
                _handle?.Dispose();
                _handle = null;

                // 落盘名冲突：默认改名为 "name (1).ext"，绝不静默覆盖用户已有文件。
                // aria2 路径靠 allow-overwrite=false 拿到同样的语义，这里必须自己实现。
                //
                // 注意不能"先 File.Exists 判断再 Move"：两个同名任务同时完成时都会看到"不存在"，
                // 于是互相覆盖（真机复现：批量下同一文件，最后只剩一个，两个任务都指向它）。
                // 正确做法是让 Move 自己当原子占位：目标已存在就抛 IOException，换下一个候选名重试。
                FilePath ??= Path.Combine(opts.Directory, Sanitize(opts.FileName ?? GuessName(CurrentUrl)));
                long moveStartedAt = _life.ElapsedMilliseconds;
                string finalPath = ClaimTargetPath(part, FilePath, opts.OverwriteTarget);
                long moveMs = _life.ElapsedMilliseconds - moveStartedAt;
                // 改名正常是微秒级。这里超过 1 秒说明盘忙或被占用：记一条，免得以后又靠猜。
                if (moveMs > 1000)
                    _log?.Invoke($"[idm] 收尾改名耗时 {moveMs}ms（目标盘繁忙或被占用）");
                FilePath = finalPath;      // 上报给管理器 → 元数据 → 历史行
                _finished = true;          // 置位后再删状态，避免检查点把文件又写回来
                TryDelete(statePath);
                // 跨卷时这个目录是我们建的，任务完成后不该留在用户的下载目录里
                if (relocatedParts) TryRemoveEmptyDirectory(partsDir);
                _state = IdmTaskState.Completed;
            }
        }
        catch (OperationCanceledException) when (_stalled && !ct.IsCancellationRequested)
        {
            // 卡死：不是用户暂停，要报失败（但断点保留，用户可以重试接着下）
            _state = IdmTaskState.Failed;
            await SaveStateAsync(statePath).ConfigureAwait(false);
            throw _failError ?? new TimeoutException(
                $"超过 {(opts.StallTimeout ?? DefaultStallTimeout).TotalSeconds:0} 秒没有收到任何数据，已停止（断点已保留）");
        }
        catch (OperationCanceledException)
        {
            _state = IdmTaskState.Paused;
            await SaveStateAsync(statePath).ConfigureAwait(false);
            throw;
        }
        catch
        {
            _state = IdmTaskState.Failed;
            await SaveStateAsync(statePath).ConfigureAwait(false);
            throw;
        }
        finally
        {
            checkpointCts.Cancel();
            try { await checkpoint.ConfigureAwait(false); } catch { }
            _abortRun = null;
            // 句柄必须在这里释放：暂停后「继续」会复用同一个 downloader 重新打开同一个 .kdpart，
            // 旧句柄不放就是「文件正被另一个进程使用」（真机 + 用例都复现过）。
            _handle?.Dispose();
            _handle = null;
        }

        return Report(progress);
    }

    private const int CheckpointIntervalMs = 2000;

    /// <summary>临时诊断：收尾各阶段耗时（定位"数据下完到状态完成"的空档）。</summary>
    private readonly Stopwatch _life = new();

    private async Task CheckpointLoopAsync(string statePath, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(CheckpointIntervalMs, ct).ConfigureAwait(false);
                await SaveStateAsync(statePath).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* 正常退出 */ }
    }

    private async Task RunFreshAsync(IdmDownloadOptions opts, string part, string statePath,
        Action<IdmProgress>? progress, CancellationToken ct)
    {
        if (CurrentUrl.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase))
        {
            await RunFtpAsync(opts, part, statePath, progress, ct).ConfigureAwait(false);
            return;
        }

        // C0：裸 GET（不带 Range），响应头即探测结果。
        // 主地址挂了就换镜像——这是"多 URL 任务只有一个能用"场景的唯一出路。
        HttpResponseMessage? resp = null;
        Exception? lastError = null;
        for (int attempt = 0; attempt < Math.Max(1, _urls.Count); attempt++)
        {
            try
            {
                // 临时 5xx 必须重试：CDN 限流、边缘节点抖动都是常态，一次 503 就判失败
                // 会让用户看到"下载失败"却什么也没发生（aria2 路径有 --max-tries=5，
                // 换成自研引擎后这段能力没跟过来，属于能力回退）。
                for (int retry = 0; ; retry++)
                {
                    using var probe = BuildRequest(opts, CurrentUrl, null, useRange: false);
                    resp = await _http.SendAsync(probe, HttpCompletionOption.ResponseHeadersRead, ct)
                        .ConfigureAwait(false);

                    // 403 且带了自定义 UA：换回内置 UA 再试一次。
                    // 有些站点的反爬规则恰好拒绝浏览器 UA（镜像站常见），而下载器默认 UA 是放行的。
                    if (resp.StatusCode == HttpStatusCode.Forbidden && !_dropUserAgent && HasUserAgentHeader(opts))
                    {
                        _log?.Invoke("[idm] 403：去掉自定义 User-Agent 重试一次");
                        _dropUserAgent = true;
                        resp.Dispose();
                        using var retryReq = BuildRequest(opts, CurrentUrl, null, useRange: false);
                        resp = await _http.SendAsync(retryReq, HttpCompletionOption.ResponseHeadersRead, ct)
                            .ConfigureAwait(false);
                    }

                    if (!IsTransientStatus(resp.StatusCode) || retry >= MaxProbeRetries - 1)
                        break;

                    TimeSpan wait = RetryDelayOf(resp, retry);
                    _log?.Invoke($"[idm] 探测收到 {(int)resp.StatusCode}，{wait.TotalMilliseconds:0} ms 后重试（第 {retry + 1} 次）");
                    resp.Dispose();
                    resp = null;
                    await Task.Delay(wait, ct).ConfigureAwait(false);
                }

                resp!.EnsureSuccessStatusCode();
                break;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                lastError = ex;
                resp?.Dispose();
                resp = null;
                RotateMirror(ex.Message);
            }
        }
        if (resp is null)
            throw lastError ?? new InvalidOperationException("所有下载地址都不可用");

        using (resp)
        {
            long total = resp.Content.Headers.ContentLength ?? -1;
            // .NET 可能把 Accept-Ranges 归到响应头也可能归到内容头，两处都查
            _ranges = total > 0 && (HasBytesRange(resp.Headers) || HasBytesRange(resp.Content.Headers));
            NegotiatedProtocol = resp.Version.ToString();
            _log?.Invoke($"[idm] HTTP 版本 {NegotiatedProtocol}");
            _etag = resp.Headers.ETag?.ToString();
            _lastModified = resp.Content.Headers.LastModified?.ToString("R");

            // 真名优先取 Content-Disposition（下载站点的 URL 常常只是 /download?id=123）
            if (FilePath is null)
                FilePath = Path.Combine(opts.Directory, Sanitize(PickName(opts, CurrentUrl, resp)));

            if (total <= 0)
            {
                // 服务器没给总长度（chunked / 动态生成的文件）。
                // 分段和续传都以"已知总长"为前提，这里退回"单连接顺序写、读到 EOF 即完成"。
                // 直接报错是不对的：aria2 时代这类地址能下，不能因为换了引擎就下不了。
                _unknownLength = true;
                _ranges = false;
                _log?.Invoke("[idm] 服务器未提供 Content-Length，改为单连接顺序下载（不支持断点续传）");
                await RunUnknownLengthAsync(opts, resp, progress, ct).ConfigureAwait(false);
                return;
            }

            EnsureFreeSpace(opts.Directory, total);

            // 声明了 Accept-Ranges 不等于真的兑现 Range。实测有服务器对区间请求回整份 200，
            // 此时每条 worker 都会各自"整份读取 + 跳过前缀"，文件虽然正确但下载量翻倍（实测 2.02x）。
            // 用一个 1 字节的区间请求确认真实能力：不兑现就找能兑现的镜像，都没有才退回单连接。
            if (_ranges && opts.MaxConnections > 1 && total >= opts.MinSplitSize * 2
                && !await VerifyRangeAsync(opts, ct))
            {
                string? capable = await FindRangeCapableMirrorAsync(opts, ct).ConfigureAwait(false);
                if (capable is not null)
                {
                    // 把能兑现 Range 的镜像排到最前：分段连接从它开始试，
                    // 不必每条都先撞一遍主地址那份"整份 200"。
                    var reordered = new List<string> { capable };
                    reordered.AddRange(_urls.Where(u => !string.Equals(u, capable, StringComparison.OrdinalIgnoreCase)));
                    _urls = reordered;
                    _mirror = 0;
                    _log?.Invoke($"[idm] 主地址忽略 Range，分段改走镜像: {capable}");
                }
                else
                {
                    _ranges = false;
                    _log?.Invoke("[idm] 服务器声明 Accept-Ranges 但忽略 Range 请求，改为单连接顺序下载");
                }
            }

            _chunks.Initialize(total);
            PreparePartFile(_handle!, total, opts.SparsePreallocation);
            _chunks.Claim(0);

            // 第一条连接直接当 worker 0 用（与 C0 行为一致）
            _workers.Add(RunWorkerAsync(opts, 0, resp, progress, ct));
            var monitor = MonitorAsync(opts, statePath, total, progress,
                id => RunRangeWorkerAsync(opts, id, progress, ct), ct);

            // 必须等管理员退出（= 全部分段完成或取消），否则会漏掉后续连接开出来的段
            await Task.WhenAll(_workers).ConfigureAwait(false);
            await monitor.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 未知总长度：单连接顺序写，读到 EOF 即完成。
    /// 没有监视器在跑，所以进度要自己按 250ms 的节奏上报（与监视器一致）。
    /// </summary>
    private async Task RunUnknownLengthAsync(IdmDownloadOptions opts, HttpResponseMessage resp,
        Action<IdmProgress>? progress, CancellationToken ct)
    {
        Interlocked.Increment(ref _connections);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        long pos = 0;
        long lastReport = Environment.TickCount64;
        try
        {
            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            while (!ct.IsCancellationRequested)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
                if (read <= 0) { _reachedEof = true; break; }

                await RandomAccess.WriteAsync(_handle!, buffer.AsMemory(0, read), pos, ct).ConfigureAwait(false);
                pos += read;
                Interlocked.Add(ref _completed, read);
                await _limiter.ThrottleAsync(read, ct).ConfigureAwait(false);

                long now = Environment.TickCount64;
                if (now - lastReport >= 250)
                {
                    lastReport = now;
                    SampleSpeed();
                    Report(progress);
                }
            }
        }
        finally
        {
            Interlocked.Decrement(ref _connections);
            ArrayPool<byte>.Shared.Return(buffer);
            resp.Dispose();
        }
    }

    private async Task RunResumeAsync(IdmDownloadOptions opts, string part, string statePath,
        Action<IdmProgress>? progress, CancellationToken ct)
    {
        PreparePartFile(_handle!, _chunks.TotalLength, opts.SparsePreallocation);
        _completed = _chunks.CompletedLength;

        foreach (var seg in _chunks.Segments.Values.OrderBy(s => s.Start))
            if (!seg.IsComplete) _chunks.Release(seg.Id);

        // 续传时只有一条空闲连接：先领第一个未完成段，再交给监视器继续分裂
        var firstSeg = _chunks.ClaimNextUnfinished();
        if (firstSeg is not null)
            _workers.Add(RunRangeWorkerAsync(opts, firstSeg.Id, progress, ct));

        var monitor = MonitorAsync(opts, statePath, _chunks.TotalLength, progress,
            id => RunRangeWorkerAsync(opts, id, progress, ct), ct);
        await Task.WhenAll(_workers).ConfigureAwait(false);
        await monitor.ConfigureAwait(false);
    }

    /// <summary>
    /// FTP 下载：没有 Range 头，等价能力是 REST(offset) + RETR。
    /// 先用 SIZE 拿总长建表，然后每条连接各开一个控制连接，从自己的 offset 起取够本段字节数。
    /// </summary>
    private async Task RunFtpAsync(IdmDownloadOptions opts, string part, string statePath,
        Action<IdmProgress>? progress, CancellationToken ct)
    {
        var uri = new Uri(CurrentUrl);
        string remotePath = Uri.UnescapeDataString(uri.AbsolutePath);

        // FTP 没有响应头，名字只能来自 URL
        if (FilePath is null)
            FilePath = Path.Combine(opts.Directory, Sanitize(opts.FileName ?? GuessName(CurrentUrl)));

        long total;
        using (var probe = await FtpConnection.ConnectAsync(uri, ct).ConfigureAwait(false))
            total = await probe.SizeAsync(remotePath, ct).ConfigureAwait(false);

        if (total <= 0) throw new InvalidOperationException("FTP SIZE 返回 0，无法分段");

        EnsureFreeSpace(opts.Directory, total);
        _chunks.Initialize(total);
        PreparePartFile(_handle!, total, opts.SparsePreallocation);
        _ranges = true;                 // REST 总是可用，允许持续切分
        _chunks.Claim(0);

        _workers.Add(RunFtpWorkerAsync(opts, uri, remotePath, 0, progress, ct));
        var monitor = MonitorAsync(opts, statePath, total, progress,
            id => RunFtpWorkerAsync(opts, uri, remotePath, id, progress, ct), ct);

        await Task.WhenAll(_workers).ConfigureAwait(false);
        await monitor.ConfigureAwait(false);
    }

    private async Task RunFtpWorkerAsync(IdmDownloadOptions opts, Uri uri, string remotePath, int segId,
        Action<IdmProgress>? progress, CancellationToken ct)
    {
        Interlocked.Increment(ref _connections);
        try
        {
            var seg = _chunks.Segments[segId];
            using var conn = await FtpConnection.ConnectAsync(uri, ct).ConfigureAwait(false);
            // 同 HTTP：REST 要落在「本段已写到的位置」，否则续传数据整体错位
            await using var stream = await conn.OpenReadAsync(remotePath, seg.Start + seg.Written, ct).ConfigureAwait(false);
            await PumpAsync(stream, segId, progress, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log?.Invoke($"[idm/ftp] 段 {segId} 连接失败: {ex.Message}");
        }
        finally
        {
            Interlocked.Decrement(ref _connections);
            CountIfIncomplete(segId);
            _chunks.Release(segId);
        }
    }

    /// <summary>连接管理员：连接未满时切段并开新连接（IDM 的 "可选连接"）。</summary>
    private async Task MonitorAsync(IdmDownloadOptions opts, string statePath, long total,
        Action<IdmProgress>? progress, Func<int, Task> spawn, CancellationToken ct)
    {
        var tick = TimeSpan.FromMilliseconds(250);
        // 切分只发生在「监视器醒来」的那一刻，所以 250ms 一拍意味着连接数是"每拍翻一倍"：
        // 16 连接要 4 拍 ≈ 1 秒才铺满。几秒就能下完的文件会被这一秒拖住（实测 16 连接
        // 达成率只有 86%，16 个连接里平均只有 7.9 个在工作）。
        // 起步阶段改用 40ms 的节拍，铺满后立刻落回 250ms；最多加速 40 拍兜底，
        // 避免服务器极慢（每拍都切不动）时一直空转。
        var rampTick = TimeSpan.FromMilliseconds(40);
        const int MaxFastTicks = 40;
        int maxConns = Math.Max(1, opts.MaxConnections);
        bool ramp = _ranges && maxConns > 1;
        int fastTicks = 0;
        long lastWatchdogDone = -1;
        long lastWatchdogAt = Stopwatch.GetTimestamp();
        while (!ct.IsCancellationRequested && !_chunks.IsComplete)
        {
            try { await Task.Delay(ramp ? rampTick : tick, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            int spawned = 0;

            SampleSpeed();

            // 看门狗：只要「已写入字节数」长时间不动就判定卡死。
            // 覆盖两种现实故障——
            //   1) 服务器发了一半就吊着不关连接（ReadAsync 会一直阻塞，而 HttpClient 是无限超时）；
            //   2) 连接反复失败（403 过期、404、磁盘写失败），监视器每 250ms 重新认领、又失败，循环不止。
            // 两种情况都不会有任何异常传出来，没有这条判定，任务会永远停在"下载中"。
            long watchdogDone = CompletedLength;
            if (watchdogDone != lastWatchdogDone)
            {
                lastWatchdogDone = watchdogDone;
                lastWatchdogAt = Stopwatch.GetTimestamp();
                // 有字节进账就说明连接是活的，连续失败计数清零
                Interlocked.Exchange(ref _consecutiveFailures, 0);
            }
            else if (Volatile.Read(ref _connections) == 0 && !IsReallyComplete
                     && Volatile.Read(ref _consecutiveFailures) >= MaxRetriesOf(opts))
            {
                // 连接被持续拒绝（403 过期 / 404 / 磁盘写满）：不必等满看门狗，
                // 否则用户要干等一分钟才看到一个跟真实原因无关的"没有收到数据"。
                int n = Volatile.Read(ref _consecutiveFailures);
                _failError = new IOException($"连续 {n} 次连接失败，已停止（断点已保留）");
                _stalled = true;
                _log?.Invoke($"[idm] 连续 {n} 次连接失败，停止重试");
                _abortRun?.Invoke();
                return;
            }
            else if (Stopwatch.GetElapsedTime(lastWatchdogAt) > (opts.StallTimeout ?? DefaultStallTimeout))
            {
                _stalled = true;
                _log?.Invoke($"[idm] {(opts.StallTimeout ?? DefaultStallTimeout).TotalSeconds:0} 秒没有收到任何数据，判定卡死并停止");
                _abortRun?.Invoke();
                return;
            }

            // 孤儿段回收：必须放在 _ranges 判断之前。服务器不支持分段时（_ranges=false）
            // 已经没有"切分"这回事，但先前切出去、连接又失败的尾段仍得有人取，否则永远下不完。
            if (Volatile.Read(ref _connections) == 0 && !_chunks.IsComplete)
            {
                var orphan = _chunks.ClaimNextUnfinished();
                if (orphan is not null)
                {
                    _workers.Add(spawn(orphan.Id));
                }
                else
                {
                    // 未完成的段都被"刚 spawn、还没真正开始跑"的连接持有，这一拍先让位。
                    //
                    // 这里**不能 break**：曾经只要连续 500ms 看不到连接就收尾，
                    // 结果是 4MB 的下载下到 1MB 时以"正常返回"结束，调用方标成已完成
                    // ——用户拿到一个 1MB 的文件，界面却写着 4MB/4MB。
                    // 现在由"无进展看门狗"负责收尾，无限等待是安全的。
                    await Task.Delay(250, ct).ConfigureAwait(false);
                }
            }

            if (!_ranges)
            {
                ramp = false;
                Report(progress);
                continue;
            }

            // 必须把"本拍已经 spawn、但 worker 还没自增计数"的那些也算进去：
            // _connections 是在 worker 里自增的，spawn 之后要过几毫秒才生效。
            // 只看 _connections 会在同一拍里连续切分，实际开出的连接数超过用户设定
            // （基准里 4 连接测到过 5 个并发请求，等于偷偷给站点加压）。
            while (Volatile.Read(ref _connections) + spawned < maxConns)
            {
                lock (_splitLock)
                {
                    var candidate = _chunks.PickSplittable(opts.MinSplitSize);
                    if (candidate is null) break;

                    // 关键：切在「剩余区间的中点」，而不是当前写入位置。
                    // 切在当前位置等于把正在下载的连接当场截断，它立刻退出、槽位被新连接顶替，
                    // 结果永远只有一条连接在传（实测 6MB/4.5s 只跑到 744KB/s ≈ 单连接限速）。
                    // 取中点后两段都还有大量剩余，新旧连接才能真并行。
                    long cursor = candidate.Start + candidate.Written;
                    long pos = cursor + (candidate.End - cursor) / 2;
                    if (pos >= candidate.End) break;
                    var tail = _chunks.Split(candidate.Id, pos);
                    _chunks.Claim(tail.Id);
                    _workers.Add(spawn(tail.Id));
                }
                spawned++;
            }

            // 只有"本拍确实开出了新连接、且离上限还有距离"才继续用快节拍。
            if (ramp)
            {
                fastTicks++;
                if (spawned == 0 || Volatile.Read(ref _connections) >= maxConns || fastTicks >= MaxFastTicks)
                    ramp = false;
            }

            Report(progress);
        }
    }

    private async Task RunWorkerAsync(IdmDownloadOptions opts, int segId, HttpResponseMessage resp,
        Action<IdmProgress>? progress, CancellationToken ct)
    {
        Interlocked.Increment(ref _connections);
        _chunks.Claim(segId);
        try
        {
            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await PumpAsync(stream, segId, progress, ct).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _connections);
            _chunks.Release(segId);
            resp.Dispose();
        }
    }

    private async Task RunRangeWorkerAsync(IdmDownloadOptions opts, int segId,
        Action<IdmProgress>? progress, CancellationToken ct)
    {
        // 每条连接自己按顺序遍历所有地址：不要用全局镜像索引，否则同一个 worker 重试时
        // 可能又被转回那个坏地址（两个地址交替，永远试不到能用的那个）。
        int count = Math.Max(1, _urls.Count);
        for (int i = 0; i < count; i++)
        {
            string url = _urls.Count == 0 ? opts.Url : _urls[i];
            bool lastTry = i + 1 >= count;
            Interlocked.Increment(ref _connections);
            try
            {
                var seg = _chunks.Segments[segId];
                // 关键：从「本段已写到的位置」起请求，而不是 seg.Start。
                // 续传/断线重连时 Written > 0，若仍从 Start 起请求、却按 Start+Written 写盘，
                // 整段数据会平移，产出大小正确但内容错位的文件（实测在 311296 字节处开始错）。
                long start = seg.Start + seg.Written;
                long endExclusive = seg.End;
                if (start >= endExclusive) return;   // 本段已完成，无需再连

                using var req = BuildRequest(opts, url, (start, endExclusive - 1), useRange: true);
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);

                if (resp.StatusCode == HttpStatusCode.PartialContent)
                {
                    await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    await PumpAsync(stream, segId, progress, ct).ConfigureAwait(false);
                    return;
                }

                // 非 206：这个地址不支持分段，换下一个；全都不支持再退回"整份 200 + 跳过前缀"
                if (!lastTry)
                {
                    _log?.Invoke($"[idm] {url} 返回 {(int)resp.StatusCode} 而非 206，换下一个地址");
                    continue;
                }

                // 只有 200 才能得出"这个服务器忽略 Range"的结论。
                // 503/403/404 这类是临时或权限问题：把它们也当成"不支持分段"，会让整个任务
                // 从此不再切分（一次 5xx 就把多连接降级成单连接，且不会自愈）。
                if (resp.StatusCode == HttpStatusCode.OK)
                {
                    _ranges = false;
                    // 服务器支持 Range 是运气，不支持也不该下不完：读整份、丢掉前缀、从本段起点写
                    await using var body = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    await SkipAsync(body, start, ct).ConfigureAwait(false);
                    await PumpAsync(body, segId, progress, ct).ConfigureAwait(false);
                }
                return;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                if (lastTry) return;   // 单条连接失败不终止整体：段被 Release，监视器会重新认领
                _log?.Invoke($"[idm] {url} 连接失败，换下一个地址：{ex.Message}");
                try { await Task.Delay(200, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
            }
            finally
            {
                Interlocked.Decrement(ref _connections);
                // 只要这段没下完就算一次失败——**不能只在异常里计**：
                // 服务器回 403/404 是正常响应，走的是 return 而不是 throw。
                CountIfIncomplete(segId);
                _chunks.Release(segId);
            }
        }
    }

    private void CountIfIncomplete(int segId)
    {
        if (_chunks.Segments.TryGetValue(segId, out var seg) && !seg.IsComplete)
            Interlocked.Increment(ref _consecutiveFailures);
    }

    /// <summary>丢弃流开头的 n 字节（服务器忽略 Range、回整份内容时用来对齐到分段起点）。</summary>
    private static async Task SkipAsync(Stream stream, long n, CancellationToken ct)
    {
        if (n <= 0) return;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            long left = n;
            while (left > 0)
            {
                int want = (int)Math.Min(buffer.Length, left);
                int read = await stream.ReadAsync(buffer.AsMemory(0, want), ct).ConfigureAwait(false);
                if (read <= 0) throw new IOException("响应体短于分段起点，无法对齐");
                left -= read;
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    /// <summary>把响应体写进目标文件；每轮都重读 seg.End，因为管理员可能刚把本段截短。</summary>
    private async Task PumpAsync(Stream stream, int segId, Action<IdmProgress>? progress,
        CancellationToken ct)
    {
        var seg = _chunks.Segments[segId];
        byte[] buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                long end = Volatile.Read(ref seg.End);
                long pos = seg.Start + seg.Written;
                long room = end - pos;
                if (room <= 0) break;

                int want = (int)Math.Min(buffer.Length, room);
                int read = await stream.ReadAsync(buffer.AsMemory(0, want), ct).ConfigureAwait(false);
                if (read <= 0) break;

                await RandomAccess.WriteAsync(_handle!, buffer.AsMemory(0, read), pos, ct)
                    .ConfigureAwait(false);

                lock (_splitLock)
                {
                    seg.Written += read;
                }
                Interlocked.Add(ref _completed, read);

                // 单任务限速：多连接共享同一速率
                await _limiter.ThrottleAsync(read, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static bool HasBytesRange(HttpHeaders headers) =>
        headers.TryGetValues("Accept-Ranges", out var values)
        && values.Any(v => v.Contains("bytes", StringComparison.OrdinalIgnoreCase));

    private HttpRequestMessage BuildRequest(IdmDownloadOptions opts, string url, (long from, long to)? range, bool useRange)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        // 明文 http 不主动试 h2c：.NET 只有在"精确要求 2.0"时才会走 prior-knowledge，
        // 而对普通 HTTP/1.1 站点那样做会直接连不上。https 才用 ALPN 协商。
        var version = opts.RequestVersion
                      ?? (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                          ? HttpVersion.Version20
                          : HttpVersion.Version11);
        req.Version = version;
        req.VersionPolicy = version.Major >= 2
            ? opts.VersionPolicy
            : HttpVersionPolicy.RequestVersionOrLower;

        // 先落用户/扩展给的头（Cookie、UA、Authorization…），再补我们的默认值：
        // 反过来的话 UA 会变成两个值，站点按第一个匹配，扩展传的真实 UA 就失效了。
        if (opts.Headers is { Count: > 0 })
        {
            foreach (var raw in opts.Headers)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                int colon = raw.IndexOf(':');
                if (colon <= 0) continue;
                string name = raw[..colon].Trim();
                string value = raw[(colon + 1)..].Trim();
                if (name.Length == 0 || value.Length == 0) continue;
                // 这几个由下载器自己管：透传会破坏 Range/分片语义或分帧
                if (_dropUserAgent && name.Equals("User-Agent", StringComparison.OrdinalIgnoreCase))
                    continue;   // 已判定要丢弃自定义 UA，下面会补内置默认值
                if (name.Equals("Range", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("Host", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("Accept-Encoding", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("Connection", StringComparison.OrdinalIgnoreCase))
                    continue;
                req.Headers.TryAddWithoutValidation(name, value);
            }
        }

        if (!req.Headers.Contains("User-Agent"))
            req.Headers.TryAddWithoutValidation("User-Agent", opts.UserAgent);
        if (!req.Headers.Contains("Accept"))
            req.Headers.TryAddWithoutValidation("Accept", "*/*");
        // 必须 identity：压缩会改变字节流，按偏移写盘就全错位了
        req.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
        if (!string.IsNullOrWhiteSpace(opts.Referer) && !req.Headers.Contains("Referer"))
            req.Headers.TryAddWithoutValidation("Referer", opts.Referer);
        if (!string.IsNullOrWhiteSpace(opts.Cookies) && !req.Headers.Contains("Cookie"))
            req.Headers.TryAddWithoutValidation("Cookie", opts.Cookies);
        if (useRange && range is { } r)
            req.Headers.Range = new RangeHeaderValue(r.from, r.to);
        return req;
    }

    private void SampleSpeed()
    {
        long now = Stopwatch.GetTimestamp();
        double seconds = (now - _lastSampleTicks) / (double)Stopwatch.Frequency;
        if (seconds < 0.4) return;
        long bytes = CompletedLength;
        _speed = (long)((bytes - _lastSampleBytes) / seconds);
        _lastSampleTicks = now;
        _lastSampleBytes = bytes;
    }

    private IdmProgress Report(Action<IdmProgress>? progress)
    {
        var p = new IdmProgress(TotalLength, CompletedLength, Connections, _speed,
            IsReallyComplete ? IdmTaskState.Completed : _state,
            _chunks.Segments.Count, FilePath);
        progress?.Invoke(p);
        return p;
    }

    private async Task SaveStateAsync(string statePath)
    {
        if (_finished) return;   // 已完成：不能再生成状态文件，否则续传会拿着它重下一遍
        try
        {
            var st = new IdmStateFile
            {
                Url = _url,
                FilePath = FilePath ?? "",
                TotalLength = _chunks.TotalLength,
                RangesSupported = _ranges,
                ETag = _etag,
                LastModified = _lastModified,
                Segments = _chunks.Snapshot(),
            };
            await File.WriteAllTextAsync(statePath, JsonSerializer.Serialize(st)).ConfigureAwait(false);
        }
        catch { /* 状态保存失败不影响主流程 */ }
    }

    /// <summary>
    /// 读取并**校验**续传状态。任何越界、空洞或版本不兼容都返回 null（当作全新下载）。
    /// 这里的校验不是洁癖：一旦放行坏状态，分段表会全部"已完成"，续传直接把残缺的
    /// .kdpart 改名成目标文件，用户得到一个大小正常但内容损坏的文件。
    /// </summary>
    internal static IdmStateFile? TryLoadState(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var st = JsonSerializer.Deserialize<IdmStateFile>(File.ReadAllText(path));
            if (st is null || st.TotalLength <= 0 || st.Segments.Count == 0) return null;

            long covered = 0;
            foreach (var s in st.Segments)
            {
                if (s.Start < 0 || s.End <= s.Start || s.End > st.TotalLength) return null;
                if (s.Written < 0 || s.Written > s.End - s.Start) return null;
                covered += s.End - s.Start;
            }
            // 分段必须正好铺满 [0, TotalLength)，否则说明表被截断/重叠
            return covered == st.TotalLength ? st : null;
        }
        catch { return null; }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    /// <summary>目录内同名时追加编号：name.ext → name (1).ext → name (2).ext …（与 DownloadEngine 同一套规则）。</summary>
    /// <summary>
    /// 把断点文件原子地落到目标路径，返回最终使用的路径。
    /// 不用"先判断存在再移动"：两个同名任务同时完成时都会判断为"不存在"，随后互相覆盖。
    /// 这里让 Move 充当占位操作——目标已存在会抛 IOException，换下一个候选名重试。
    /// </summary>
    private string ClaimTargetPath(string part, string desired, bool overwrite)
    {
        if (overwrite)
        {
            MoveFile(part, desired, overwrite: true);
            return desired;
        }

        for (int i = 0; i < 1000; i++)
        {
            string candidate = i == 0 ? desired : NamedCandidate(desired, i);
            try
            {
                MoveFile(part, candidate, overwrite: false);
                if (i > 0)
                    _log?.Invoke($"[idm] 目标文件已存在，自动重命名: {Path.GetFileName(desired)} → {Path.GetFileName(candidate)}");
                return candidate;
            }
            catch (IOException) when (File.Exists(LongSafe(candidate)))
            {
                // 名字被占：换下一个候选
            }
        }
        string fallback = NamedCandidate(desired, -1);
        MoveFile(part, fallback, overwrite: false);
        return fallback;
    }

    /// <summary>name.ext → name (i).ext；i &lt; 0 用时间戳兜底。</summary>
    private static string NamedCandidate(string path, int i)
    {
        string dir = Path.GetDirectoryName(path) ?? "";
        string stem = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);
        string suffix = i < 0 ? DateTime.Now.ToString("yyyyMMddHHmmssfff") : i.ToString();
        return Path.Combine(dir, $"{stem} ({suffix}){ext}");
    }

    /// <summary>
    /// 跨卷移动：断点放在 %APPDATA%（通常在 C:），下载目录可能在别的盘。
    /// File.Move 在跨卷时依赖 MoveFileEx 的 COPY_ALLOWED，不同 .NET 版本行为不一致，
    /// 这里显式先试 Move，失败再走"复制→删除"，结果一致且可预期。
    /// </summary>
    /// <summary>
    /// 把断点文件改名成目标文件。
    ///
    /// 同卷改名是一次元数据操作（微秒级），跨卷才需要真的搬数据。原来的写法对**任何**
    /// IOException 都退回"复制 → 删除"，于是"刚写完的文件被短暂独占"（杀毒软件扫描、
    /// 缩略图预览、索引服务）会把 128 MB 的改名变成整份复制——实测多花 3.3 秒，
    /// 用户看到的是"进度 100% 但还在等"。
    ///
    /// 现在按卷区分：同卷先重试改名（占用通常是毫秒级），仍失败才退化为复制并留日志；
    /// 跨卷直接复制 + 删除。
    /// </summary>
    internal static void MoveFile(string source, string target, bool overwrite)
    {
        // 目标路径可能超过 MAX_PATH（下载目录深 + 文件名长）：加前缀再做 IO，
        // 对外仍然返回原始路径，界面/元数据不受影响。
        string src = LongSafe(source);
        string dst = LongSafe(target);
        try
        {
            File.Move(src, dst, overwrite);
            return;
        }
        catch (IOException) when (!overwrite && File.Exists(dst))
        {
            throw;   // 名字被占用：交给上层换名，不能在这里 copy 覆盖
        }
        catch (IOException) when (SameVolume(source, target))
        {
            // 同卷不可能有"必须搬家"的理由：只可能是瞬时占用（杀毒扫描刚写完的文件、
            // 搜索索引、缩略图预览），或改名时内核正在回刷脏页。
            //
            // 退避策略：**先立刻重试一次**，再按 20/40/80/160/320/640 毫秒退避，
            // 总预算约 1.9 秒。上一版是"先睡再试"，白等了 0.9 秒才发现占用还在。
            int[] backoff = { 0, 20, 40, 80, 160, 320, 640, 640 };
            foreach (int wait in backoff)
            {
                if (wait > 0) Thread.Sleep(wait);
                try
                {
                    File.Move(src, dst, overwrite);
                    return;
                }
                catch (IOException) when (!overwrite && File.Exists(dst))
                {
                    throw;
                }
                catch (IOException)
                {
                    // 还被占着，继续等
                }
            }
            throw;   // 等不到就按原样报错，绝不静默复制（复制同样会被拒，反而多写一份）
        }
        catch (IOException)
        {
            MoveByCopy(src, dst, overwrite);
        }
    }

    /// <summary>跨卷搬运：复制 + 删除。复制失败时清掉半成品，不留"大小不对的目标文件"。</summary>
    private static void MoveByCopy(string src, string dst, bool overwrite)
    {
        string dir = Path.GetDirectoryName(dst) ?? "";
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        try
        {
            File.Copy(src, dst, overwrite);
        }
        catch
        {
            try { if (File.Exists(dst)) File.Delete(dst); } catch { }
            throw;
        }
        try { File.Delete(src); }
        catch (IOException) { /* 源删不掉就留着：目标已经完整，用户数据没风险 */ }
    }

    private const uint FsctlSetSparse = 0x000900C4;

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(
        System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle handle, uint code,
        IntPtr inBuffer, uint inSize,
        IntPtr outBuffer, uint outSize,
        out uint bytesReturned, IntPtr overlapped);

    /// <summary>
    /// 预分配断点文件。
    /// 直接 SetLength 到总长会让"10GB 的文件一下载就吃掉 10GB 磁盘"，所以先给文件打上稀疏标记，
    /// 再设长度——逻辑大小仍等于远端大小（写入任意偏移都合法），但物理占用只随真实数据增长。
    /// 文件系统不支持稀疏时**不预分配**，改为按写入进度增长（任何情况下都比一次性占满好）。
    /// </summary>
    private static void PreparePartFile(SafeFileHandle handle, long total, bool sparse)
    {
        if (total <= 0) return;
        if (!sparse || !TryMarkSparse(handle)) return;
        RandomAccess.SetLength(handle, total);
    }

    private static bool TryMarkSparse(SafeFileHandle handle)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            return DeviceIoControl(handle, FsctlSetSparse, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
        }
        catch { return false; }
    }

    /// <summary>可用地址：主地址 + 同协议镜像（去重，保持顺序）。</summary>
    private static IReadOnlyList<string> BuildUrlList(IdmDownloadOptions opts)
    {
        bool ftp = opts.Url.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase);
        var list = new List<string>();
        void Add(string? u)
        {
            if (string.IsNullOrWhiteSpace(u)) return;
            u = u.Trim();
            bool isFtp = u.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase);
            bool isHttp = u.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                       || u.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
            if (ftp ? !isFtp : !isHttp) return;                  // 协议必须一致，否则续传语义不同
            if (!list.Contains(u, StringComparer.OrdinalIgnoreCase)) list.Add(u);
        }
        Add(opts.Url);
        foreach (var m in opts.Mirrors ?? Array.Empty<string>()) Add(m);
        return list;
    }

    /// <summary>当前使用的地址（镜像轮转后变化）。</summary>
    private string CurrentUrl => _urls.Count == 0 ? "" : _urls[Math.Abs(_mirror) % _urls.Count];

    private void RotateMirror(string? reason = null)
    {
        if (_urls.Count <= 1) return;
        int next = Interlocked.Increment(ref _mirror);
        _log?.Invoke($"[idm] 切换到镜像 {_urls[Math.Abs(next) % _urls.Count]}" +
                     (reason is null ? "" : $"（原因：{reason}）"));
    }

    /// <summary>
    /// 向服务器要一次"当前版本"的校验信息（Range 0-0，只读响应头）。
    /// 有的站点不支持 HEAD，所以用 GET + Range 而不是 HEAD。
    /// </summary>
    private async Task<(string? ETag, string? LastModified, long Total)> ProbeValidatorsAsync(
        IdmDownloadOptions opts, CancellationToken ct)
    {
        using var req = BuildRequest(opts, CurrentUrl, (0, 0), useRange: true);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        string? etag = resp.Headers.ETag?.ToString();
        string? lastModified = resp.Content.Headers.LastModified?.ToString("R");
        long total = resp.Content.Headers.ContentRange?.Length
                     ?? resp.Content.Headers.ContentLength
                     ?? -1;
        return (etag, lastModified, total);
    }

    /// <summary>
    /// 断点是否已经作废。任一信号表明"换了一份内容"就返回 true：
    /// 总长度不同（最容易判断）、ETag 不同、Last-Modified 不同。
    /// 服务器不给校验器时返回 false —— 无法判断就按原断点继续，不假装能判断。
    /// </summary>
    private static bool ValidatorsChanged(IdmStateFile state, (string? ETag, string? LastModified, long Total) now)
    {
        if (now.Total > 0 && state.TotalLength > 0 && now.Total != state.TotalLength) return true;
        if (!string.IsNullOrEmpty(state.ETag) && !string.IsNullOrEmpty(now.ETag)
            && !string.Equals(state.ETag, now.ETag, StringComparison.Ordinal)) return true;
        if (!string.IsNullOrEmpty(state.LastModified) && !string.IsNullOrEmpty(now.LastModified)
            && !string.Equals(state.LastModified, now.LastModified, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>
    /// 决定落盘名：显式指定 &gt; Content-Disposition &gt; URL 末段。
    /// 下载站点常见 /download?id=123 这种地址，真名只在响应头里；
    /// 一律取最后一段，避免 Content-Disposition 里塞 ../ 做路径穿越。
    /// </summary>
    private static string PickName(IdmDownloadOptions opts, string currentUrl, HttpResponseMessage? resp)
    {
        if (!string.IsNullOrWhiteSpace(opts.FileName)) return opts.FileName!;
        try
        {
            var cd = resp?.Content.Headers.ContentDisposition;
            string? raw = cd?.FileNameStar ?? cd?.FileName;
            if (!string.IsNullOrWhiteSpace(raw))
            {
                string name = raw.Trim().Trim('"');
                name = Path.GetFileName(name.Replace('\\', '/'));
                if (!string.IsNullOrWhiteSpace(name) && name != "." && name != "..") return name;
            }
        }
        catch { /* 头格式不合法就退回 URL 末段 */ }
        return GuessName(currentUrl);
    }

    private static int MaxRetriesOf(IdmDownloadOptions opts) => opts.MaxRetries > 0 ? opts.MaxRetries : 12;

    /// <summary>
    /// 探测阶段遇到临时错误时的总尝试次数（含首次）。
    /// 对齐 aria2 的 <c>--max-tries=5</c> 语义但更克制：4 次、退避合计最多 3.5 秒，
    /// 既能扛过 CDN 边缘节点"抖三下"的情况，也不会让死掉的地址把用户拖住十几秒。
    /// </summary>
    private const int MaxProbeRetries = 4;

    /// <summary>临时性错误：值得退避重试；404/401/403 这类是永久错误，重试只会浪费用户时间。</summary>
    private static bool IsTransientStatus(HttpStatusCode code) => code is
        HttpStatusCode.RequestTimeout or
        HttpStatusCode.TooManyRequests or
        HttpStatusCode.InternalServerError or
        HttpStatusCode.BadGateway or
        HttpStatusCode.ServiceUnavailable or
        HttpStatusCode.GatewayTimeout;

    /// <summary>优先服从服务端 Retry-After（上限 10 秒），否则 500ms 起步指数退避、封顶 4 秒。</summary>
    private static TimeSpan RetryDelayOf(HttpResponseMessage resp, int retry)
    {
        var ra = resp.Headers.RetryAfter;
        if (ra?.Delta is { } delta && delta > TimeSpan.Zero && delta <= TimeSpan.FromSeconds(10))
            return delta;
        if (ra?.Date is { } at)
        {
            var wait = at - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero && wait <= TimeSpan.FromSeconds(10)) return wait;
        }
        return TimeSpan.FromMilliseconds(Math.Min(4000, 500 * (1 << Math.Min(retry, 3))));
    }

    /// <summary>
    /// 用 <c>Range: bytes=0-0</c> 确认服务器是否真的兑现区间请求。
    /// 只认 206：200（忽略 Range 回整份）和 416（拒绝区间）都会让"整份读取 + 跳过前缀"
    /// 的分段退化路径产生重复传输，不如一开始就单连接。
    /// 其他状态码/异常保持保守——不能因为一次探测失败就放弃多连接。
    /// </summary>
    private async Task<bool> VerifyRangeAsync(IdmDownloadOptions opts, CancellationToken ct)
    {
        try
        {
            using var req = BuildRequest(opts, CurrentUrl, (0, 0), useRange: true);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.PartialContent) return true;
            if (resp.StatusCode is HttpStatusCode.OK or HttpStatusCode.RequestedRangeNotSatisfiable)
                return false;
            return !IsTransientStatus(resp.StatusCode);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log?.Invoke($"[idm] Range 能力校验失败，按服务器声明的能力继续: {ex.Message}");
            return true;
        }
    }

    /// <summary>
    /// 主地址不兑现 Range 时，找一个真正回 206 的镜像，让分段连接改走它。
    /// 主地址仍会作为兜底留在列表里（镜像全挂时靠它的"整份 200"也能下完）。
    /// </summary>
    private async Task<string?> FindRangeCapableMirrorAsync(IdmDownloadOptions opts, CancellationToken ct)
    {
        foreach (string url in _urls)
        {
            if (string.Equals(url, CurrentUrl, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                using var req = BuildRequest(opts, url, (0, 0), useRange: true);
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);
                if (resp.StatusCode == HttpStatusCode.PartialContent) return url;
            }
            catch (OperationCanceledException) { throw; }
            catch { /* 单个镜像探测失败不影响其它候选 */ }
        }
        return null;
    }

    private static bool HasUserAgentHeader(IdmDownloadOptions opts)
        => opts.Headers?.Any(h => h.TrimStart().StartsWith("User-Agent:", StringComparison.OrdinalIgnoreCase)) == true;

    /// <summary>跨卷时断点的落点：目标目录下的隐藏子目录。</summary>
    internal const string RelocatedPartsDirName = ".kokona-parts";

    /// <summary>
    /// 决定这次下载的断点目录。
    ///
    /// 同卷就用首选目录（%APPDATA%），跨卷则挪到目标盘。原因在收尾那一步：
    /// 跨卷的 <c>File.Move</c> 会退化成"整份复制 + 删除"——128MB 实测 1.1 秒（最差 5 秒），
    /// 10GB 就是一分半，这段时间界面停在 100%，用户会以为卡死。
    /// 放到目标盘后，收尾只是一次改名。
    /// </summary>
    internal static string ResolvePartsDirectory(string? preferred, string targetDir)
    {
        if (string.IsNullOrWhiteSpace(preferred)) return targetDir;
        try
        {
            return SameVolume(preferred, targetDir)
                ? preferred
                : Path.Combine(targetDir, RelocatedPartsDirName);
        }
        catch
        {
            return preferred;   // 路径非法等异常：不影响下载，退回原行为
        }
    }

    internal static bool SameVolume(string a, string b)
    {
        // 入参可能带 \\?\ 长路径前缀（也可能不带），先去前缀再比盘符，否则同一块盘会被判成"跨卷"。
        static string Root(string p)
        {
            if (p.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) p = @"\\" + p[8..];
            else if (p.StartsWith(@"\\?\", StringComparison.Ordinal)) p = p[4..];
            return (Path.GetPathRoot(Path.GetFullPath(p)) ?? "").TrimEnd('\\', '/');
        }
        return string.Equals(Root(a), Root(b), StringComparison.OrdinalIgnoreCase);
    }

    private static void TryHideDirectory(string dir)
    {
        try { File.SetAttributes(dir, File.GetAttributes(dir) | FileAttributes.Hidden); }
        catch { /* FAT/网络盘可能不支持隐藏属性，不影响功能 */ }
    }

    /// <summary>目录为空才删；非空（别的任务还在用）就留着。</summary>
    internal static void TryRemoveEmptyDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir);
        }
        catch { /* 并发创建/占用：下次再来 */ }
    }

    /// <summary>
    /// 磁盘空间预检。写完磁盘之后再失败，用户看到的会是"没有收到数据"这种完全无关的原因；
    /// 提前算一次，能给出"需要 4 GB，D: 只剩 1.2 GB"这种能直接行动的提示。
    /// 取不到容量（网络盘 / 配额 / 权限）就跳过——测不到不代表空间不够，不能因此阻断下载。
    /// </summary>
    internal static void EnsureFreeSpace(string dir, long needed, Func<string, long?>? probe = null)
    {
        if (needed <= 0) return;
        long? free;
        try { free = probe is not null ? probe(dir) : FreeBytes(dir); }
        catch { return; }
        if (free is long f && f < needed)
            throw new IOException($"磁盘空间不足：需要 {Size(needed)}，目标盘只剩 {Size(f)}");
    }

    internal static long? FreeBytes(string dir)
    {
        string root = Path.GetPathRoot(Path.GetFullPath(dir)) ?? "";
        if (string.IsNullOrEmpty(root)) return null;
        var drive = new DriveInfo(root);
        return drive.IsReady ? drive.AvailableFreeSpace : null;
    }

    private static string Size(long bytes) => bytes >= 1L << 30
        ? $"{bytes / (double)(1L << 30):0.##} GB"
        : $"{bytes / (double)(1L << 20):0.#} MB";

    /// <summary>
    /// 超过 240 字符的路径加 \\?\ 前缀绕开 MAX_PATH。只在真正做文件 IO 的地方用，
    /// 对外（界面/元数据）仍然报原始路径。
    /// </summary>
    internal static string LongSafe(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length < 240) return path;
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path;
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return @"\\?\UNC\" + path[2..];
        return @"\\?\" + Path.GetFullPath(path);
    }

    /// <summary>Windows 设备名：即便带扩展名（CON.txt）也不能用作文件名。</summary>
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private static string Sanitize(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        // Windows 会静默吞掉结尾的点和空格：写 "a." 实际得到 "a"，后续改名/校验就对不上了
        name = name.Trim().TrimEnd('.', ' ');
        if (string.IsNullOrWhiteSpace(name)) return "download";
        // 设备名：URL 末段是 /CON 这种，直接建文件会失败（或写进控制台设备）
        string stem = Path.GetFileNameWithoutExtension(name);
        if (ReservedNames.Contains(stem)) name = "_" + name;
        return name;
    }

    private static string GuessName(string url)
    {
        try
        {
            var path = new Uri(url).AbsolutePath;
            var name = Path.GetFileName(path);
            return string.IsNullOrWhiteSpace(name) ? "download" : name;
        }
        catch { return "download"; }
    }

    public ValueTask DisposeAsync()
    {
        _handle?.Dispose();
        _http.Dispose();
        return ValueTask.CompletedTask;
    }
}
