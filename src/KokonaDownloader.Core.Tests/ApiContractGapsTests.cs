using System.Net;
using System.Text;
using System.Text.Json;
using KokonaDownloader.Core.Api;
using KokonaDownloader.Core.Engine;
using KokonaDownloader.Core.Settings;

namespace KokonaDownloader.Core.Tests;

/// <summary>
/// API 契约补充测试：补齐既有测试与文档都没覆盖的分支。
/// 覆盖点（均来自扩展 background.js 真实依赖的响应字段）：
///  - duplicate=true：扩展靠它提示"客户端已有此任务"，且**不得**重复建任务；
///  - confirm=true：单条磁力链接必须走"用户确认"而不是静默建任务；
///  - 非法 JSON / 未知字段 / 空补丁的容错（v1.0.5 报告里"未知字段返回 500"的回归护栏）；
///  - 多 URL 的镜像语义（同一请求里的多个地址会合并成一个任务的多个镜像，而不是多个任务）；
///  - 任务操作端点的 gid 校验（非法格式 404 / 不存在 400）。
/// </summary>
public class ApiContractGapsTests : IAsyncLifetime
{
    private TestEnv.FileServer _server = null!;
    private DownloadEngine _engine = null!;
    private ApiService _api = null!;
    private SettingsStore _settings = null!;
    private string _workDir = null!;
    private string _downloadDir = null!;
    private HttpClient _http = null!;
    private const string Secret = "gaps-secret";

    private readonly List<string> _magnetRequests = new();
    private readonly List<string> _duplicateNotices = new();

    public async Task InitializeAsync()
    {
        _server = new TestEnv.FileServer();
        _workDir = TestEnv.NewWorkDir();
        _downloadDir = Path.Combine(_workDir, "downloads");
        Directory.CreateDirectory(_downloadDir);

        _settings = new SettingsStore(Path.Combine(_workDir, "settings.json"));
        var apiPort = TestEnv.GetFreePort();
        _settings.Update(s =>
        {
            s.ApiSecret = Secret;
            s.ApiPort = apiPort;
            s.DefaultDownloadDir = _downloadDir;
            return true;
        });

        var engineConfig = new EngineConfig
        {
            Aria2Path = TestEnv.Aria2Path,
            WorkDir = Path.Combine(_workDir, "engine"),
            DefaultDownloadDir = _downloadDir,
            RpcPort = TestEnv.GetFreePort(),
            RpcSecret = "engine-secret",
            BtEnabled = false,
            PollIntervalMs = 200
        };
        _engine = new DownloadEngine(engineConfig, new TaskStore(Path.Combine(_workDir, "tasks.json")));
        await _engine.StartAsync();

        _api = new ApiService(_engine, _settings, apiPort);
        _api.MagnetConfirmRequested += m => { lock (_magnetRequests) _magnetRequests.Add(m); };
        _api.DuplicateTaskNoticeRequested += n => { lock (_duplicateNotices) _duplicateNotices.Add(n); };
        _api.Start();

        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{apiPort}") };
    }

    public async Task DisposeAsync()
    {
        // InitializeAsync 中途失败时（如高负载下 aria2 15s 未就绪）后面的字段还是 null：
        // 必须逐项判空清理，否则 NRE 会中断清理 → 引擎轮询循环留在测试进程里、aria2 泄漏。
        try { _http?.Dispose(); } catch { }
        try { _api?.Dispose(); } catch { }
        if (_engine != null) { try { await _engine.DisposeAsync(); } catch { } }
        try { _server?.Dispose(); } catch { }
        try { if (_workDir != null) Directory.Delete(_workDir, true); } catch { }
    }

