using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using KokonaDownloader.Core.Engine;
using KokonaDownloader.Core.Settings;

namespace KokonaDownloader.Core.Api;

#region API DTO（扩展 ↔ 客户端 通信契约）

public sealed class ApiDownloadRequest
{
    [JsonPropertyName("urls")] public List<string> Urls { get; set; } = new();
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("filename")] public string? FileName { get; set; }
    [JsonPropertyName("dir")] public string? Dir { get; set; }
    [JsonPropertyName("connections")] public int Connections { get; set; }
    [JsonPropertyName("speedLimit")] public long SpeedLimit { get; set; }
    [JsonPropertyName("referer")] public string? Referer { get; set; }
    [JsonPropertyName("headers")] public List<string>? Headers { get; set; }

    public List<string> AllUrls()
    {
        var list = new List<string>();
        if (!string.IsNullOrWhiteSpace(Url)) list.Add(Url.Trim());
        list.AddRange(Urls.Where(u => !string.IsNullOrWhiteSpace(u)).Select(u => u.Trim()));
        return list.Distinct().ToList();
    }
}

public sealed class ApiTaskDto
{
    public string gid { get; set; } = string.Empty;
    /// <summary>时间戳唯一编号（yyyyMMddHHmmssfff）。</summary>
    public long taskNumber { get; set; }
    public string name { get; set; } = string.Empty;
    public string state { get; set; } = string.Empty;
    public long totalLength { get; set; }
    public long completedLength { get; set; }
    public double progress { get; set; }
    public long downloadSpeed { get; set; }
    public long? etaSeconds { get; set; }
    public string? filePath { get; set; }
    public string? dir { get; set; }
    public List<string> urls { get; set; } = new();
    public string? errorMessage { get; set; }

    public static ApiTaskDto From(DownloadTaskInfo t) => new()
    {
        gid = t.Gid,
        taskNumber = t.TaskNumber,
        name = t.Name,
        state = t.State.ToString().ToLowerInvariant(),
        totalLength = t.TotalLength,
        completedLength = t.CompletedLength,
        progress = Math.Round(t.Progress * 100, 2),
        downloadSpeed = t.DownloadSpeed,
        etaSeconds = (long?)t.Eta?.TotalSeconds,
        filePath = t.FilePath,
        dir = t.Dir,
        urls = t.Urls,
        errorMessage = t.ErrorMessage
    };
}

public sealed class ApiStatsDto
{
    public long downloadSpeed { get; set; }
    public int numActive { get; set; }
    public int numWaiting { get; set; }
    public int numStopped { get; set; }
    public long globalSpeedLimit { get; set; }
}

public sealed class ApiSettingsPatch
{
    [JsonPropertyName("defaultDownloadDir")] public string? DefaultDownloadDir { get; set; }
    [JsonPropertyName("maxConcurrentDownloads")] public int? MaxConcurrentDownloads { get; set; }
    [JsonPropertyName("defaultConnections")] public int? DefaultConnections { get; set; }
    [JsonPropertyName("notificationsEnabled")] public bool? NotificationsEnabled { get; set; }
    [JsonPropertyName("theme")] public string? Theme { get; set; }
    [JsonPropertyName("globalSpeedLimit")] public long? GlobalSpeedLimit { get; set; }
    [JsonPropertyName("interceptBrowserDownloads")] public bool? InterceptBrowserDownloads { get; set; }
    [JsonPropertyName("minimizeToTrayOnClose")] public bool? MinimizeToTrayOnClose { get; set; }
}

#endregion

