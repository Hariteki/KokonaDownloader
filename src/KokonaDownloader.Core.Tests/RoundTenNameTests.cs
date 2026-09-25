using System.Diagnostics;
using System.Text;
using KokonaDownloader.Core.Engine;

namespace KokonaDownloader.Core.Tests;

/// <summary>
/// 第十轮（测试报告 §16）：临时链接解析不到真实文件名。
/// 用 23 种响应头形态实测后确认：aria2 1.37.0 单独就能解析 <c>filename*=UTF-8''…</c>（含 CJK/空格/语言标记/302），
/// 真正失败的是三类畸形头——非法 ext-value（没编码的圆括号，且会让整条 Content-Disposition 作废）、
/// 缺 disposition 类型、以及 <c>filename=</c> 里裸写 UTF-8 字节。
/// 本文件锁住"我们比 aria2 宽容"的解析器、预解析的触发条件、以及 aria2 启动参数里的 UTF-8 开关。
/// </summary>
public class RoundTenNameTests
{
    // ---- 1. 宽容版 Content-Disposition 解析 ----

    [Theory]
    // 合法 ext-value：括号按 RFC 6266 百分号编码。aria2 自己也能解，这里是基线
    [InlineData("attachment; filename*=UTF-8''%E6%8A%A5%E5%91%8A%20%281%29.pdf", "报告 (1).pdf")]
    // 非法 ext-value（圆括号裸写）：浏览器宽容、aria2 直接放弃整条头 —— 我们必须解出来
    [InlineData("attachment; filename*=UTF-8''%E6%8A%A5%E5%91%8A%20(1).pdf", "报告 (1).pdf")]
    // 非法 ext-value 旁边还有个合法的 filename=：aria2 连它也不认（实测落回 URL 末段），我们要用更优的那个
    [InlineData("attachment; filename=\"fallback.bin\"; filename*=UTF-8''%E6%8A%A5%E5%91%8A%20(1).pdf", "报告 (1).pdf")]
    // 缺 disposition 类型（只有参数）：aria2 实测不认
    [InlineData("filename*=UTF-8''%E6%97%A0%E7%B1%BB%E5%9E%8B.pdf", "无类型.pdf")]
    // inline + 小写 charset
    [InlineData("inline; filename*=utf-8''report.pdf", "report.pdf")]
    // 带语言标记
    [InlineData("attachment; filename*=UTF-8'zh-cn'%E4%B8%AD%E6%96%87.pdf", "中文.pdf")]
    // 空 charset
    [InlineData("attachment; filename*=''a.pdf", "a.pdf")]
    // 字面量 %25 还原成 %
    [InlineData("attachment; filename*=UTF-8''100%25_done.pdf", "100%_done.pdf")]
    // 零宽字符开头（部分 CDN 会带）：首尾零宽/BOM 一律剥掉，否则"看起来同名"却判不出重复
    [InlineData("attachment; filename*=UTF-8''%E2%80%8B%E6%8A%A5%E5%91%8A.pdf", "报告.pdf")]
    // 纯 ASCII
    [InlineData("attachment; filename*=UTF-8''report%20%281%29.pdf", "report (1).pdf")]
    public void 宽容解析_filename星号(string header, string expected)
    {
        Assert.Equal(expected, ContentDispositionParser.ParseFileName(header));
    }

    [Theory]
    // 普通记法按 RFC 6266 是原样字节串：不做百分号解码（与浏览器、aria2 一致）
    [InlineData("attachment; filename=\"%E6%8A%A5%E5%91%8A.pdf\"", "%E6%8A%A5%E5%91%8A.pdf")]
    [InlineData("attachment; filename=\"a;b.pdf\"", "a;b.pdf")]
    [InlineData("attachment; filename=\"plain name.zip\"", "plain name.zip")]
    public void 宽容解析_普通filename(string header, string expected)
    {
        Assert.Equal(expected, ContentDispositionParser.ParseFileName(header));
    }

