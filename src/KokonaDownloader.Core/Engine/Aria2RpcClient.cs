using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KokonaDownloader.Core.Engine;

/// <summary>
/// aria2 JSON-RPC 2.0 客户端。通过 HTTP POST 与 aria2 通信。
/// 设计决策：不使用 WebSocket 推送（aria2 的 websocket 通知仍需轮询详情），
/// 采用短轮询 + multicall 批量查询，实现简单且足够高效。
/// </summary>
public sealed class Aria2RpcClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly string _token;
    private long _id;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
    };

    public Aria2RpcClient(string host, int port, string secret, TimeSpan? timeout = null)
    {
        _endpoint = new Uri($"http://{host}:{port}/jsonrpc");
        _token = $"token:{secret}";
        _http = new HttpClient { Timeout = timeout ?? TimeSpan.FromSeconds(10) };
    }

    /// <summary>执行单次 RPC 调用，返回 result 节点。</summary>
    public async Task<JsonNode?> CallAsync(string method, object?[]? args = null, CancellationToken ct = default)
    {
        var payload = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = Interlocked.Increment(ref _id).ToString(),
            ["method"] = method,
            ["params"] = BuildParams(args ?? Array.Empty<object?>())
        };
        var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        var resp = await _http.PostAsync(_endpoint, content, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new Aria2RpcException((int)resp.StatusCode, $"HTTP {(int)resp.StatusCode}: {Truncate(body)}");
        return ParseResponse(body);
    }

    /// <summary>发送自定义完整报文（用于 multicall 等特殊结构）。</summary>
    private async Task<JsonNode?> PostRawAsync(JsonObject payload, CancellationToken ct)
    {
        var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        var resp = await _http.PostAsync(_endpoint, content, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new Aria2RpcException((int)resp.StatusCode, $"HTTP {(int)resp.StatusCode}: {Truncate(body)}");
        return ParseResponse(body);
    }

    /// <summary>批量调用（system.multicall），返回每个子调用的 result。
    /// aria2 要求：外层 params 为单个数组（不再单独放密钥），密钥放在每个子调用的 params 首位。</summary>
    public async Task<List<JsonNode?>> MultiCallAsync(IEnumerable<(string Method, object?[] Args)> calls, CancellationToken ct = default)
    {
        var list = calls.Select(c => new JsonObject
        {
            ["methodName"] = c.Method,
            ["params"] = BuildParams(c.Args, includeToken: true)
        }).ToArray();

        var payload = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = Interlocked.Increment(ref _id).ToString(),
            ["method"] = "system.multicall",
            ["params"] = new JsonArray { new JsonArray(list) }
        };
        var result = await PostRawAsync(payload, ct).ConfigureAwait(false);
        var results = new List<JsonNode?>();
        if (result is JsonArray arr)
        {
            foreach (var item in arr)
            {
                // multicall 成功时每个结果是单元素数组 [value]
                if (item is JsonArray inner && inner.Count > 0) results.Add(inner[0]);
                else results.Add(item);
            }
        }
        return results;
    }

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try
        {
            var v = await CallAsync("aria2.getVersion", ct: ct).ConfigureAwait(false);
            return v != null;
        }
        catch
        {
            return false;
        }
    }

    #region aria2 高层方法

    public async Task<string> AddUriAsync(NewTaskRequest req, CancellationToken ct = default)
    {
        var options = BuildOptions(req);
        var gid = await CallAsync("aria2.addUri", new object?[] { req.Urls, options }, ct).ConfigureAwait(false);
        return gid?.GetValue<string>() ?? throw new Aria2RpcException(-1, "addUri 未返回 gid");
    }

    public async Task<List<string>> AddUriBatchAsync(IEnumerable<NewTaskRequest> requests, CancellationToken ct = default)
    {
        var calls = requests.Select(req => ("aria2.addUri", new object?[] { req.Urls, BuildOptions(req) })).ToList();
        var results = await MultiCallAsync(calls, ct).ConfigureAwait(false);
        return results.Select(r => r?.GetValue<string>() ?? string.Empty).ToList();
    }

    public async Task PauseAsync(string gid, CancellationToken ct = default)
        => await CallAsync("aria2.pause", new object?[] { gid }, ct).ConfigureAwait(false);

    public async Task UnpauseAsync(string gid, CancellationToken ct = default)
        => await CallAsync("aria2.unpause", new object?[] { gid }, ct).ConfigureAwait(false);

    public async Task PauseAllAsync(CancellationToken ct = default)
        => await CallAsync("aria2.pauseAll", ct: ct).ConfigureAwait(false);

    public async Task UnpauseAllAsync(CancellationToken ct = default)
        => await CallAsync("aria2.unpauseAll", ct: ct).ConfigureAwait(false);

    public async Task RemoveAsync(string gid, CancellationToken ct = default)
        => await CallAsync("aria2.remove", new object?[] { gid }, ct).ConfigureAwait(false);

    public async Task RemoveDownloadResultAsync(string gid, CancellationToken ct = default)
        => await CallAsync("aria2.removeDownloadResult", new object?[] { gid }, ct).ConfigureAwait(false);

    public async Task<Aria2TaskStatus?> TellStatusAsync(string gid, CancellationToken ct = default)
    {
        var node = await CallAsync("aria2.tellStatus", new object?[] { gid }, ct).ConfigureAwait(false);
        return node is null ? null : node.Deserialize<Aria2TaskStatus>(JsonOpts);
    }

    public async Task<List<Aria2TaskStatus>> TellActiveAsync(CancellationToken ct = default)
        => DeserializeList(await CallAsync("aria2.tellActive", ct: ct).ConfigureAwait(false));

    public async Task<List<Aria2TaskStatus>> TellWaitingAsync(int offset = 0, int num = 1000, CancellationToken ct = default)
        => DeserializeList(await CallAsync("aria2.tellWaiting", new object?[] { offset, num }, ct).ConfigureAwait(false));

    public async Task<List<Aria2TaskStatus>> TellStoppedAsync(int offset = 0, int num = 1000, CancellationToken ct = default)
        => DeserializeList(await CallAsync("aria2.tellStopped", new object?[] { offset, num }, ct).ConfigureAwait(false));

    public async Task<GlobalStat> GetGlobalStatAsync(CancellationToken ct = default)
    {
        var node = await CallAsync("aria2.getGlobalStat", ct: ct).ConfigureAwait(false);
        return node?.Deserialize<Aria2GlobalStat>(JsonOpts) is { } s
            ? new GlobalStat
            {
                DownloadSpeed = s.DownloadSpeed,
                UploadSpeed = s.UploadSpeed,
                NumActive = s.NumActive,
                NumWaiting = s.NumWaiting,
                NumStopped = s.NumStopped
            }
            : new GlobalStat();
    }

    public async Task SetGlobalSpeedLimitAsync(long bytesPerSec, CancellationToken ct = default)
    {
        var options = new JsonObject { ["max-overall-download-limit"] = bytesPerSec > 0 ? bytesPerSec.ToString() : "0" };
        await CallAsync("aria2.changeGlobalOption", new object?[] { options }, ct).ConfigureAwait(false);
    }

    public async Task SetTaskSpeedLimitAsync(string gid, long bytesPerSec, CancellationToken ct = default)
    {
        var options = new JsonObject { ["max-download-limit"] = bytesPerSec > 0 ? bytesPerSec.ToString() : "0" };
        await CallAsync("aria2.changeOption", new object?[] { gid, options }, ct).ConfigureAwait(false);
    }

    public async Task SaveSessionAsync(CancellationToken ct = default)
        => await CallAsync("aria2.saveSession", ct: ct).ConfigureAwait(false);

    public async Task ShutdownAsync(CancellationToken ct = default)
        => await CallAsync("aria2.shutdown", ct: ct).ConfigureAwait(false);

    #endregion

    private static List<Aria2TaskStatus> DeserializeList(JsonNode? node)
    {
        if (node is not JsonArray arr) return new List<Aria2TaskStatus>();
        var list = new List<Aria2TaskStatus>();
        foreach (var item in arr)
        {
            var t = item?.Deserialize<Aria2TaskStatus>(JsonOpts);
            if (t != null) list.Add(t);
        }
        return list;
    }

    private static JsonObject BuildOptions(NewTaskRequest req)
    {
        var options = new JsonObject();
        if (!string.IsNullOrWhiteSpace(req.Directory)) options["dir"] = req.Directory;
        if (!string.IsNullOrWhiteSpace(req.FileName)) options["out"] = req.FileName;
        if (req.Connections > 0)
        {
            options["split"] = req.Connections.ToString();
            options["min-split-size"] = "1M";
        }
        if (req.SpeedLimit > 0) options["max-download-limit"] = req.SpeedLimit.ToString();
        if (!string.IsNullOrWhiteSpace(req.Referer)) options["referer"] = req.Referer;
        if (req.Headers is { Count: > 0 }) options["header"] = new JsonArray(req.Headers.Select(h => (JsonNode)h).ToArray());
        // 新建任务总是从零下载：目标文件已存在时直接覆盖。
        // 全局 --continue=true 仅用于重启后恢复未完成任务的断点续传。
        options["continue"] = "false";
        return options;
    }

    private JsonArray BuildParams(object?[] args, bool includeToken = true)
    {
        var arr = new JsonArray();
        if (includeToken) arr.Add(_token);
        foreach (var arg in args)
            arr.Add(ToJsonNode(arg));
        return arr;
    }

    /// <summary>把任意参数递归转换为 JsonNode，保留对象/数组结构（不能 ToString）。</summary>
    private static JsonNode? ToJsonNode(object? value) => value switch
    {
        null => null,
        JsonNode jn => jn,
        string s => JsonValue.Create(s),
        int i => JsonValue.Create(i),
        long l => JsonValue.Create(l),
        bool b => JsonValue.Create(b),
        IEnumerable<object> e => new JsonArray(e.Select(ToJsonNode).ToArray()),
        _ => JsonValue.Create(value.ToString())
    };

    private static JsonNode? ParseResponse(string body)
    {
        var doc = JsonNode.Parse(body) ?? throw new Aria2RpcException(-32700, "无法解析 RPC 响应");
        if (doc["error"] is JsonObject err)
        {
            var code = err["code"]?.GetValue<int>() ?? -1;
            var msg = err["message"]?.GetValue<string>() ?? "未知错误";
            throw new Aria2RpcException(code, msg);
        }
        return doc["result"];
    }

    private static string Truncate(string s) => s.Length > 200 ? s[..200] : s;

    public void Dispose() => _http.Dispose();
}
