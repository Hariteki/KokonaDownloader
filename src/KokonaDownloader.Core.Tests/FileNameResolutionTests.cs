using System.Net;
using System.Text;
using System.Text.Json;
using KokonaDownloader.Core.Api;
using KokonaDownloader.Core.Engine;
using KokonaDownloader.Core.Settings;

namespace KokonaDownloader.Core.Tests;

/// <summary>
/// 文件名解析集成测试（真实 aria2 + 本地文件服务器）：
/// 覆盖「URL 是临时名、真实文件名只在响应 Content-Disposition 里」的场景——
/// 修复前客户端会把 URL 末段猜测名固定为 aria2 的 out，导致落盘名永远是临时名；
/// 修复后不携带 filename 时由 aria2 按响应头解析真实文件名。
/// </summary>
public class FileNameResolutionTests : IAsyncLifetime
{
    private TestEnv.FileServer _fileServer = null!;
    private DownloadEngine _engine = null!;
    private ApiService _api = null!;
    private SettingsStore _settings = null!;
    private string _workDir = null!;
    private int _apiPort;
    private HttpClient _http = null!;
    private const string Secret = "fname-test-secret";

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

    /// <summary>模拟扩展自动捕获：POST /api/download，不携带 filename（浏览器尚未解析出文件名）。</summary>
    private async Task<JsonElement> SendDownloadWithoutFileName(string url)
    {
        var msg = new HttpRequestMessage(HttpMethod.Post, "/api/download");
        msg.Headers.Add("X-Kokona-Secret", Secret);
        msg.Content = new StringContent(JsonSerializer.Serialize(new { urls = new[] { url } }), Encoding.UTF8, "application/json");
        var resp = await _http.SendAsync(msg);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
    }

    /// <summary>轮询 /api/tasks 直到指定 gid 的任务到达终态，返回该任务的 JSON。</summary>
    private async Task<JsonElement> WaitTaskAsync(string gid)
    {
        var deadline = DateTime.Now.AddSeconds(30);
        while (DateTime.Now < deadline)
        {
            var msg = new HttpRequestMessage(HttpMethod.Get, "/api/tasks");
            msg.Headers.Add("X-Kokona-Secret", Secret);
            var tasks = JsonDocument.Parse(await (await _http.SendAsync(msg)).Content.ReadAsStringAsync());
            foreach (var t in tasks.RootElement.EnumerateArray())
            {
                if (t.GetProperty("gid").GetString() == gid)
                {
                    var state = t.GetProperty("state").GetString();
                    if (state is "completed" or "failed") return t.Clone();
                }
            }
            await Task.Delay(300);
        }
        Assert.Fail($"任务 {gid} 未在超时前完成");
        throw new InvalidOperationException();
    }

    [Fact]
    public async Task 临时名URL按ContentDisposition落盘真实文件名()
    {
        // URL 末段是临时名 abc123.tmp，服务器通过 Content-Disposition 给出真实文件名
        _fileServer.AddFile("tmp/abc123.tmp", new byte[64 * 1024],
            new Dictionary<string, string> { ["Content-Disposition"] = "attachment; filename=\"real-file.exe\"" });
        var url = _fileServer.Url("tmp/abc123.tmp");

        var resp = await SendDownloadWithoutFileName(url);
        Assert.True(resp.GetProperty("ok").GetBoolean());
        var gid = resp.GetProperty("gid").GetString()!;

        var task = await WaitTaskAsync(gid);
        Assert.Equal("completed", task.GetProperty("state").GetString());

        // 落盘名必须是 Content-Disposition 的真实文件名，而不是 URL 里的临时名
        var dir = _settings.Current.DefaultDownloadDir;
        Assert.True(File.Exists(Path.Combine(dir, "real-file.exe")),
            $"应存在 real-file.exe，实际目录内容: {string.Join(", ", Directory.GetFiles(dir))}");
        Assert.False(File.Exists(Path.Combine(dir, "abc123.tmp")), "不应落盘为 URL 临时名 abc123.tmp");

        // 任务列表上报的名字也应是真实文件名（aria2 轮询后更新）
        Assert.Equal("real-file.exe", task.GetProperty("name").GetString());
    }

