using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KokonaDownloader.Core.Engine;

namespace KokonaDownloader.Core.Tests;

/// <summary>
/// RPC 解析路径的回归测试（第六轮开销审计 §12-C O-1 的落地护栏）。
///
/// 背景：界面轮询一次 multicall 在 1000 任务规模下响应实测 458 KB，旧实现是
/// "ReadAsStringAsync（LOH 字符串）→ JsonNode DOM → 逐元素再序列化"，实测每轮 3,995 KB 垃圾；
/// 改成"流式 JsonDocument + 子调用直接反序列化"后降到 1,445 KB。
/// 这里用一个假的 aria2 RPC 服务把两条路径（文档态 / JsonNode 态）的输出对齐，
/// 保证省分配的同时**解析语义一字不差**：字段值、缺省、fault 结构、error 报文、HTTP 错误。
/// </summary>
public class RpcParsePathTests
{
    /// <summary>假的 aria2 JSON-RPC：固定回包 + 记录请求，便于逐字节控制响应形状。</summary>
    private sealed class FakeRpc : IDisposable
    {
        private readonly HttpListener _listener = new();
        public int Port { get; }
        public int StatusCode = 200;
        public string Body = "{}";
        public int RequestCount;
        public readonly List<string> RequestBodies = new();

        public FakeRpc()
        {
            Port = TestEnv.GetFreePort();
            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _listener.Start();
            _ = Task.Run(Loop);
        }