    [Fact]
    public void 服务器裸写UTF8字节的filename不再乱码()
    {
        // 服务器把中文按 UTF-8 直接写进头里（没按 RFC 编码）。.NET 会把这段字节按 Latin-1 读成乱码串，
        // 我们必须还原（对应 aria2 的 --content-disposition-default-utf8=true）
        var rawUtf8AsLatin1 = Encoding.Latin1.GetString(Encoding.UTF8.GetBytes("中文 报告.pdf"));
        Assert.False(rawUtf8AsLatin1.Contains("中"), "前置条件：裸字节读进来应是乱码");
        Assert.Equal("中文 报告.pdf",
            ContentDispositionParser.ParseFileName($"attachment; filename=\"{rawUtf8AsLatin1}\""));
    }

    [Theory]
    [InlineData("attachment", null)]                                  // 没给名字
    [InlineData("attachment; filename=\"\"", null)]                    // 空名
    [InlineData("attachment; filename=\"   \"", null)]                  // 全空白
    [InlineData("attachment; filename=\"a<b.pdf\"", null)]              // Windows 非法字符
    [InlineData("attachment; filename=\"CON.txt\"", null)]              // Windows 保留名
    [InlineData("attachment; filename=\"COM2.log\"", null)]
    [InlineData("attachment; filename*=UTF-8''%00bad.pdf", null)]       // 控制字符
    [InlineData(null, null)]
    public void 不可用的名字一律返回null交回aria2(string? header, string? expected)
    {
        Assert.Equal(expected, ContentDispositionParser.ParseFileName(header));
    }

    [Fact]
    public void 百分号编码的路径穿越只取basename()
    {
        Assert.Equal("evil.txt", ContentDispositionParser.ParseFileName("attachment; filename*=UTF-8''..%2F..%2Fevil.txt"));
        Assert.Equal("evil.txt", ContentDispositionParser.ParseFileName("attachment; filename=\"..\\..\\evil.txt\""));
    }

    [Fact]
    public void 超长名字截断但保留扩展名()
    {
        var header = "attachment; filename=\"" + new string('a', 300) + ".zip\"";
        var name = ContentDispositionParser.ParseFileName(header);
        Assert.NotNull(name);
        Assert.True(name!.Length <= 200);
        Assert.EndsWith(".zip", name);
    }