/// <summary>
/// 本地 HTTP API 服务：仅绑定 127.0.0.1，供浏览器扩展与主界面外部调用。
/// 安全：除 /api/ping 外，所有请求必须携带正确密钥
/// （请求头 X-Kokona-Secret 或 Authorization: Bearer）。
/// CORS：允许任意来源（扩展 origin 为 chrome-extension://），并处理 OPTIONS 预检。
/// </summary>
public sealed class ApiService : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly DownloadEngine _engine;
    private readonly SettingsStore _settings;
    private readonly Action<string> _log;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    /// <summary>请求处理并发闸门：原先每请求一个 Task.Run 无上限，病态客户端可无限堆积处理器任务。
    /// 回环+鉴权下风险低，但仍设上限；达到上限时直接 503 拒绝（排队会让每个等待请求继续占着 socket）。</summary>
    private readonly SemaphoreSlim _requestGate;
    /// <summary>生产默认并发上限（实测 120 并发突发下 75 个 200 + 45 个 503，无排队挂起）。</summary>
    public const int DefaultMaxConcurrentRequests = 64;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public int Port { get; }
    public bool IsListening => _listener.IsListening;

    /// <summary>对外报告的版本：唯一来源是程序集版本（src\Directory.Build.props），不再手写字符串。</summary>
    public string Version { get; } = ReadAssemblyVersion();

    private static string ReadAssemblyVersion()
    {
        try
        {
            var asm = typeof(ApiService).Assembly;
            var attrs = asm.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false);
            if (attrs.Length > 0 &&
                attrs[0] is System.Reflection.AssemblyInformationalVersionAttribute info &&
                !string.IsNullOrWhiteSpace(info.InformationalVersion))
            {
                // SDK 默认会在 InformationalVersion 末尾追加 "+commit 哈希"（源链接），这里截断
                var v = info.InformationalVersion.Trim();
                var plus = v.IndexOf('+');
                return plus > 0 ? v[..plus] : v;
            }
            var ver = asm.GetName().Version;
            return ver is null ? "0.0.0" : $"{ver.Major}.{ver.Minor}.{Math.Max(ver.Build, 0)}";
        }
        catch
        {
            return "0.0.0";
        }
    }

    /// <summary>收到单条磁力链接（浏览器扩展/系统协议转发）：UI 层订阅后弹独立确认窗口，由用户决定是否下载。</summary>
    public event Action<string>? MagnetConfirmRequested;

    /// <summary>扩展送来的链接命中下载中/排队/暂停的重复任务（已自动跳过）：UI 层订阅后弹窗提醒用户。</summary>
    public event Action<string>? DuplicateTaskNoticeRequested;

    /// <param name="maxConcurrentRequests">并发闸门容量，默认 <see cref="DefaultMaxConcurrentRequests"/>。
    /// 之所以可注入：这道门的"饱和即 503"行为必须在单测里确定性地复现（用 64 槽需要同时挂 65 个连接，
    /// 且无法稳定命中时序），生产调用方一律使用默认值。</param>
    public ApiService(DownloadEngine engine, SettingsStore settings, int port, Action<string>? log = null,
                      int maxConcurrentRequests = DefaultMaxConcurrentRequests)
    {
        _engine = engine;
        _settings = settings;
        _log = log ?? (_ => { });
        Port = port;
        if (maxConcurrentRequests <= 0) throw new ArgumentOutOfRangeException(nameof(maxConcurrentRequests));
        _requestGate = new SemaphoreSlim(maxConcurrentRequests, maxConcurrentRequests);
    }

    public void Start()
    {
        _listener.Prefixes.Clear();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => AcceptLoop(_cts.Token));
        _log($"API 服务已启动: http://127.0.0.1:{Port}/");
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (HttpListenerException ex)
            {
                // Stop()/Close() 会让挂起的 GetContextAsync 抛出该异常，属正常关停；
                // 若监听器仍在监听却抛出，则是真实故障，必须留下日志再退出（否则表现为"端口占用但无响应"）。
                if (!ct.IsCancellationRequested && _listener.IsListening)
                    _log($"API 接受循环异常退出（监听器仍在使用中，API 将停止接收新请求）: {ex.Message}");
                break;
            }
            catch (ObjectDisposedException)
            {
                break; // 监听器已释放：正常关停路径
            }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                    _log($"API 接受循环未预期异常退出（监听器仍在使用中，API 将停止接收新请求）: {ex}");
                break;
            }
            if (!_requestGate.Wait(0)) // 非阻塞尝试获取（本机 SDK 的 SemaphoreSlim 无 TryWait 重载，Wait(0) 语义相同）
            {
                // 并发上限已满：立即拒绝，避免无界排队（每个排队的请求还会一直占着 socket）
                try
                {
                    ctx.Response.StatusCode = 503;
                    ctx.Response.Close();
                }
                catch { }
                continue;
            }
            try
            {
                // 不向 Task.Run 传取消令牌：令牌已取消时 Task.Run 会直接返回已取消的任务而不执行委托，
                // 那样 finally 里的 Release 永远跑不到，闸门槽位会被永久吃掉。
                _ = Task.Run(async () =>
                {
                    try { await HandleSafe(ctx).ConfigureAwait(false); }
                    finally { ReleaseGate(); }
                });
            }
            catch (Exception ex)
            {
                // 任务未能启动（线程池耗尽/进程正在关闭）：立即归还槽位，否则 64 个槽会被逐个吃光
                _log($"API 请求处理器启动失败: {ex.Message}");
                ReleaseGate();
                try
                {
                    ctx.Response.StatusCode = 503;
                    ctx.Response.Close();
                }
                catch { }
            }
        }
    }

    private void ReleaseGate()
    {
        // Dispose 后可能仍有在途处理器归还槽位：ObjectDisposedException 属关停竞态，就地吞掉
        try { _requestGate.Release(); }
        catch (ObjectDisposedException) { }
    }

    private async Task HandleSafe(HttpListenerContext ctx)
    {
        try { await Handle(ctx).ConfigureAwait(false); }
        catch (Exception ex)
        {
            _log($"API 处理异常: {ex.Message}");
            try { await WriteJson(ctx, 500, new { error = ex.Message }).ConfigureAwait(false); } catch { }
        }
        finally
        {
            try { ctx.Response.Close(); } catch { }
        }
    }

    private async Task Handle(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var resp = ctx.Response;

        // 来源校验：只放行浏览器扩展与回环地址页面。
        // 历史上这里是 Access-Control-Allow-Origin: *，而鉴权依赖自定义头（X-Kokona-Secret），
        // 自定义头必然触发 CORS 预检，而预检对**任何**来源都回 204 并允许该头 —— 于是任意网页
        // 都能带密钥调用本 API 并读取响应体（投递下载 / 删除任务 / 改下载目录），
        // 只要密钥以任何方式外泄即形成"访问一个网页就让用户机器无声开始下载"的链路。
        var origin = req.Headers["Origin"];
        if (!IsTrustedOrigin(origin))
        {
            _log($"API 拒绝不受信任的来源: {origin} {req.HttpMethod} {req.Url?.AbsolutePath}");
            resp.StatusCode = 403;
            return;
        }
        if (!string.IsNullOrEmpty(origin))
        {
            // 只回显受信任的具体来源（不再用 *），并声明响应按 Origin 变化
            resp.Headers["Access-Control-Allow-Origin"] = origin;
            resp.Headers["Vary"] = "Origin";
            resp.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
            resp.Headers["Access-Control-Allow-Headers"] = "Content-Type, X-Kokona-Secret, Authorization";
            resp.Headers["Access-Control-Max-Age"] = "600";
        }
        if (req.HttpMethod == "OPTIONS")
        {
            resp.StatusCode = 204;
            return;
        }

        var path = req.Url?.AbsolutePath.TrimEnd('/') ?? "/";

        // ping 免鉴权（扩展用于探测客户端是否在线）
        if (path == "/api/ping")
        {
            await WriteJson(ctx, 200, new { ok = true, app = "KokonaDownloader", version = Version }).ConfigureAwait(false);
            return;
        }

        // 鉴权
        if (!IsAuthorized(req))
        {
            await WriteJson(ctx, 401, new { error = "unauthorized", message = "密钥缺失或错误" }).ConfigureAwait(false);
            return;
        }

        switch (req.HttpMethod)
        {
            case "GET" when path == "/api/tasks":
                await WriteJson(ctx, 200, (await _engine.GetAllTasksAsync().ConfigureAwait(false)).Select(ApiTaskDto.From)).ConfigureAwait(false);
                return;
            case "GET" when path == "/api/stats":
                var stat = await _engine.GetGlobalStatAsync().ConfigureAwait(false);
                await WriteJson(ctx, 200, new ApiStatsDto
                {
                    downloadSpeed = stat.DownloadSpeed,
                    numActive = stat.NumActive,
                    numWaiting = stat.NumWaiting,
                    numStopped = stat.NumStopped,
                    globalSpeedLimit = _settings.Current.GlobalSpeedLimit
                }).ConfigureAwait(false);
                return;
            case "GET" when path == "/api/settings":
                await WriteJson(ctx, 200, new
                {
                    defaultDownloadDir = _settings.Current.DefaultDownloadDir,
                    maxConcurrentDownloads = _settings.Current.MaxConcurrentDownloads,
                    defaultConnections = _settings.Current.DefaultConnections,
                    notificationsEnabled = _settings.Current.NotificationsEnabled,
                    theme = _settings.Current.Theme.ToString(),
                    globalSpeedLimit = _settings.Current.GlobalSpeedLimit,
                    interceptBrowserDownloads = _settings.Current.InterceptBrowserDownloads,
                    minimizeToTrayOnClose = _settings.Current.MinimizeToTrayOnClose
                }).ConfigureAwait(false);
                return;
            case "POST" when path == "/api/download":
                await HandleDownload(ctx).ConfigureAwait(false);
                return;
            case "POST" when path == "/api/settings":
                await HandleSettingsPatch(ctx).ConfigureAwait(false);
                return;
        }

        // 任务操作 /api/tasks/{gid}/{action}
        var m = System.Text.RegularExpressions.Regex.Match(path, @"^/api/tasks/([0-9a-fA-F]+)/(pause|resume|remove|redownload)$");
        if (m.Success && req.HttpMethod == "POST")
        {
            await HandleTaskAction(ctx, m.Groups[1].Value, m.Groups[2].Value).ConfigureAwait(false);
            return;
        }

        await WriteJson(ctx, 404, new { error = "not_found" }).ConfigureAwait(false);
    }

    private async Task HandleDownload(HttpListenerContext ctx)
    {
        var body = await ReadBodyAsync(ctx.Request).ConfigureAwait(false);
        ApiDownloadRequest? dlReq;
        try { dlReq = JsonSerializer.Deserialize<ApiDownloadRequest>(body, JsonOpts); }
        catch (JsonException)
        {
            await WriteJson(ctx, 400, new { error = "bad_request", message = "请求体不是合法 JSON" }).ConfigureAwait(false);
            return;
        }
        if (dlReq == null)
        {
            await WriteJson(ctx, 400, new { error = "bad_request", message = "请求体为空" }).ConfigureAwait(false);
            return;
        }
        var urls = dlReq.AllUrls();
        if (urls.Count == 0)
        {
            await WriteJson(ctx, 400, new { error = "bad_request", message = "未提供下载地址" }).ConfigureAwait(false);
            return;
        }
        foreach (var u in urls)
        {
            // 磁力链接由引擎按 BT 参数特判处理，不走 Uri 协议校验
            if (u.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)) continue;
            if (!Uri.TryCreate(u, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https" && uri.Scheme != "ftp"))
            {
                await WriteJson(ctx, 400, new { error = "bad_request", message = $"不支持的下载地址: {u}" }).ConfigureAwait(false);
                return;
            }
        }

        // 重复检测只针对"未结束"任务（下载中/排队/暂停/做种中），已完成/失败的历史任务不算重复；
        // 命中时跳过添加，并通知 UI 层弹窗提醒用户该任务已在下载中
        var dups = await _engine.FindActiveDuplicatesAsync(urls).ConfigureAwait(false);
        if (dups.Count > 0)
        {
            _log($"API 跳过重复任务（客户端已存在）: {string.Join(", ", urls)}");
            var names = string.Join("\n", dups.Select(t =>
                "• " + (string.IsNullOrEmpty(t.Name) ? (t.Urls.FirstOrDefault() ?? t.Gid) : t.Name)));
            try { DuplicateTaskNoticeRequested?.Invoke(names); }
            catch (Exception ex) { _log($"API 重复任务提醒弹窗失败: {ex.Message}"); }
            await WriteJson(ctx, 200, new { ok = true, duplicate = true }).ConfigureAwait(false);
            return;
        }

        var dir = string.IsNullOrWhiteSpace(dlReq.Dir) ? null : dlReq.Dir;
        var limit = dlReq.SpeedLimit;

        // 磁力链接逐条添加（引擎内做 BT 参数特判，不能与普通地址混批），普通地址按原逻辑添加
        var magnets = urls.Where(u => u.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)).ToList();
        var normal = urls.Except(magnets).ToList();

        // 单条磁力链接 = 浏览器/系统唤起的确认场景：不再静默建任务，
        // 唤起 UI 层独立确认窗口由用户决定；立即应答避免扩展长时间等待
        if (magnets.Count == 1 && normal.Count == 0)
        {
            _log($"API 收到磁力链接，等待用户在确认窗口确认: {magnets[0]}");
            try { MagnetConfirmRequested?.Invoke(magnets[0]); }
            catch (Exception ex) { _log($"唤起磁力确认窗口失败: {ex.Message}"); }
            await WriteJson(ctx, 200, new { ok = true, confirm = true }).ConfigureAwait(false);
            return;
        }

        var gid = string.Empty;
        try
        {
            foreach (var m in magnets)
            {
                var t = await _engine.AddTaskAsync(new NewTaskRequest
                {
                    Urls = new List<string> { m },
                    Directory = dir,
                    SpeedLimit = limit
                }).ConfigureAwait(false);
                gid = string.IsNullOrEmpty(gid) ? t.Gid : gid;
            }

            if (normal.Count > 0)
            {
                var newReq = new NewTaskRequest
                {
                    Urls = normal,
                    Directory = dir,
                    FileName = string.IsNullOrWhiteSpace(dlReq.FileName) ? null : SanitizeFileName(dlReq.FileName),
                    Connections = dlReq.Connections,
                    SpeedLimit = limit,
                    Referer = dlReq.Referer,
                    Headers = dlReq.Headers
                };
                var task = await _engine.AddTaskAsync(newReq).ConfigureAwait(false);
                gid = string.IsNullOrEmpty(gid) ? task.Gid : gid;
            }
        }
        catch (DuplicateTaskException dex)
        {
            // 种子 infohash 重复（任务已存在/做种中）：与 URL 重复同等应答，扩展端提示即可
            _log($"API 跳过重复种子任务: {dex.Message}");
            await WriteJson(ctx, 200, new { ok = true, duplicate = true, message = dex.Message }).ConfigureAwait(false);
            return;
        }

        _log($"API 新建任务: {gid} <- {string.Join(", ", urls)}");
        await WriteJson(ctx, 200, new { ok = true, gid }).ConfigureAwait(false);
    }

    private async Task HandleTaskAction(HttpListenerContext ctx, string gid, string action)
    {
        try
        {
            switch (action)
            {
                case "pause": await _engine.PauseAsync(gid).ConfigureAwait(false); break;
                case "resume": await _engine.ResumeAsync(gid).ConfigureAwait(false); break;
                case "remove": await _engine.RemoveAsync(gid).ConfigureAwait(false); break;
                case "redownload":
                    var t = await _engine.RedownloadAsync(gid).ConfigureAwait(false);
                    await WriteJson(ctx, 200, new { ok = true, gid = t.Gid }).ConfigureAwait(false);
                    return;
            }
            await WriteJson(ctx, 200, new { ok = true }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await WriteJson(ctx, 400, new { error = "task_action_failed", message = ex.Message }).ConfigureAwait(false);
        }
    }

    private async Task HandleSettingsPatch(HttpListenerContext ctx)
    {
        var body = await ReadBodyAsync(ctx.Request).ConfigureAwait(false);
        try
        {
            var patch = JsonSerializer.Deserialize<ApiSettingsPatch>(body, JsonOpts);
            if (patch == null)
            {
                await WriteJson(ctx, 400, new { error = "bad_request" }).ConfigureAwait(false);
                return;
            }
            _settings.Update(s =>
            {
                // 逐字段比对后返回"是否真的变了"：原先无条件 return true，
                // 于是内容完全相同的 PATCH（扩展/脚本重复提交同一份设置）也会触发
                // 一次全量主题重算 + 一次 settings.json 落盘（实测约 240ms CPU/次）。
                var changed = false;
                if (patch.DefaultDownloadDir != null && s.DefaultDownloadDir != patch.DefaultDownloadDir)
                {
                    s.DefaultDownloadDir = patch.DefaultDownloadDir;
                    changed = true;
                }
                if (patch.MaxConcurrentDownloads is > 0 and <= 32 && s.MaxConcurrentDownloads != patch.MaxConcurrentDownloads.Value)
                {
                    s.MaxConcurrentDownloads = patch.MaxConcurrentDownloads.Value;
                    changed = true;
                }
                if (patch.DefaultConnections is > 0 and <= 64 && s.DefaultConnections != patch.DefaultConnections.Value)
                {
                    s.DefaultConnections = patch.DefaultConnections.Value;
                    changed = true;
                }
                if (patch.NotificationsEnabled.HasValue && s.NotificationsEnabled != patch.NotificationsEnabled.Value)
                {
                    s.NotificationsEnabled = patch.NotificationsEnabled.Value;
                    changed = true;
                }
                if (patch.Theme != null && Enum.TryParse<ThemeMode>(patch.Theme, true, out var theme) && s.Theme != theme)
                {
                    s.Theme = theme;
                    changed = true;
                }
                if (patch.GlobalSpeedLimit is >= 0 && s.GlobalSpeedLimit != patch.GlobalSpeedLimit!.Value)
                {
                    s.GlobalSpeedLimit = patch.GlobalSpeedLimit!.Value;
                    changed = true;
                }
                if (patch.InterceptBrowserDownloads.HasValue && s.InterceptBrowserDownloads != patch.InterceptBrowserDownloads.Value)
                {
                    s.InterceptBrowserDownloads = patch.InterceptBrowserDownloads.Value;
                    changed = true;
                }
                if (patch.MinimizeToTrayOnClose.HasValue && s.MinimizeToTrayOnClose != patch.MinimizeToTrayOnClose.Value)
                {
                    s.MinimizeToTrayOnClose = patch.MinimizeToTrayOnClose.Value;
                    changed = true;
                }
                return changed;
            });
            await WriteJson(ctx, 200, new { ok = true }).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            await WriteJson(ctx, 400, new { error = "bad_request" }).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 是否放行该请求来源。
    /// 无 Origin 头 = 非浏览器调用（扩展 Service Worker 内部 fetch、curl、本项目测试脚本），放行；
    /// 浏览器来源只放行扩展（chrome-extension / edge 的 extension / moz-extension）与回环页面
    /// （本机调试页、本地测试站点）。用 Uri 解析而不是前缀匹配，避免
    /// "http://127.0.0.1.evil.com" 这类以回环地址开头的域名绕过。
    /// </summary>
    private static bool IsTrustedOrigin(string? origin)
    {
        if (string.IsNullOrEmpty(origin)) return true;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme is "chrome-extension" or "extension" or "moz-extension") return true;
        if (uri.Scheme is not ("http" or "https")) return false;
        return uri.IsLoopback;
    }

    private bool IsAuthorized(HttpListenerRequest req)
    {
        var expected = _settings.Current.ApiSecret;
        var header = req.Headers["X-Kokona-Secret"];
        if (!string.IsNullOrEmpty(header))
            return FixedTimeEquals(header, expected);
        var auth = req.Headers["Authorization"];
        if (!string.IsNullOrEmpty(auth) && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return FixedTimeEquals(auth["Bearer ".Length..].Trim(), expected);
        return false;
    }

    /// <summary>常量时间比较，避免时序侧信道。</summary>
    private static bool FixedTimeEquals(string a, string b)
    {
        var ab = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        if (ab.Length != bb.Length) return false;
        var diff = 0;
        for (var i = 0; i < ab.Length; i++) diff |= ab[i] ^ bb[i];
        return diff == 0;
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name) sb.Append(invalid.Contains(c) ? '_' : c);
        var result = sb.ToString().Trim();
        return string.IsNullOrEmpty(result) ? "download" : result;
    }

    private static async Task<string> ReadBodyAsync(HttpListenerRequest req)
    {
        using var reader = new StreamReader(req.InputStream, req.ContentEncoding);
        return await reader.ReadToEndAsync().ConfigureAwait(false);
    }

    private static async Task WriteJson(HttpListenerContext ctx, int status, object data)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        var json = JsonSerializer.Serialize(data, JsonOpts);
        var bytes = Encoding.UTF8.GetBytes(json);
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _listener.Stop(); } catch { }
    }

    public void Dispose()
    {
        Stop();
        try { _listener.Close(); } catch { }
        // 自有可释放资源必须释放（CA2213）：令牌源读操作在 Dispose 后仍安全，接受循环只读 IsCancellationRequested
        var cts = _cts;
        _cts = null;
        try { cts?.Dispose(); } catch { }
        _requestGate.Dispose();
    }
}
