using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using KokonaDownloader.Core.Api;
using KokonaDownloader.Core.Engine;
using KokonaDownloader.Core.Settings;

namespace KokonaDownloader.Core.Tests;

/// <summary>
/// API 服务集成测试：真实启动引擎 + API + 文件服务器，
/// 覆盖鉴权、CORS、下载端点、任务操作、设置补丁。
/// </summary>
public class ApiServiceTests : IAsyncLifetime
{
    private TestEnv.FileServer _fileServer = null!;
    private DownloadEngine _engine = null!;
    private ApiService _api = null!;
    private SettingsStore _settings = null!;
    private string _workDir = null!;
    private int _apiPort;
    private HttpClient _http = null!;
    private const string Secret = "api-test-secret";

    public async Task InitializeAsync()
    {
        _fileServer = new TestEnv.FileServer();
        _workDir = TestEnv.NewWorkDir();
        _apiPort = TestEnv.GetFreePort();

        var settingsPath = Path.Combine(_workDir, "settings.json");
        _settings = new SettingsStore(settingsPath);
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
        // InitializeAsync 中途失败时（如高负载下 aria2 15s 未就绪）后面的字段还是 null：
        // 必须逐项判空清理，否则 NRE 会中断清理 → 引擎轮询循环留在测试进程里、aria2 泄漏。
        try { _http?.Dispose(); } catch { }
        try { _api?.Dispose(); } catch { }
        if (_engine != null) { try { await _engine.DisposeAsync(); } catch { } }
        try { _fileServer?.Dispose(); } catch { }
        try { if (_workDir != null) Directory.Delete(_workDir, true); } catch { }
    }

    private HttpRequestMessage Authed(HttpMethod method, string path, object? body = null)
    {
        var msg = new HttpRequestMessage(method, path);
        msg.Headers.Add("X-Kokona-Secret", Secret);
        if (body != null)
            msg.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        return msg;
    }

