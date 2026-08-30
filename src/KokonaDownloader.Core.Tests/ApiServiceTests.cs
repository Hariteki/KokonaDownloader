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
        _http.Dispose();
        _api.Dispose();
        await _engine.DisposeAsync();
        _fileServer.Dispose();
        try { Directory.Delete(_workDir, true); } catch { }
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
    public async Task OPTIONS预检返回204带CORS头()
    {
        var msg = new HttpRequestMessage(HttpMethod.Options, "/api/download");
        msg.Headers.Add("Origin", "chrome-extension://abcdef");
        var resp = await _http.SendAsync(msg);
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        Assert.Equal("*", resp.Headers.GetValues("Access-Control-Allow-Origin").First());
        Assert.Contains("X-Kokona-Secret", string.Join(",", resp.Headers.GetValues("Access-Control-Allow-Headers")));
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
