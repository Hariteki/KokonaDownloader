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
    /// <summary>内部反序列化统一用这套选项（Aria2MultiCall 也复用它，保证两条解析路径语义一致）。</summary>
    internal static readonly JsonSerializerOptions JsonOpts = new()
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
        using var doc = await PostForDocumentAsync(payload, ct).ConfigureAwait(false);
        return ExtractResult(doc.RootElement);
    }

    /// <summary>发送自定义完整报文（用于 multicall 等特殊结构）。</summary>
    private async Task<JsonNode?> PostRawAsync(JsonObject payload, CancellationToken ct)
    {
        using var doc = await PostForDocumentAsync(payload, ct).ConfigureAwait(false);
        return ExtractResult(doc.RootElement);
    }

    /// <summary>
    /// 发送报文，并把响应体**直接从网络流**解析为 JsonDocument（唯一一次全量解析）。
    ///
    /// 为什么不先 ReadAsStringAsync：界面轮询的一次 multicall 响应在 1000 任务规模下实测 458 KB，
    /// 原先是"字节→string（≥85 KB 直接进 LOH）→ JsonNode DOM → 逐元素再序列化"，同一份数据被复制三遍
    /// （实测每轮 3,995 KB 垃圾）；改成流式 JsonDocument 后降到 1,445 KB（约 −64 %）。
    /// 只有 HTTP 失败分支才把正文读成字符串——错误体很小，正常响应却可能上百 KB。
    /// </summary>
    private async Task<JsonDocument> PostForDocumentAsync(JsonObject payload, CancellationToken ct)
    {
        using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        var resp = await _http.PostAsync(_endpoint, content, ct).ConfigureAwait(false);
        try
        {
            if (!resp.IsSuccessStatusCode)
                throw new Aria2RpcException((int)resp.StatusCode,
                    $"HTTP {(int)resp.StatusCode}: {Truncate(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false))}");
            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return await JsonDocument.ParseAsync(stream, default, ct).ConfigureAwait(false);
        }
        finally { resp.Dispose(); }
    }

    /// <summary>取 result 节点。语义与旧的 string 版 ParseResponse 完全一致：
    /// error 对象存在即抛 Aria2RpcException（code/message 原样），无 result 则返回 null。</summary>
    private static JsonNode? ExtractResult(JsonElement root)
    {
        ThrowIfError(root);
        return root.TryGetProperty("result", out var r) && r.ValueKind != JsonValueKind.Undefined
            ? r.Deserialize<JsonNode>() // .NET 8 的 JsonElement 没有 AsNode()，走 JsonNode 转换器
            : null;
    }

    /// <summary>响应带 error 对象时抛出 RPC 异常（code/message 原样透传，缺省分别取 -1 / "未知错误"）。</summary>
    private static void ThrowIfError(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        if (!root.TryGetProperty("error", out var err) || err.ValueKind != JsonValueKind.Object) return;
        var code = err.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out var ci)
            ? ci : -1;
        var msg = err.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
        throw new Aria2RpcException(code, msg ?? "未知错误");
    }

    /// <summary>
    /// 轮询专用的强类型 multicall：整份响应只解析一次成 JsonDocument，各子调用结果以 JsonElement
    /// （指向同一份文档、零额外拷贝）暴露，由调用方按需反序列化成目标类型。
    /// 相比 MultiCallAsync（JsonNode 版）省掉整棵 DOM 与"每个任务再序列化一遍"：
    /// 1000 任务/轮实测分配 3,995 KB → 1,445 KB，且不再产生 LOH 字符串。
    /// 返回值持有文档，用完必须 Dispose（调用方用 using）。
    /// </summary>
    public async Task<Aria2MultiCall> MultiCallBufferedAsync(
        IEnumerable<(string Method, object?[] Args)> calls, CancellationToken ct = default)
    {
        var payload = BuildMultiCallPayload(calls);
        var doc = await PostForDocumentAsync(payload, ct).ConfigureAwait(false);
        try
        {
            ThrowIfError(doc.RootElement);
            var subs = new List<JsonElement>();
            if (doc.RootElement.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in result.EnumerateArray())
                {
                    // multicall 成功时每个结果是单元素数组 [value]；失败时是 fault 对象，原样保留由调用方判形状
                    subs.Add(item.ValueKind == JsonValueKind.Array && item.GetArrayLength() > 0 ? item[0] : item);
                }
            }
            return new Aria2MultiCall(doc, subs);
        }
        catch
        {
            doc.Dispose(); // 出错路径没人接手，立即释放
            throw;
        }
    }

    private JsonObject BuildMultiCallPayload(IEnumerable<(string Method, object?[] Args)> calls)
    {
        var list = calls.Select(c => new JsonObject
        {
            ["methodName"] = c.Method,
            ["params"] = BuildParams(c.Args, includeToken: true)
        }).ToArray();

        return new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = Interlocked.Increment(ref _id).ToString(),
            ["method"] = "system.multicall",
            ["params"] = new JsonArray { new JsonArray(list) }
        };
    }

    /// <summary>批量调用（system.multicall），返回每个子调用的 result。
    /// aria2 要求：外层 params 为单个数组（不再单独放密钥），密钥放在每个子调用的 params 首位。
    /// 小结果集（添加/暂停/删除回执）走这个版本即可；界面轮询请改用 MultiCallBufferedAsync。</summary>
    public async Task<List<JsonNode?>> MultiCallAsync(IEnumerable<(string Method, object?[] Args)> calls, CancellationToken ct = default)
    {
        var payload = BuildMultiCallPayload(calls);
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

    /// <summary>添加 .torrent 种子任务。aria2.addTorrent 要求种子内容为 base64 编码；
    /// 第二参数为可选的 Web Seed（HTTP 直链辅助源），此处传空。</summary>
    public async Task<string> AddTorrentAsync(byte[] torrentData, NewTaskRequest req, CancellationToken ct = default)
    {
        var options = BuildOptions(req);
        var base64 = Convert.ToBase64String(torrentData);
        var gid = await CallAsync("aria2.addTorrent", new object?[] { base64, Array.Empty<string>(), options }, ct).ConfigureAwait(false);
        return gid?.GetValue<string>() ?? throw new Aria2RpcException(-1, "addTorrent 未返回 gid");
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

    /// <summary>
    /// 一次 multicall 拿齐 active / waiting / stopped 三份列表。
    /// 等价于三次独立调用，但只花**一次 HTTP 往返**——界面每秒刷新列表、下载前做重复预检都走它，
    /// 是本项目最主要的周期性 RPC 开销来源。
    /// </summary>
    public async Task<(List<Aria2TaskStatus> Active, List<Aria2TaskStatus> Waiting, List<Aria2TaskStatus> Stopped)> TellAllAsync(
        int offset = 0, int num = 1000, CancellationToken ct = default)
    {
        var results = await MultiCallAsync(new (string, object?[])[]
        {
            ("aria2.tellActive", Array.Empty<object?>()),
            ("aria2.tellWaiting", new object?[] { offset, num }),
            ("aria2.tellStopped", new object?[] { offset, num })
        }, ct).ConfigureAwait(false);

        return (DeserializeList(results.ElementAtOrDefault(0)),
                DeserializeList(results.ElementAtOrDefault(1)),
                DeserializeList(results.ElementAtOrDefault(2)));
    }

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

    /// <summary>通用全局选项修改（如热更新 bt-tracker 列表）。</summary>
    public async Task ChangeGlobalOptionAsync(Dictionary<string, string> options, CancellationToken ct = default)
    {
        var json = new JsonObject();
        foreach (var kv in options) json[kv.Key] = kv.Value;
        await CallAsync("aria2.changeGlobalOption", new object?[] { json }, ct).ConfigureAwait(false);
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

    /// <summary>把 NewTaskRequest 翻成 aria2 addUri 的 options 对象。
    /// 公开只为单测（allow-overwrite / continue / dir / split 这几条是第八轮修复的行为契约）。</summary>
    public static JsonObject BuildOptions(NewTaskRequest req)
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
        // 新建任务总是从零下载：全局 --continue=true 仅用于重启后恢复未完成任务的断点续传。
        options["continue"] = "false";
        // 全局 --allow-overwrite=true 是给会话恢复/断点续传用的（aria2 必须能写回已存在的部分文件）。
        // 新建下载必须逐条关掉它：否则"重复下载同一链接"会静默覆盖用户已有文件（第八轮 P1-2）。
        // 关掉后 aria2 自己改名（file.bin → file.1.bin），旧文件完好；BT/磁力必须保持 true，
        // 因为 BT 要复用目录里的同名文件做分片校验。
        options["allow-overwrite"] = req.AllowOverwriteExisting ? "true" : "false";
        if (req.ExtraOptions != null)
        {
            foreach (var kv in req.ExtraOptions) options[kv.Key] = kv.Value;
        }
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

    private static string Truncate(string s) => s.Length > 200 ? s[..200] : s;

    public void Dispose() => _http.Dispose();
}

/// <summary>
/// system.multicall 的**文档态**结果：整份响应保存在一份 JsonDocument 里（全进程只此一份解析），
/// 按子调用序号取强类型结果，中途不再产生 JsonNode。
/// JsonElement 指向文档内部缓冲，因此本对象 Dispose 后所有取出的 JsonElement 立即失效——
/// 正确用法是把需要的数据在 using 作用域内取完（DownloadEngine 轮询即如此）。
/// </summary>
public sealed class Aria2MultiCall : IDisposable
{
    private JsonDocument? _doc;
    private readonly List<JsonElement> _results;

    internal Aria2MultiCall(JsonDocument doc, List<JsonElement> results)
    {
        _doc = doc;
        _results = results;
    }

    /// <summary>子调用结果个数（顺序与请求一致）。aria2 出错时可能少于请求数。</summary>
    public int Count => _results.Count;

    /// <summary>第 index 个子调用是否有返回值（越界、JSON null 算没有）。</summary>
    public bool HasResult(int index)
        => index >= 0 && index < _results.Count && _results[index].ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

    /// <summary>第 index 个子调用是否返回了 aria2 的 fault 结构（{"faultCode":…,"faultString":…}）。
    /// 此时 DeserializeList 会得到空列表，调用方若想知道"到底有没有出错"可以看这个。</summary>
    public bool IsFault(int index)
        => index >= 0 && index < _results.Count && _results[index].ValueKind == JsonValueKind.Object
           && _results[index].TryGetProperty("faultCode", out _);

    /// <summary>把第 index 个子调用结果反序列化为列表；结果不是数组（例如 aria2 的 fault 结构）时返回空列表。</summary>
    public List<T> DeserializeList<T>(int index)
    {
        var list = new List<T>();
        if (index < 0 || index >= _results.Count || _results[index].ValueKind != JsonValueKind.Array) return list;
        foreach (var item in _results[index].EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Null) continue;
            var v = JsonSerializer.Deserialize<T>(item, Aria2RpcClient.JsonOpts);
            if (v is not null) list.Add(v);
        }
        return list;
    }

    /// <summary>把第 index 个子调用结果反序列化为单个对象；越界/为 null 时返回 default。</summary>
    public T? Deserialize<T>(int index)
    {
        if (index < 0 || index >= _results.Count) return default;
        var el = _results[index];
        if (el.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return default;
        // fault 结构（对象但形状不对）交给反序列化器：字段对不上会得到一个空对象，与原 string 版行为一致
        return JsonSerializer.Deserialize<T>(el, Aria2RpcClient.JsonOpts);
    }

    public void Dispose()
    {
        _doc?.Dispose();
        _doc = null;
    }
}