    [Fact]
    public async Task 无ContentDisposition时按URL末段命名()
    {
        // 服务器不提供 Content-Disposition：aria2 回退到 URL 末段（与浏览器行为一致）
        _fileServer.AddFile("files/setup.exe", new byte[32 * 1024]);
        var url = _fileServer.Url("files/setup.exe");

        var resp = await SendDownloadWithoutFileName(url);
        var gid = resp.GetProperty("gid").GetString()!;

        var task = await WaitTaskAsync(gid);
        Assert.Equal("completed", task.GetProperty("state").GetString());

        var dir = _settings.Current.DefaultDownloadDir;
        Assert.True(File.Exists(Path.Combine(dir, "setup.exe")),
            $"应存在 setup.exe，实际目录内容: {string.Join(", ", Directory.GetFiles(dir))}");
    }

    [Fact]
    public async Task 显式文件名仍然固定out并优先于ContentDisposition()
    {
        // 用户/浏览器显式给出文件名时保持旧行为：out 固定为该名（覆盖 Content-Disposition）
        _fileServer.AddFile("tmp/xyz.tmp", new byte[32 * 1024],
            new Dictionary<string, string> { ["Content-Disposition"] = "attachment; filename=\"server-name.zip\"" });
        var url = _fileServer.Url("tmp/xyz.tmp");

        var msg = new HttpRequestMessage(HttpMethod.Post, "/api/download");
        msg.Headers.Add("X-Kokona-Secret", Secret);
        msg.Content = new StringContent(JsonSerializer.Serialize(new
        {
            urls = new[] { url },
            filename = "user-chosen.exe"
        }), Encoding.UTF8, "application/json");
        var resp = await _http.SendAsync(msg);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var gid = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.GetProperty("gid").GetString()!;

        var task = await WaitTaskAsync(gid);
        Assert.Equal("completed", task.GetProperty("state").GetString());

        var dir = _settings.Current.DefaultDownloadDir;
        Assert.True(File.Exists(Path.Combine(dir, "user-chosen.exe")),
            $"应存在 user-chosen.exe，实际目录内容: {string.Join(", ", Directory.GetFiles(dir))}");
    }

    [Fact]
    public async Task 显式文件名冲突时仍自动重命名()
    {
        // 可靠文件名 + 目标已存在 → 追加编号（原有防覆盖行为保留）
        var dir = _settings.Current.DefaultDownloadDir;
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "dup.exe"), "old");

        _fileServer.AddFile("tmp/dup.tmp", new byte[16 * 1024],
            new Dictionary<string, string> { ["Content-Disposition"] = "attachment; filename=\"dup.exe\"" });
        var url = _fileServer.Url("tmp/dup.tmp");

        var msg = new HttpRequestMessage(HttpMethod.Post, "/api/download");
        msg.Headers.Add("X-Kokona-Secret", Secret);
        msg.Content = new StringContent(JsonSerializer.Serialize(new
        {
            urls = new[] { url },
            filename = "dup.exe"
        }), Encoding.UTF8, "application/json");
        var resp = await _http.SendAsync(msg);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var gid = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.GetProperty("gid").GetString()!;

        var task = await WaitTaskAsync(gid);
        Assert.Equal("completed", task.GetProperty("state").GetString());

        Assert.True(File.Exists(Path.Combine(dir, "dup (1).exe")),
            $"应存在 dup (1).exe，实际目录内容: {string.Join(", ", Directory.GetFiles(dir))}");
        Assert.Equal("old", File.ReadAllText(Path.Combine(dir, "dup.exe"))); // 旧文件未被覆盖
    }
}