    [Fact]
    public async Task 错误响应里的ContentDisposition不得当作落盘名()
    {
        // 下载站的 404/403 页面也经常挂着 Content-Disposition（"文件不存在.html"）。
        // 预解析只认 200/206，否则会把一个错误响应的名字钉到任务上。
        var port = TestEnv.GetFreePort();
        using var listener = new System.Net.HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var serve = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                System.Net.HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); }
                catch { break; }
                var notFound = ctx.Request.Url!.AbsolutePath.Contains("missing");
                ctx.Response.StatusCode = notFound ? 404 : 200;
                ctx.Response.Headers["Content-Disposition"] =
                    notFound ? "attachment; filename=\"does-not-exist.html\"" : "attachment; filename=\"real.bin\"";
                var body = System.Text.Encoding.UTF8.GetBytes("x");
                ctx.Response.ContentLength64 = body.Length;
                await ctx.Response.OutputStream.WriteAsync(body);
                ctx.Response.Close();
            }
        });
        var resolver = new RemoteNameResolver();
        try
        {
            Assert.Equal("real.bin", await resolver.ResolveAsync($"http://127.0.0.1:{port}/ok", null, null));
            Assert.Null(await resolver.ResolveAsync($"http://127.0.0.1:{port}/missing", null, null));
        }
        finally
        {
            listener.Stop();
            try { await serve; } catch { }
        }
    }

    // ---- 2. 预解析触发条件：宁可少发请求，也不给签名链接/显式命名添麻烦 ----

    [Theory]
    // 已经有名字（用户手填 / 浏览器已解析）：绝不猜
    [InlineData("http://x/a.zip", "chosen.zip", false)]
    [InlineData("http://x/tok_123", "chosen.bin", false)]
    // 无扩展名（临时链接/token 最常见形态）：值得探测
    [InlineData("http://x/tok_123", null, true)]
    [InlineData("http://x/dl/", null, true)]
    // 有扩展名且无签名参数：值得探测（响应头可能给出更好的名字）
    [InlineData("http://x/a.zip", null, true)]
    // 查询串里点名了文件名/响应头：值得探测（S3 的 response-content-disposition 形态）
    [InlineData("http://x/a.bin?response-content-disposition=attachment", null, true)]
    // 签名链接 + 有扩展名：跳过，避免多碰一次性签名 URL
    [InlineData("http://x/a.zip?sign=abc", null, false)]
    [InlineData("http://x/a.zip?X-Amz-Signature=zzz", null, false)]
    // 签名链接 + 无扩展名：没别的可兜底，仍然探测
    [InlineData("http://x/tok_123?X-Amz-Signature=zzz", null, true)]
    // 非 HTTP：一律不探测
    [InlineData("magnet:?xt=urn:btih:abc", null, false)]
    [InlineData("ftp://x/a", null, false)]
    [InlineData("", null, false)]
    public void 预解析触发条件(string url, string? explicitName, bool expected)
    {
        Assert.Equal(expected, RemoteNameResolver.ShouldPreresolve(url, explicitName));
    }

    // ---- 3. aria2 启动参数：裸 UTF-8 的 Content-Disposition 必须由 aria2 自己认下 ----

    [Fact]
    public void aria2启动参数包含ContentDisposition的UTF8开关()
    {
        var config = new EngineConfig
        {
            Aria2Path = "aria2c.exe",
            WorkDir = "work",
            DefaultDownloadDir = "dl",
            RpcPort = 16801,
            RpcSecret = "s",
            BtEnabled = false
        };
        var args = Aria2Process.BuildArgs(config, "work/aria2.session", "work/aria2.log");
        // 实测：不加这个开关时 filename="中文.pdf"（裸 UTF-8 字节）会落成乱码 ä¸­æ.pdf（见测试报告 §16）
        Assert.Contains("--content-disposition-default-utf8=true", args);
        Assert.DoesNotContain("--enable-dht=true", args);
    }

    [Fact]
    public void aria2启动参数保留既有行为不回归()
    {
        var config = new EngineConfig
        {
            Aria2Path = "aria2c.exe",
            WorkDir = "work",
            DefaultDownloadDir = "dl",
            RpcPort = 16801,
            RpcSecret = "s",
            GlobalSpeedLimit = 1048576,
            BtEnabled = true,
            BtSeedEnabled = false
        };
        var args = Aria2Process.BuildArgs(config, "s", "l");
        Assert.Contains("--retry-wait=2", args);          // 第八轮 P3-6
        Assert.Contains("--max-connection-per-server=16", args);
        Assert.Contains("--show-console-readout=false", args);
        Assert.Contains("--max-overall-download-limit=1048576", args);
        Assert.Contains("--seed-time=0", args);
    }
}

/// <summary>
/// 第十轮集成测试：真实 aria2 + 本地文件服务器，端到端验证"临时链接 → 真实文件名"，
/// 以及引擎子进程被杀后能自愈（此前的实测缺陷：一旦掉线，之后每次添加都返回 HTTP 500 且永不恢复）。
/// </summary>
public class RoundTenNameIntegrationTests : IAsyncLifetime
{
    private TestEnv.FileServer _server = null!;
    private DownloadEngine _engine = null!;
    private string _workDir = null!;
    private string _dlDir = null!;
    private string _engineDir = null!;