    private async Task<HttpResponseMessage> PostAsync(string path, string rawBody)
    {
        var msg = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(rawBody, Encoding.UTF8, "application/json")
        };
        msg.Headers.Add("X-Kokona-Secret", Secret);
        return await _http.SendAsync(msg);
    }

    private Task<HttpResponseMessage> PostJsonAsync(string path, object body)
        => PostAsync(path, JsonSerializer.Serialize(body));

    private async Task<JsonDocument> GetTasksAsync()
    {
        var msg = new HttpRequestMessage(HttpMethod.Get, "/api/tasks");
        msg.Headers.Add("X-Kokona-Secret", Secret);
        var resp = await _http.SendAsync(msg);
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task 重复链接返回duplicate并跳过添加()
    {
        // 慢速文件 + 等任务真正进入 active 再发第二次请求，保证"未结束"前提成立。
        // 体积刻意压到 2MB（约 8s 下发完）：够覆盖"发出第二次请求"的时间窗，又不给同进程其它测试添负载。
        var content = new byte[2 * 1024 * 1024];
        _server.AddFile("dup.bin", content, chunkBytes: 64 * 1024, chunkDelayMs: 250);
        var url = _server.Url("dup.bin");

        var first = await PostJsonAsync("/api/download", new { urls = new[] { url }, connections = 1 });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        await WaitForActiveAsync(url);

        var second = await PostJsonAsync("/api/download", new { urls = new[] { url } });
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var json = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        Assert.True(json.RootElement.GetProperty("duplicate").GetBoolean(),
            "同一 URL 且任务未结束时应返回 duplicate=true（扩展据此提示用户）");

        lock (_duplicateNotices)
            Assert.NotEmpty(_duplicateNotices); // UI 提醒事件必须被触发

        // 不得重复建任务：该 URL 只对应一个任务
        using var tasks = await GetTasksAsync();
        var sameUrl = tasks.RootElement.EnumerateArray()
            .Count(t => t.GetProperty("urls").EnumerateArray().Any(u => u.GetString() == url));
        Assert.Equal(1, sameUrl);
    }

    /// <summary>轮询直到该 URL 的任务进入下载中（active）——重复检测只针对未结束任务。</summary>
    private async Task WaitForActiveAsync(string url)
    {
        var deadline = DateTime.Now.AddSeconds(20);
        while (DateTime.Now < deadline)
        {
            using var tasks = await GetTasksAsync();
            foreach (var t in tasks.RootElement.EnumerateArray())
            {
                if (t.GetProperty("state").GetString() != "active") continue;
                if (t.GetProperty("urls").EnumerateArray().Any(u => u.GetString() == url)) return;
            }
            await Task.Delay(100);
        }
        Assert.Fail($"任务未在 20s 内进入 active: {url}");
    }

    [Fact]
    public async Task 单条磁力返回confirm并交由用户确认()
    {
        var magnet = "magnet:?xt=urn:btih:" + new string('a', 40) + "&dn=confirm-test";

        var resp = await PostJsonAsync("/api/download", new { urls = new[] { magnet } });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.True(json.RootElement.GetProperty("confirm").GetBoolean(),
            "单条磁力链接应返回 confirm=true（扩展据此提示'请在确认窗口中确认'）");

        lock (_magnetRequests)
        {
            Assert.Single(_magnetRequests);
            Assert.Equal(magnet, _magnetRequests[0]);
        }

        // 确认前不得静默建任务
        using var tasks = await GetTasksAsync();
        Assert.DoesNotContain(tasks.RootElement.EnumerateArray(),
            t => t.GetProperty("urls").EnumerateArray().Any(u => u.GetString() == magnet));
    }

    [Fact]
    public async Task 请求体非法JSON返回400()
    {
        var resp = await PostAsync("/api/download", "{not json");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task 未知设置字段被忽略而不是500()
    {
        // v1.0.5 报告记录的缺陷：未知字段曾导致 API 500。此处作为回归护栏。
        var resp = await PostJsonAsync("/api/settings", new { unknownField = 123, maxConcurrentDownloads = 4 });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(4, _settings.Current.MaxConcurrentDownloads);
    }

    [Fact]
    public async Task 空设置补丁不改变任何设置()
    {
        var before = JsonSerializer.Serialize(_settings.Current);
        var resp = await PostJsonAsync("/api/settings", new { });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(before, JsonSerializer.Serialize(_settings.Current));
    }

    [Fact]
    public async Task 多URL请求按镜像语义合并为单任务()
    {
        // 现状记录（此前只在评估报告里提过，没有测试锁定）：/api/download 一次请求里的多个地址
        // 会作为同一任务的多个镜像 URI 提交给 aria2，而不是拆成多个任务。
        // 浏览器扩展每次只发一个 URL，因此用户可见路径不受影响；外部脚本按"批量"使用时需注意。
        _server.AddFile("mirror-a.bin", new byte[64 * 1024]);
        _server.AddFile("mirror-b.bin", new byte[64 * 1024]);
        var a = _server.Url("mirror-a.bin");
        var b = _server.Url("mirror-b.bin");

        var resp = await PostJsonAsync("/api/download", new { urls = new[] { a, b } });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var gid = json.RootElement.GetProperty("gid").GetString();
        Assert.False(string.IsNullOrEmpty(gid));

        await Task.Delay(1500);
        using var tasks = await GetTasksAsync();
        var task = tasks.RootElement.EnumerateArray().Single(t => t.GetProperty("gid").GetString() == gid);
        var urls = task.GetProperty("urls").EnumerateArray().Select(u => u.GetString()).ToList();
        Assert.Equal(2, urls.Count);
        Assert.Contains(a, urls);
        Assert.Contains(b, urls);
    }

    [Fact]
    public async Task 任务操作gid非法格式返回404()
    {
        var resp = await PostAsync("/api/tasks/not-a-gid/pause", "{}");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task 任务操作不存在的gid返回400()
    {
        var resp = await PostAsync("/api/tasks/deadbeefdeadbeef/resume", "{}");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task 下载端点错误方法返回404()
    {
        var msg = new HttpRequestMessage(HttpMethod.Get, "/api/download");
        msg.Headers.Add("X-Kokona-Secret", Secret);
        var resp = await _http.SendAsync(msg);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
