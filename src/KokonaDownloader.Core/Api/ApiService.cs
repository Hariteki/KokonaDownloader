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
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public int Port { get; }
    public bool IsListening => _listener.IsListening;
    public string Version { get; } = "1.0.0";

    /// <summary>收到单条磁力链接（浏览器扩展/系统协议转发）：UI 层订阅后弹独立确认窗口，由用户决定是否下载。</summary>
    public event Action<string>? MagnetConfirmRequested;

    public ApiService(DownloadEngine engine, SettingsStore settings, int port, Action<string>? log = null)
    {
        _engine = engine;
        _settings = settings;
        _log = log ?? (_ => { });
        Port = port;
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
            try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
            catch { break; }
            _ = Task.Run(() => HandleSafe(ctx), ct);
        }
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

        // CORS
        resp.Headers["Access-Control-Allow-Origin"] = "*";
        resp.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
        resp.Headers["Access-Control-Allow-Headers"] = "Content-Type, X-Kokona-Secret, Authorization";
        resp.Headers["Access-Control-Max-Age"] = "600";
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
                    interceptBrowserDownloads = _settings.Current.InterceptBrowserDownloads
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

        // 以客户端任务列表为唯一事实源做重复检测：
        // 任务列表里已有相同 URL 则不再重复添加；用户删除任务后自然允许重新下载
        var existingUrls = (await _engine.GetAllTasksAsync().ConfigureAwait(false))
            .SelectMany(t => t.Urls)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (urls.Any(existingUrls.Contains))
        {
            _log($"API 跳过重复任务（客户端已存在）: {string.Join(", ", urls)}");
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
                if (patch.DefaultDownloadDir != null) s.DefaultDownloadDir = patch.DefaultDownloadDir;
                if (patch.MaxConcurrentDownloads is > 0 and <= 32) s.MaxConcurrentDownloads = patch.MaxConcurrentDownloads.Value;
                if (patch.DefaultConnections is > 0 and <= 64) s.DefaultConnections = patch.DefaultConnections.Value;
                if (patch.NotificationsEnabled.HasValue) s.NotificationsEnabled = patch.NotificationsEnabled.Value;
                if (patch.Theme != null && Enum.TryParse<ThemeMode>(patch.Theme, true, out var theme)) s.Theme = theme;
                if (patch.GlobalSpeedLimit is >= 0) s.GlobalSpeedLimit = patch.GlobalSpeedLimit!.Value;
                if (patch.InterceptBrowserDownloads.HasValue) s.InterceptBrowserDownloads = patch.InterceptBrowserDownloads.Value;
                return true;
            });
            await WriteJson(ctx, 200, new { ok = true }).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            await WriteJson(ctx, 400, new { error = "bad_request" }).ConfigureAwait(false);
        }
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
    }
}