    public async Task InitializeAsync()
    {
        _server = new TestEnv.FileServer();
        _workDir = TestEnv.NewWorkDir();
        _dlDir = Path.Combine(_workDir, "downloads");
        _engineDir = Path.Combine(_workDir, "engine");
        var engineConfig = new EngineConfig
        {
            Aria2Path = TestEnv.Aria2Path,
            WorkDir = _engineDir,
            DefaultDownloadDir = _dlDir,
            RpcPort = TestEnv.GetFreePort(),
            RpcSecret = "round10-secret",
            PollIntervalMs = 300
        };
        _engine = new DownloadEngine(engineConfig, new TaskStore(Path.Combine(_workDir, "tasks.json")));
        await _engine.StartAsync();
    }

    public async Task DisposeAsync()
    {
        try { if (_engine != null) await _engine.DisposeAsync(); } catch { }
        try { _server?.Dispose(); } catch { }
        try { if (_workDir != null) Directory.Delete(_workDir, true); } catch { }
    }

    private async Task<DownloadTaskInfo> WaitDoneAsync(string gid)
    {
        var deadline = DateTime.Now.AddSeconds(40);
        while (DateTime.Now < deadline)
        {
            var t = await _engine.GetTaskAsync(gid);
            if (t is { State: TaskState.Completed or TaskState.Failed }) return t;
            await Task.Delay(200);
        }
        Assert.Fail($"任务 {gid} 未在超时前结束");
        throw new InvalidOperationException();
    }

    private static byte[] Payload(int size = 96 * 1024)
    {
        var b = new byte[size];
        new Random(10).NextBytes(b);
        return b;
    }

    [Fact]
    public async Task 非法extvalue的临时链接_预解析后按真实名落盘()
    {
        // 用户报告的形态：链接末段是 token（无扩展名），真实名只在 Content-Disposition 里，
        // 而且那个 ext-value 是非法的（圆括号没编码）——正是 aria2 自己会放弃整条头的情况
        const string cd = "attachment; filename*=UTF-8''%E6%8A%A5%E5%91%8A%20(1).pdf";
        _server.AddFile("tmp/tok_9f3a1c", Payload(), new Dictionary<string, string> { ["Content-Disposition"] = cd });
        var url = _server.Url("tmp/tok_9f3a1c");

        var added = await _engine.AddTaskAsync(new NewTaskRequest { Urls = new List<string> { url } });
        var done = await WaitDoneAsync(added.Gid);
        Assert.Equal(TaskState.Completed, done.State);

        Assert.Equal("报告 (1).pdf", done.Name);
        Assert.True(File.Exists(Path.Combine(_dlDir, "报告 (1).pdf")),
            $"应落盘真实名，实际目录内容: {string.Join(", ", Directory.GetFiles(_dlDir))}");
        Assert.False(File.Exists(Path.Combine(_dlDir, "tok_9f3a1c")), "不应落盘为 URL 里的 token");
    }

    [Fact]
    public async Task 缺disposition类型的响应头也能拿到真实名()
    {
        _server.AddFile("tmp/tok_nodepth", Payload(), new Dictionary<string, string> { ["Content-Disposition"] = "filename*=UTF-8''%E6%97%A0%E7%B1%BB%E5%9E%8B.pdf" });
        var url = _server.Url("tmp/tok_nodepth");

        var added = await _engine.AddTaskAsync(new NewTaskRequest { Urls = new List<string> { url } });
        var done = await WaitDoneAsync(added.Gid);

        Assert.Equal(TaskState.Completed, done.State);
        Assert.Equal("无类型.pdf", done.Name);
        Assert.True(File.Exists(Path.Combine(_dlDir, "无类型.pdf")),
            $"应落盘真实名，实际目录内容: {string.Join(", ", Directory.GetFiles(_dlDir))}");
    }

