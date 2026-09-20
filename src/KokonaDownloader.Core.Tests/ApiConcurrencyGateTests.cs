using System.Net;
using System.Net.Sockets;
using System.Text;
using KokonaDownloader.Core.Api;
using KokonaDownloader.Core.Engine;
using KokonaDownloader.Core.Settings;

namespace KokonaDownloader.Core.Tests;

/// <summary>
/// 并发请求门（L-9）的回归测试（第四轮审计 N-2 补护栏）。
/// 契约：闸门满员时**立即 503**（不是排队挂住——排队的请求会一直占着 socket），
/// 且处理器结束后槽位必须归还（否则槽位被逐个吃光，表现为"端口在、进程在、但永不响应"）。
///
/// 用注入的 2 槽上限把时序做实：两个"请求头已收完、请求体永远读不完"的 TCP 连接
/// 各占一个槽位（处理器正阻塞在 ReadBodyAsync 上），此时第三个请求必须被拒；
/// 断开这两个连接后服务应自动恢复。生产环境仍是 64 槽（部署后用并发压测复核）。
/// </summary>
public class ApiConcurrencyGateTests : IAsyncLifetime
{
    private const string Secret = "gate-test-secret";
    private string _workDir = null!;
    private SettingsStore _settings = null!;
    private DownloadEngine _engine = null!;
    private ApiService _api = null!;
    private int _port;

    public Task InitializeAsync()
    {
        _workDir = TestEnv.NewWorkDir();
        _port = TestEnv.GetFreePort();

        _settings = new SettingsStore(Path.Combine(_workDir, "settings.json"));
        _settings.Update(s =>
        {
            s.ApiSecret = Secret;
            s.ApiPort = _port;
            s.DefaultDownloadDir = Path.Combine(_workDir, "downloads");
            return true;
        });

        // 只构造不启动：本测试考察的是接受循环与闸门，不应拉起 aria2
        _engine = new DownloadEngine(
            new EngineConfig
            {
                Aria2Path = TestEnv.Aria2Path,
                WorkDir = Path.Combine(_workDir, "engine"),
                DefaultDownloadDir = Path.Combine(_workDir, "downloads"),
                RpcPort = TestEnv.GetFreePort(),
                RpcSecret = "gate-engine-secret"
            },
            new TaskStore(Path.Combine(_workDir, "tasks.json")));

        _api = new ApiService(_engine, _settings, _port, maxConcurrentRequests: 2);
        _api.Start();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        // InitializeAsync 中途失败时后面的字段可能仍是 null：逐项判空清理，避免泄漏 aria2/监听器
        try { _api?.Dispose(); } catch { }
        if (_engine != null) { try { _engine.DisposeAsync().AsTask().Wait(2000); } catch { } }
        try { if (_workDir != null) Directory.Delete(_workDir, true); } catch { }
        return Task.CompletedTask;
    }

    /// <summary>建立一个"请求体永远发不完"的连接：服务端已收到完整请求头并进入读请求体，
    /// 因此该请求会一直占着一个闸门槽位，直到连接被关闭。</summary>
    private async Task<TcpClient> OpenHalfSentRequestAsync()
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _port);
        var req = "{\"urls\":[\"http://127.0.0.1:1/x\"]}";
        var head =
            $"POST /api/download HTTP/1.1\r\nHost: 127.0.0.1:{_port}\r\n" +
            $"X-Kokona-Secret: {Secret}\r\nContent-Type: application/json\r\n" +
            // Content-Length 远大于真正发出的字节数：服务端 ReadToEndAsync 会挂在这个连接上
            "Content-Length: 100000\r\n\r\n" + req;
        await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes(head));
        return client;
    }

    [Fact]
    public async Task 闸门满员时立即503并在请求结束后恢复()
    {
        var hangers = new List<TcpClient>();
        using var probe = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_port}") };
        try
        {
            for (var i = 0; i < 2; i++) hangers.Add(await OpenHalfSentRequestAsync());

            // 处理器进入 ReadBodyAsync 需要一点时间：轮询到闸门真的被占满
            var status = HttpStatusCode.OK;
            var deadline = DateTime.Now.AddSeconds(5);
            while (DateTime.Now < deadline)
            {
                status = (await probe.GetAsync("/api/ping")).StatusCode;
                if (status == HttpStatusCode.ServiceUnavailable) break;
                await Task.Delay(100);
            }
            Assert.Equal(HttpStatusCode.ServiceUnavailable, status);

            foreach (var c in hangers) { try { c.Close(); } catch { } }
            hangers.Clear();

            // 关键断言：槽位必须归还。缺了 finally 里的 Release（或 Task.Run 未启动却不归还），
            // 这一步会一直 503 下去
            var recovered = HttpStatusCode.ServiceUnavailable;
            deadline = DateTime.Now.AddSeconds(10);
            while (DateTime.Now < deadline)
            {
                try
                {
                    recovered = (await probe.GetAsync("/api/ping")).StatusCode;
                    if (recovered == HttpStatusCode.OK) break;
                }
                catch (HttpRequestException) { }
                await Task.Delay(100);
            }
            Assert.Equal(HttpStatusCode.OK, recovered);
        }
        finally
        {
            foreach (var c in hangers) { try { c.Close(); } catch { } }
        }
    }

    [Fact]
    public async Task 未饱和时正常请求不受闸门影响()
    {
        using var probe = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_port}") };
        for (var i = 0; i < 20; i++)
        {
            var resp = await probe.GetAsync("/api/ping");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }
    }
}