        private async Task Loop()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); } catch { break; }
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var body = await new StreamReader(ctx.Request.InputStream).ReadToEndAsync();
                        lock (RequestBodies) RequestBodies.Add(body);
                        Interlocked.Increment(ref RequestCount);
                        var bytes = Encoding.UTF8.GetBytes(Body);
                        ctx.Response.StatusCode = StatusCode;
                        ctx.Response.ContentType = "application/json";
                        ctx.Response.ContentLength64 = bytes.Length;
                        await ctx.Response.OutputStream.WriteAsync(bytes);
                    }
                    catch { }
                    finally { try { ctx.Response.Close(); } catch { } }
                });
            }
        }

        public void Dispose() { try { _listener.Stop(); _listener.Close(); } catch { } }
    }

    private static JsonObject TaskObj(string gid, string status, long total, long done) => new()
    {
        ["gid"] = gid,
        ["status"] = status,
        // aria2 的数值字段本来就是字符串形式，这里刻意保持原样（解析器靠 AllowReadingFromString 兜住）
        ["totalLength"] = total.ToString(),
        ["completedLength"] = done.ToString(),
        ["downloadSpeed"] = "1024",
        ["uploadSpeed"] = "0",
        ["connections"] = "4",
        ["dir"] = "C:/down",
        ["files"] = new JsonArray(new JsonObject
        {
            ["path"] = $"C:/down/{gid}.bin",
            ["length"] = total.ToString(),
            ["uris"] = new JsonArray(new JsonObject { ["uri"] = $"http://127.0.0.1/{gid}", ["status"] = "used" })
        })
    };

    /// <summary>system.multicall 的成功响应：每个子调用结果都被包成单元素数组 [value]。</summary>
    private static string MultiCallBody(JsonArray active, JsonArray waiting, JsonArray stopped, JsonObject stat)
        => new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = "1",
            ["result"] = new JsonArray(
                new JsonArray(active),
                new JsonArray(waiting),
                new JsonArray(stopped),
                new JsonArray(stat))
        }.ToJsonString();

    private static (JsonArray Active, JsonArray Waiting, JsonArray Stopped, JsonObject Stat) SamplePayload()
    {
        var active = new JsonArray(TaskObj("aaaa1", "active", 1000, 400), TaskObj("aaaa2", "active", 2000, 50));
        var waiting = new JsonArray(TaskObj("bbbb1", "waiting", 3000, 0));
        var stopped = new JsonArray(TaskObj("cccc1", "complete", 4000, 4000), TaskObj("cccc2", "error", 5000, 12));
        var stat = new JsonObject
        {
            ["downloadSpeed"] = "2048",
            ["uploadSpeed"] = "512",
            ["numActive"] = "2",
            ["numWaiting"] = "1",
            ["numStopped"] = "2"
        };
        return (active, waiting, stopped, stat);
    }

    private static (FakeRpc Server, Aria2RpcClient Client) Connected()
    {
        var server = new FakeRpc();
        return (server, new Aria2RpcClient("127.0.0.1", server.Port, "s3cret"));
    }

    [Fact]
    public async Task 文档态multicall解析出任务列表与统计()
    {
        var (server, client) = Connected();
        try
        {
            var (a, w, s, stat) = SamplePayload();
            server.Body = MultiCallBody(a, w, s, stat);

            using var call = await client.MultiCallBufferedAsync(new (string, object?[])[]
            {
                ("aria2.tellActive", Array.Empty<object?>()),
                ("aria2.tellWaiting", new object?[] { 0, 1000 }),
                ("aria2.tellStopped", new object?[] { 0, 1000 }),
                ("aria2.getGlobalStat", Array.Empty<object?>())
            });

            Assert.Equal(4, call.Count);
            var active = call.DeserializeList<Aria2TaskStatus>(0);
            Assert.Equal(2, active.Count);
            Assert.Equal("aaaa1", active[0].Gid);
            Assert.Equal(1000, active[0].TotalLength);     // 字符串数值照常解析
            Assert.Equal(400, active[0].CompletedLength);
            Assert.Equal("C:/down/aaaa1.bin", active[0].Files![0].Path);

            Assert.Single(call.DeserializeList<Aria2TaskStatus>(1));
            Assert.Equal(2, call.DeserializeList<Aria2TaskStatus>(2).Count);

            var gstat = call.Deserialize<Aria2GlobalStat>(3);
            Assert.Equal(2048, gstat!.DownloadSpeed);
            Assert.Equal(512, gstat.UploadSpeed);
            Assert.Equal(2, gstat.NumActive);
            Assert.Equal(1, gstat.NumWaiting);
            Assert.Equal(2, gstat.NumStopped);

            // 请求侧形状不能被重构改掉：外层 params 只有一个数组，子调用各自带密钥
            Assert.Contains("system.multicall", server.RequestBodies[0]);
            Assert.Contains("token:s3cret", server.RequestBodies[0]);
        }
        finally { server.Dispose(); client.Dispose(); }
    }

    [Fact]
    public async Task 文档态与JsonNode版multicall结果一致()
    {
        var (server, client) = Connected();
        try
        {
            var (a, w, s, stat) = SamplePayload();
            server.Body = MultiCallBody(a, w, s, stat);
            var calls = new (string, object?[])[]
            {
                ("aria2.tellActive", Array.Empty<object?>()),
                ("aria2.tellWaiting", new object?[] { 0, 1000 }),
                ("aria2.tellStopped", new object?[] { 0, 1000 }),
                ("aria2.getGlobalStat", Array.Empty<object?>())
            };

            List<JsonNode?> nodes;
            using (var buffered = await client.MultiCallBufferedAsync(calls))
            {
                nodes = await client.MultiCallAsync(calls);
                var gidsDoc = Enumerable.Range(0, 3)
                    .SelectMany(i => buffered.DeserializeList<Aria2TaskStatus>(i).Select(t => t.Gid))
                    .ToList();
                var gidsNode = nodes.Take(3)
                    .SelectMany(n => n is JsonArray arr
                        ? arr.Select(x => x?["gid"]?.GetValue<string>() ?? string.Empty)
                        : Enumerable.Empty<string>())
                    .ToList();
                Assert.Equal(gidsNode, gidsDoc);
                Assert.Equal(new[] { "aaaa1", "aaaa2", "bbbb1", "cccc1", "cccc2" }, gidsDoc);
            }
        }
        finally { server.Dispose(); client.Dispose(); }
    }

    [Fact]
    public async Task 子调用返回fault结构时按空列表处理()
    {
        var (server, client) = Connected();
        try
        {
            // aria2 的 multicall 里，失败的子调用直接是 fault 对象（不包 [value]）
            server.Body = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = "1",
                ["result"] = new JsonArray(
                    new JsonObject { ["faultCode"] = 1, ["faultString"] = "HTTP code 404" },
                    new JsonArray(new JsonArray(TaskObj("bbbb1", "waiting", 1, 1))),
                    new JsonArray(new JsonArray()),
                    new JsonArray(new JsonObject { ["downloadSpeed"] = "0", ["uploadSpeed"] = "0", ["numActive"] = "0", ["numWaiting"] = "1", ["numStopped"] = "0" }))
            }.ToJsonString();

            using var call = await client.MultiCallBufferedAsync(new (string, object?[])[]
            {
                ("aria2.tellActive", Array.Empty<object?>()),
                ("aria2.tellWaiting", new object?[] { 0, 1000 }),
                ("aria2.tellStopped", new object?[] { 0, 1000 }),
                ("aria2.getGlobalStat", Array.Empty<object?>())
            });

            Assert.Empty(call.DeserializeList<Aria2TaskStatus>(0)); // 不抛异常，按空列表处理
            Assert.True(call.IsFault(0));                            // 且可被识别为 aria2 的 fault 结构
            Assert.False(call.HasResult(7));                         // 越界子调用：没有返回值
            Assert.Single(call.DeserializeList<Aria2TaskStatus>(1));
            Assert.Equal(1, call.Deserialize<Aria2GlobalStat>(3)!.NumWaiting);
        }
        finally { server.Dispose(); client.Dispose(); }
    }

    [Fact]
    public async Task error报文两条解析路径都抛Aria2RpcException()
    {
        var (server, client) = Connected();
        try
        {
            server.Body = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = "1",
                ["error"] = new JsonObject { ["code"] = 2, ["message"] = "Access denied." }
            }.ToJsonString();

            var ex1 = await Assert.ThrowsAsync<Aria2RpcException>(() => client.CallAsync("aria2.tellActive"));
            Assert.Equal(2, ex1.Code);
            Assert.Contains("Access denied.", ex1.Message);

            var ex2 = await Assert.ThrowsAsync<Aria2RpcException>(() => client.MultiCallBufferedAsync(
                new (string, object?[])[] { ("aria2.tellActive", Array.Empty<object?>()) }));
            Assert.Equal(2, ex2.Code);
        }
        finally { server.Dispose(); client.Dispose(); }
    }

    [Fact]
    public async Task HTTP非200时异常带上状态码与响应正文()
    {
        var (server, client) = Connected();
        try
        {
            server.StatusCode = 500;
            server.Body = "internal error: too many requests";

            var ex = await Assert.ThrowsAsync<Aria2RpcException>(() => client.CallAsync("aria2.getVersion"));
            Assert.Equal(500, ex.Code);
            Assert.Contains("HTTP 500", ex.Message);
            Assert.Contains("too many requests", ex.Message);
        }
        finally { server.Dispose(); client.Dispose(); }
    }

    [Fact]
    public async Task 响应不是合法JSON时抛JsonException()
    {
        var (server, client) = Connected();
        try
        {
            // aria2 极少这样，但行为要钉住：解析失败必须是可预期的异常，而不是静默返回空列表
            server.Body = "<html>aria2 is not a jsonrpc server</html>";
            await Assert.ThrowsAnyAsync<JsonException>(() => client.CallAsync("aria2.getVersion"));
        }
        finally { server.Dispose(); client.Dispose(); }
    }

    [Fact]
    public async Task 大负载一千任务解析正确()
    {
        var (server, client) = Connected();
        try
        {
            // 与实测基准同量级：1000 条历史任务，响应约 450 KB
            var stopped = new JsonArray();
            for (var i = 0; i < 1000; i++) stopped.Add(TaskObj($"gid{i:x3}", "complete", 1_000_000 + i, 1_000_000 + i));
            server.Body = MultiCallBody(new JsonArray(), new JsonArray(), stopped, new JsonObject
            {
                ["downloadSpeed"] = "0",
                ["uploadSpeed"] = "0",
                ["numActive"] = "0",
                ["numWaiting"] = "0",
                ["numStopped"] = "1000"
            });

            using var call = await client.MultiCallBufferedAsync(new (string, object?[])[]
            {
                ("aria2.tellActive", Array.Empty<object?>()),
                ("aria2.tellWaiting", new object?[] { 0, 1000 }),
                ("aria2.tellStopped", new object?[] { 0, 1000 }),
                ("aria2.getGlobalStat", Array.Empty<object?>())
            });

            Assert.Empty(call.DeserializeList<Aria2TaskStatus>(0));
            var all = call.DeserializeList<Aria2TaskStatus>(2);
            Assert.Equal(1000, all.Count);
            Assert.Equal("gid000", all[0].Gid);
            Assert.Equal("gid3e7", all[^1].Gid);
            Assert.Equal(1000, call.Deserialize<Aria2GlobalStat>(3)!.NumStopped);
        }
        finally { server.Dispose(); client.Dispose(); }
    }
}
