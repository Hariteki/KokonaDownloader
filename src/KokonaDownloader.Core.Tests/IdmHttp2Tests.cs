using System.Collections.Concurrent;
using System.Net;
using KokonaDownloader.Core.Engine.Idm;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KokonaDownloader.Core.Tests;

/// <summary>
/// HTTP/2 验收。用真实的 Kestrel（h2c，明文 prior-knowledge）跑，不用假的协议层。
/// 要证明两件事：
///   1. 服务端支持 h2 时确实协商到了 2.0，且下载结果逐字节正确；
///   2. 多分段在 h2 下**共用一个 TCP 连接**（多路复用），而不是一条分段一个连接。
/// </summary>
public sealed class IdmHttp2Tests
{
    /// <summary>最小 h2c 服务器：支持 Range、限速，并记录连接 id / 并发峰值。</summary>
    private sealed class H2Server : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly byte[] _payload;

        public readonly ConcurrentDictionary<string, byte> ConnectionIds = new();
        public int MaxConcurrentRequests;
        private int _active;

        public H2Server(byte[] payload)
        {
            _payload = payload;
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(o =>
                o.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http2));
            _app = builder.Build();
            _app.MapGet("/f.bin", HandleAsync);
        }

        public async Task StartAsync()
        {
            await _app.StartAsync();
            var addr = _app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.First();
            Port = new Uri(addr).Port;
        }

        public int Port { get; private set; }
        public string Url() => $"http://127.0.0.1:{Port}/f.bin";

        private async Task HandleAsync(HttpContext ctx)
        {
            ConnectionIds.TryAdd(ctx.Connection.Id, 0);
            int now = Interlocked.Increment(ref _active);
            if (now > MaxConcurrentRequests) MaxConcurrentRequests = now;
            try
            {
                long start = 0, end = _payload.Length - 1;
                bool partial = false;
                var range = ctx.Request.Headers.Range.ToString();
                if (!string.IsNullOrEmpty(range) && range.StartsWith("bytes=", StringComparison.Ordinal))
                {
                    var parts = range[6..].Split(',')[0].Split('-');
                    start = parts[0].Length > 0 ? long.Parse(parts[0]) : 0;
                    end = parts.Length > 1 && parts[1].Length > 0 ? long.Parse(parts[1]) : _payload.Length - 1;
                    end = Math.Min(end, _payload.Length - 1);
                    partial = true;
                }

                long len = Math.Max(0, end - start + 1);
                ctx.Response.StatusCode = partial ? 206 : 200;
                ctx.Response.ContentType = "application/octet-stream";
                ctx.Response.Headers["Accept-Ranges"] = "bytes";
                if (partial)
                    ctx.Response.Headers["Content-Range"] = $"bytes {start}-{end}/{_payload.Length}";
                ctx.Response.ContentLength = len;

                // 分块慢发，制造多个分段同时在途的窗口
                const int chunk = 16 * 1024;
                long sent = 0;
                while (sent < len)
                {
                    int n = (int)Math.Min(chunk, len - sent);
                    await ctx.Response.Body.WriteAsync(_payload.AsMemory((int)(start + sent), n));
                    await ctx.Response.Body.FlushAsync();
                    sent += n;
                    await Task.Delay(15, ctx.RequestAborted);
                }
            }
            catch (OperationCanceledException) { }
            finally { Interlocked.Decrement(ref _active); }
        }

        public async ValueTask DisposeAsync()
        {
            try { await _app.StopAsync(); } catch { }
        }
    }

    private static byte[] Payload(int size)
    {
        var buf = new byte[size];
        new Random(777).NextBytes(buf);
        return buf;
    }

    [Fact]
    public async Task H2_NegotiatesVersion2_AndMultiplexesOverOneConnection()
    {
        string workDir = TestEnv.NewWorkDir();
        string dl = Path.Combine(workDir, "dl");
        string parts = Path.Combine(workDir, "parts");
        Directory.CreateDirectory(dl);

        var payload = Payload(2 * 1024 * 1024);
        await using var server = new H2Server(payload);
        await server.StartAsync();

        try
        {
            await using var downloader = new IdmHttpDownloader();
            var opts = new IdmDownloadOptions
            {
                Url = server.Url(),
                Directory = dl,
                FileName = "f.bin",
                PartsDirectory = parts,
                PartKey = "idm-h2",
                MaxConnections = 4,
                MinSplitSize = 64 * 1024,
                RequestVersion = HttpVersion.Version20,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,   // h2c 必须精确要求
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            var result = await downloader.DownloadAsync(opts, null, cts.Token);

            Assert.Equal(IdmTaskState.Completed, result.State);
            Assert.Equal("2.0", downloader.NegotiatedProtocol);
            Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(dl, "f.bin")));

            // 分段确实并发（否则"多路复用"无从谈起）
            Assert.True(server.MaxConcurrentRequests >= 2,
                $"并发请求峰值只有 {server.MaxConcurrentRequests}，没有发生分段");

            // 关键：所有请求都跑在**同一条 TCP 连接**上
            Assert.Single(server.ConnectionIds.Keys);
        }
        finally
        {
            try { Directory.Delete(workDir, true); } catch { }
        }
    }

    [Fact]
    public async Task Http1Server_StillWorks_AndStaysOnVersion11()
    {
        string workDir = TestEnv.NewWorkDir();
        string dl = Path.Combine(workDir, "dl");
        string parts = Path.Combine(workDir, "parts");
        Directory.CreateDirectory(dl);

        var payload = Payload(256 * 1024);
        using var server = new TestEnv.FileServer();   // HttpListener：只有 HTTP/1.1
        server.AddFile("h1.bin", payload);

        try
        {
            await using var downloader = new IdmHttpDownloader();
            var opts = new IdmDownloadOptions
            {
                Url = server.Url("h1.bin"),
                Directory = dl,
                FileName = "h1.bin",
                PartsDirectory = parts,
                PartKey = "idm-h1",
                // 明确要求 2.0，但策略是"协商不到就降级"——正是默认行为
                RequestVersion = HttpVersion.Version20,
                VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var result = await downloader.DownloadAsync(opts, null, cts.Token);

            Assert.Equal(IdmTaskState.Completed, result.State);
            Assert.Equal("1.1", downloader.NegotiatedProtocol);
            Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(dl, "h1.bin")));
        }
        finally
        {
            try { Directory.Delete(workDir, true); } catch { }
        }
    }
}
