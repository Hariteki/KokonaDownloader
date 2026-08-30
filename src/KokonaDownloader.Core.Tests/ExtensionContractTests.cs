using System.Net;
using System.Text;
using System.Text.Json;
using KokonaDownloader.Core.Api;
using KokonaDownloader.Core.Engine;
using KokonaDownloader.Core.Settings;

namespace KokonaDownloader.Core.Tests;

/// <summary>
/// 浏览器扩展契约集成测试：以扩展 background.js 完全相同的方式
/// （相同请求头、相同 JSON 字段）访问真实 API 服务，验证两端契约一致。
/// </summary>
public class ExtensionContractTests : IAsyncLifetime
{
    private TestEnv.FileServer _fileServer = null!;
    private DownloadEngine _engine = null!;
    private ApiService _api = null!;
    private SettingsStore _settings = null!;
    private string _workDir = null!;
    private int _apiPort;
    private HttpClient _http = null!;
    private const string Secret = "ext-contract-secret";

    public async Task InitializeAsync()
    {
        _fileServer = new TestEnv.FileServer();
        _workDir = TestEnv.NewWorkDir();
        _apiPort = TestEnv.GetFreePort();

        _settings = new SettingsStore(Path.Combine(_workDir, "settings.json"));
        _settings.Update(s =>
        {
            s.ApiSecret = Secret;
            s.ApiPort = _apiPort;
            s.DefaultDownloadDir = Path.Combine(_workDir, "downloads");
            return true;
        });

        var engineConfig = new EngineConfig
        {
            Aria2Path = TestEnv.Aria2Path,
            WorkDir = Path.Combine(_workDir, "engine"),
            DefaultDownloadDir = _settings.Current.DefaultDownloadDir,
            RpcPort = TestEnv.GetFreePort(),
            RpcSecret = "engine-secret",
            PollIntervalMs = 300
        };
        _engine = new DownloadEngine(engineConfig, new TaskStore(Path.Combine(_workDir, "tasks.json")));
        await _engine.StartAsync();

        _api = new ApiService(_engine, _settings, _apiPort);
        _api.Start();

        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_apiPort}") };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        _api.Dispose();
        await _engine.DisposeAsync();
        _fileServer.Dispose();
        try { Directory.Delete(_workDir, true); } catch { }
    }

    /// <summary>模拟扩展 forwardDownload：POST /api/download，带 X-Kokona-Secret 头，
    /// body 为 buildDownloadPayload 产物 { urls, filename, referer }。</summary>
    private async Task<HttpResponseMessage> ExtensionSendDownload(object payload)
    {
        var msg = new HttpRequestMessage(HttpMethod.Post, "/api/download");
        msg.Headers.Add("X-Kokona-Secret", Secret);
        msg.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        return await _http.SendAsync(msg);
    }

    [Fact]
    public async Task 扩展Ping探测免密钥()
    {
        // background.js refreshStatus → GET /api/ping（无密钥头）
        var resp = await _http.GetAsync("/api/ping");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.True(json.RootElement.GetProperty("ok").GetBoolean());
        Assert.True(json.RootElement.TryGetProperty("version", out _));
    }

    [Fact]
    public async Task 扩展转发下载契约一致()
    {
        _fileServer.AddFile("ext-file.bin", new byte[32 * 1024]);
        var url = _fileServer.Url("ext-file.bin");

        // 与 buildDownloadPayload 输出完全一致
        var resp = await ExtensionSendDownload(new
        {
            urls = new[] { url },
            filename = "ext-file.bin",
            referer = "https://example.com/page"
        });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.True(json.RootElement.GetProperty("ok").GetBoolean());
        Assert.False(string.IsNullOrEmpty(json.RootElement.GetProperty("gid").GetString()));

        // 等待完成，验证文件名按扩展指定
        var deadline = DateTime.Now.AddSeconds(20);
        while (DateTime.Now < deadline)
        {
            var msg = new HttpRequestMessage(HttpMethod.Get, "/api/tasks");
            msg.Headers.Add("X-Kokona-Secret", Secret);
            var tasks = JsonDocument.Parse(await (await _http.SendAsync(msg)).Content.ReadAsStringAsync());
            foreach (var t in tasks.RootElement.EnumerateArray())
            {
                if (t.GetProperty("state").GetString() == "completed" &&
                    t.GetProperty("name").GetString() == "ext-file.bin")
                {
                    Assert.Equal(100, t.GetProperty("progress").GetDouble());
                    return;
                }
            }
            await Task.Delay(300);
        }
        Assert.Fail("扩展转发的下载未完成");
    }

    [Fact]
    public async Task 扩展密钥错误得到401()
    {
        // background.js 对 401 的处理：提示「连接密钥错误」
        var msg = new HttpRequestMessage(HttpMethod.Post, "/api/download");
        msg.Headers.Add("X-Kokona-Secret", "wrong-secret");
        msg.Content = new StringContent("{\"urls\":[\"http://127.0.0.1/x\"]}", Encoding.UTF8, "application/json");
        var resp = await _http.SendAsync(msg);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task 扩展空密钥得到401()
    {
        // 扩展未配置密钥时发送空串
        var msg = new HttpRequestMessage(HttpMethod.Post, "/api/download");
        msg.Headers.Add("X-Kokona-Secret", "");
        msg.Content = new StringContent("{\"urls\":[\"http://127.0.0.1/x\"]}", Encoding.UTF8, "application/json");
        var resp = await _http.SendAsync(msg);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task 扩展来源CORS可用()
    {
        // 扩展 fetch 携带 Origin: chrome-extension://...，服务端必须放行预检
        var msg = new HttpRequestMessage(HttpMethod.Options, "/api/download");
        msg.Headers.Add("Origin", "chrome-extension://abcdefghijklmnop");
        msg.Headers.Add("Access-Control-Request-Method", "POST");
        var resp = await _http.SendAsync(msg);
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        Assert.Equal("*", resp.Headers.GetValues("Access-Control-Allow-Origin").First());
        var allowHeaders = string.Join(",", resp.Headers.GetValues("Access-Control-Allow-Headers"));
        Assert.Contains("X-Kokona-Secret", allowHeaders);
    }

    [Fact]
    public async Task 扩展不支持协议被拒绝()
    {
        // 扩展逻辑层已过滤，但服务端兜底：blob/data 等协议返回 400
        var resp = await ExtensionSendDownload(new { urls = new[] { "blob:https://example.com/uuid" } });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }
}