    [Fact]
    public async Task 无扩展名链接才会多发一次预解析请求()
    {
        _server.AddFile("tmp/tok_count_a", Payload(), new Dictionary<string, string> { ["Content-Disposition"] = "attachment; filename=\"preresolved-a.bin\"" });
        var url = _server.Url("tmp/tok_count_a");
        var added = await _engine.AddTaskAsync(new NewTaskRequest { Urls = new List<string> { url } });
        await WaitDoneAsync(added.Gid);
        // 预解析 1 次 + aria2 自己的下载请求 ≥1 次
        Assert.True(_server.RequestCountFor("tmp/tok_count_a") >= 2,
            $"无扩展名链接应先多发一次预解析请求，实际 {_server.RequestCountFor("tmp/tok_count_a")} 次");
        Assert.True(File.Exists(Path.Combine(_dlDir, "preresolved-a.bin")),
            $"预解析出的名字应作为 out 落盘，实际目录内容: {string.Join(", ", Directory.GetFiles(_dlDir))}");

        // 显式给了名字：不许再探测
        _server.AddFile("tmp/tok_count_b", Payload(), new Dictionary<string, string> { ["Content-Disposition"] = "attachment; filename=\"preresolved-b.bin\"" });
        var explicitAdd = await _engine.AddTaskAsync(new NewTaskRequest
        {
            Urls = new List<string> { _server.Url("tmp/tok_count_b") },
            FileName = "user-picked.bin"
        });
        await WaitDoneAsync(explicitAdd.Gid);
        Assert.Equal(1, _server.RequestCountFor("tmp/tok_count_b"));
        Assert.True(File.Exists(Path.Combine(_dlDir, "user-picked.bin")));
    }

    [Fact]
    public async Task 签名链接不预解析_但合法响应头仍由aria2正确解析()
    {
        // 带签名参数 + 有扩展名 → 我们不去碰它；此时必须仍靠 aria2 拿到真名（确认没有回归）
        _server.AddFile("soft/signed.zip", Payload(), new Dictionary<string, string> { ["Content-Disposition"] = "attachment; filename=\"from-header.zip\"" });
        var url = _server.Url("soft/signed.zip") + "?sign=abc123&expires=9999999999";

        var added = await _engine.AddTaskAsync(new NewTaskRequest { Urls = new List<string> { url } });
        var done = await WaitDoneAsync(added.Gid);

        Assert.Equal(TaskState.Completed, done.State);
        Assert.Equal("from-header.zip", done.Name);
        Assert.Equal(1, _server.RequestCountFor("soft/signed.zip")); // 只有 aria2 自己那一次
    }

    [Fact]
    public async Task 引擎子进程被杀后添加任务会自动重启引擎()
    {
        // 此前的实测缺陷：aria2 掉线后所有添加都变成 HTTP 500（SocketException 原样抛出）且永不恢复
        var pidFile = Path.Combine(_engineDir, "aria2.pid");
        Assert.True(File.Exists(pidFile), "前置条件：aria2.pid 应存在");
        var pid = int.Parse(File.ReadAllText(pidFile).Trim());
        Process.GetProcessById(pid).Kill(entireProcessTree: true);

        var deadline = DateTime.Now.AddSeconds(20);
        while (_engine.IsRunning && DateTime.Now < deadline) await Task.Delay(200);
        Assert.False(_engine.IsRunning, "前置条件：aria2 进程应已被杀掉");

        _server.AddFile("tmp/tok_selfheal", Payload(), new Dictionary<string, string> { ["Content-Disposition"] = "attachment; filename=\"self-heal.bin\"" });
        var added = await _engine.AddTaskAsync(new NewTaskRequest { Urls = new List<string> { _server.Url("tmp/tok_selfheal") } });
        var done = await WaitDoneAsync(added.Gid);

        Assert.Equal(TaskState.Completed, done.State);
        Assert.True(_engine.IsRunning, "引擎应已自动重启");
        Assert.True(File.Exists(Path.Combine(_dlDir, "self-heal.bin")),
            $"自愈后应能正常下载，实际目录内容: {string.Join(", ", Directory.GetFiles(_dlDir))}");
    }
}