    [Fact]
    public async Task Ping免鉴权()
    {
        var resp = await _http.GetAsync("/api/ping");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"ok\":true", json);
    }

    [Fact]
    public async Task 无密钥返回401()
    {
        var resp = await _http.GetAsync("/api/tasks");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task 错误密钥返回401()
    {
        var msg = new HttpRequestMessage(HttpMethod.Get, "/api/tasks");
        msg.Headers.Add("X-Kokona-Secret", "wrong-secret");
        var resp = await _http.SendAsync(msg);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Bearer方式鉴权可用()
    {
        var msg = new HttpRequestMessage(HttpMethod.Get, "/api/tasks");
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Secret);
        var resp = await _http.SendAsync(msg);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task OPTIONS预检返回204且只回显受信任来源()
    {
        var msg = new HttpRequestMessage(HttpMethod.Options, "/api/download");
        msg.Headers.Add("Origin", "chrome-extension://abcdef");
        var resp = await _http.SendAsync(msg);
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        // 只回显具体来源，不再用通配 *（通配 + 自定义头鉴权 = 任意网页可带密钥调用本 API）
        Assert.Equal("chrome-extension://abcdef", resp.Headers.GetValues("Access-Control-Allow-Origin").First());
        Assert.Contains("X-Kokona-Secret", string.Join(",", resp.Headers.GetValues("Access-Control-Allow-Headers")));
    }

    [Fact]
    public async Task 不受信任的网页来源被拒绝()
    {
        // 任意网页带密钥调用本地 API：必须在鉴权之前挡掉。
        // 历史行为是 Allow-Origin: * → 网页可读响应，配合外泄密钥即可无声投递下载任务。
        foreach (var origin in new[] { "https://evil.example", "http://evil.example", "http://127.0.0.1.evil.com" })
        {
            var msg = Authed(HttpMethod.Post, "/api/download", new { url = "http://127.0.0.1:1/x.bin" });
            msg.Headers.Add("Origin", origin);
            var resp = await _http.SendAsync(msg);
            Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
            Assert.False(resp.Headers.Contains("Access-Control-Allow-Origin"));
        }

        // 预检同样拒绝：浏览器不会发出真实请求（自定义头必然触发预检）
        var pre = new HttpRequestMessage(HttpMethod.Options, "/api/download");
        pre.Headers.Add("Origin", "https://evil.example");
        pre.Headers.Add("Access-Control-Request-Headers", "x-kokona-secret");
        var preResp = await _http.SendAsync(pre);
        Assert.Equal(HttpStatusCode.Forbidden, preResp.StatusCode);
        Assert.False(preResp.Headers.Contains("Access-Control-Allow-Origin"));

        // ping 免鉴权也不该给不受信任来源放行（否则可被用于探测客户端是否在运行）
        var ping = new HttpRequestMessage(HttpMethod.Get, "/api/ping");
        ping.Headers.Add("Origin", "https://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await _http.SendAsync(ping)).StatusCode);
    }

    [Fact]
    public async Task 无来源与回环来源仍可用()
    {
        // 无 Origin：扩展 Service Worker 之外的脚本、curl、本项目测试脚本
        Assert.Equal(HttpStatusCode.OK, (await _http.SendAsync(Authed(HttpMethod.Get, "/api/tasks"))).StatusCode);

        // 回环页面：本地调试页 / 本地测试站点
        var msg = Authed(HttpMethod.Get, "/api/tasks");
        msg.Headers.Add("Origin", "http://localhost:8080");
        var resp = await _http.SendAsync(msg);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("http://localhost:8080", resp.Headers.GetValues("Access-Control-Allow-Origin").First());
    }

    [Fact]
    public async Task 设置补丁内容未变时不触发变更事件()
    {
        // 逐字段比对后 return changed：内容相同的 PATCH 不应触发 Changed
        // （Changed → ThemeService.Apply 全量主题重算 + settings.json 落盘，实测约 240ms CPU/次）
        var fired = 0;
        void Handler(object? _, EventArgs __) => Interlocked.Increment(ref fired);
        _settings.Changed += Handler;
        try
        {
            var snapshot = new
            {
                defaultDownloadDir = _settings.Current.DefaultDownloadDir,
                maxConcurrentDownloads = _settings.Current.MaxConcurrentDownloads,
                defaultConnections = _settings.Current.DefaultConnections,
                notificationsEnabled = _settings.Current.NotificationsEnabled,
                globalSpeedLimit = _settings.Current.GlobalSpeedLimit
            };
            for (var i = 0; i < 3; i++)
            {
                var r = await _http.SendAsync(Authed(HttpMethod.Post, "/api/settings", snapshot));
                Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            }
            Assert.Equal(0, fired);

            // 真的改了才触发
            var r2 = await _http.SendAsync(Authed(HttpMethod.Post, "/api/settings",
                new { maxConcurrentDownloads = _settings.Current.MaxConcurrentDownloads + 1 }));
            Assert.Equal(HttpStatusCode.OK, r2.StatusCode);
            Assert.Equal(1, fired);
        }
        finally
        {
            _settings.Changed -= Handler;
        }
    }

    [Fact]
    public async Task 下载端点完整流程()
    {
        _fileServer.AddFile("api-dl.bin", new byte[64 * 1024]);
        var resp = await _http.SendAsync(Authed(HttpMethod.Post, "/api/download", new
        {
            url = _fileServer.Url("api-dl.bin"),
            connections = 4
        }));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var gid = json.RootElement.GetProperty("gid").GetString();
        Assert.False(string.IsNullOrEmpty(gid));

        // 等待完成
        var deadline = DateTime.Now.AddSeconds(20);
        while (DateTime.Now < deadline)
        {
            var tasks = await _http.SendAsync(Authed(HttpMethod.Get, "/api/tasks"));
            var list = JsonDocument.Parse(await tasks.Content.ReadAsStringAsync());
            var me = list.RootElement.EnumerateArray().FirstOrDefault(e => e.GetProperty("gid").GetString() == gid);
            if (me.ValueKind != JsonValueKind.Undefined && me.GetProperty("state").GetString() == "completed")
            {
                Assert.Equal(100, me.GetProperty("progress").GetDouble());
                return;
            }
            await Task.Delay(300);
        }
        Assert.Fail("API 下载未完成");
    }

    [Fact]
    public async Task 下载端点参数校验()
    {
        // 无 URL
        var r1 = await _http.SendAsync(Authed(HttpMethod.Post, "/api/download", new { }));
        Assert.Equal(HttpStatusCode.BadRequest, r1.StatusCode);

        // 非法 URL
        var r2 = await _http.SendAsync(Authed(HttpMethod.Post, "/api/download", new { url = "javascript:alert(1)" }));
        Assert.Equal(HttpStatusCode.BadRequest, r2.StatusCode);

        // 非法 JSON
        var msg = Authed(HttpMethod.Post, "/api/download");
        msg.Content = new StringContent("{broken", Encoding.UTF8, "application/json");
        var r3 = await _http.SendAsync(msg);
        Assert.Equal(HttpStatusCode.BadRequest, r3.StatusCode);
    }

    [Fact]
    public async Task 任务操作端点()
    {
        _fileServer.AddFile("api-op.bin", new byte[4 * 1024 * 1024]);
        var resp = await _http.SendAsync(Authed(HttpMethod.Post, "/api/download", new
        {
            url = _fileServer.Url("api-op.bin"),
            connections = 1,
            speedLimit = 100 * 1024 // 限速 100KB/s，保证任务停留在下载中
        }));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var gid = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.GetProperty("gid").GetString()!;
        await Task.Delay(800);

        var pause = await _http.SendAsync(Authed(HttpMethod.Post, $"/api/tasks/{gid}/pause"));
        Assert.Equal(HttpStatusCode.OK, pause.StatusCode);
        await Task.Delay(400);

        var resume = await _http.SendAsync(Authed(HttpMethod.Post, $"/api/tasks/{gid}/resume"));
        Assert.Equal(HttpStatusCode.OK, resume.StatusCode);

        var remove = await _http.SendAsync(Authed(HttpMethod.Post, $"/api/tasks/{gid}/remove"));
        Assert.Equal(HttpStatusCode.OK, remove.StatusCode);
    }

    [Fact]
    public async Task 统计端点()
    {
        var resp = await _http.SendAsync(Authed(HttpMethod.Get, "/api/stats"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.True(json.RootElement.TryGetProperty("downloadSpeed", out _));
        Assert.True(json.RootElement.TryGetProperty("numActive", out _));
    }

    [Fact]
    public async Task 设置补丁端点()
    {
        var resp = await _http.SendAsync(Authed(HttpMethod.Post, "/api/settings", new
        {
            maxConcurrentDownloads = 5,
            theme = "dark",
            notificationsEnabled = false
        }));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(5, _settings.Current.MaxConcurrentDownloads);
        Assert.Equal(ThemeMode.Dark, _settings.Current.Theme);
        Assert.False(_settings.Current.NotificationsEnabled);

        // 越界值被拒绝（保持原值）
        await _http.SendAsync(Authed(HttpMethod.Post, "/api/settings", new { maxConcurrentDownloads = 999 }));
        Assert.Equal(5, _settings.Current.MaxConcurrentDownloads);

        var get = await _http.SendAsync(Authed(HttpMethod.Get, "/api/settings"));
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
    }

    [Fact]
    public async Task 未知路径404()
    {
        var resp = await _http.SendAsync(Authed(HttpMethod.Get, "/api/nope"));
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
