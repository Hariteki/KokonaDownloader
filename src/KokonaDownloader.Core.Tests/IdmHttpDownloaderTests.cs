using System.Net;
using System.Net.Sockets;
using System.Text;
using KokonaDownloader.Core.Engine.Idm;

namespace KokonaDownloader.Core.Tests;

/// <summary>
/// IDM 协议引擎的端到端验证：起一个最小 HTTP 服务器（真实 Range 语义 + 限速），
/// 断言 1) 落盘内容与原文件逐字节一致；2) 服务端确实收到多区间并发请求（即发生了动态切分）。
/// </summary>
public sealed class IdmHttpDownloaderTests : IDisposable
{
    private readonly string _dir;

    public IdmHttpDownloaderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "kokona-idm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private sealed class MiniServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly byte[] _payload;
        private readonly CancellationTokenSource _cts = new();
        public readonly List<string> Ranges = new();
        public readonly List<int> Connections = new();
        public int MaxConcurrent;
        private int _activeNow;
        private readonly object _lock = new();
        private int _connSeq;

        public MiniServer(byte[] payload)
        {
            _payload = payload;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            _ = AcceptLoopAsync();
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
                catch { return; }
                _ = HandleAsync(client);
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            int id;
            lock (_lock) id = ++_connSeq;
            try
            {
                using (client)
                await using (var ns = client.GetStream())
                {
                    var header = new StringBuilder();
                    var buf = new byte[1];
                    while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                    {
                        int n = await ns.ReadAsync(buf.AsMemory(0, 1), _cts.Token);
                        if (n <= 0) return;
                        header.Append((char)buf[0]);
                        if (header.Length > 16384) return;
                    }

                    string text = header.ToString();
                    string range = "";
                    foreach (var line in text.Split("\r\n"))
                        if (line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase))
                            range = line[6..].Trim();

                    lock (_lock)
                    {
                        Ranges.Add(range);
                        Connections.Add(id);
                        _activeNow++;
                        if (_activeNow > MaxConcurrent) MaxConcurrent = _activeNow;
                    }

                    long start = 0, end = _payload.Length - 1;
                    bool partial = false;
                    if (range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
                    {
                        var spec = range[6..].Split(',')[0];
                        var parts = spec.Split('-');
                        start = long.Parse(parts[0]);
                        if (parts.Length > 1 && parts[1].Length > 0) end = long.Parse(parts[1]);
                        partial = true;
                    }

                    long len = end - start + 1;
                    var head = new StringBuilder();
                    head.Append(partial ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n");
                    head.Append("Server: MiniTest\r\n");
                    head.Append("Content-Type: application/octet-stream\r\n");
                    head.Append("Accept-Ranges: bytes\r\n");
                    if (partial) head.Append($"Content-Range: bytes {start}-{end}/{_payload.Length}\r\n");
                    head.Append($"Content-Length: {len}\r\n\r\n");
                    await ns.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), _cts.Token);

                    // 限速：每 64KB 停 12ms → 单连接约 5MB/s，足够触发多次切分
                    const int chunk = 65536;
                    long sent = 0;
                    while (sent < len)
                    {
                        int n = (int)Math.Min(chunk, len - sent);
                        await ns.WriteAsync(_payload.AsMemory((int)(start + sent), n), _cts.Token);
                        sent += n;
                        await Task.Delay(12, _cts.Token);
                    }
                }
            }
            catch { /* 客户端提前断开是正常现象 */ }
            finally
            {
                lock (_lock) _activeNow--;
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
        }
    }

    [Fact]
    public async Task Downloads_CorrectBytes_And_SplitsIntoMultipleRanges()
    {
        // 3MB 伪随机数据（用固定种子，便于比对）
        var payload = new byte[3 * 1024 * 1024];
        new Random(12345).NextBytes(payload);
        using var server = new MiniServer(payload);
        string url = $"http://127.0.0.1:{server.Port}/file.bin";

        await using var downloader = new IdmHttpDownloader();
        var opts = new IdmDownloadOptions
        {
            Url = url,
            Directory = _dir,
            FileName = "out.bin",
            MaxConnections = 4,
            MinSplitSize = 128 * 1024,
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var result = await downloader.DownloadAsync(opts, null, cts.Token);

        string file = Path.Combine(_dir, "out.bin");
        Assert.True(File.Exists(file), "下载产物不存在");
        var actual = await File.ReadAllBytesAsync(file);
        Assert.Equal(payload.Length, actual.Length);
        Assert.True(payload.AsSpan().SequenceEqual(actual), "落盘内容与源文件不一致");

        Assert.Equal(IdmTaskState.Completed, result.State);
        Assert.Equal(payload.Length, result.TotalLength);

        // 关键断言：至少出现过一次 Range 请求（动态切分真的发生了）
        var rangeReqs = server.Ranges.Where(r => r.Length > 0).ToList();
        Assert.True(rangeReqs.Count >= 1,
            $"未观察到 Range 请求，实际请求数={server.Ranges.Count}，说明没有发生 IDM 式切分");
        // 首条连接应当是裸 GET（IDM 的 C0 行为）
        Assert.Equal("", server.Ranges[0]);

        // 必须真的发生过并行：否则"分段"只是把一条连接反复截断重连（曾经的缺陷）
        Assert.True(server.MaxConcurrent >= 2,
            $"并发连接峰值只有 {server.MaxConcurrent}，分段没有产生真正的并行下载");
    }

    [Fact]
    public void ChunkTable_SplitAtGivenPosition_AndLinksSuccessor()
    {
        var table = new ChunkTable();
        table.Initialize(10_000_000);
        table.Claim(0);
        table.Segments[0].Written = 400_000;

        var tail = table.Split(0, 400_000);

        Assert.Equal(0, table.Segments[0].Start);
        Assert.Equal(400_000, table.Segments[0].End);
        Assert.Equal(tail.Id, table.Segments[0].NextId);
        Assert.Equal(400_000, tail.Start);
        Assert.Equal(10_000_000, tail.End);
        Assert.Equal(-1, tail.NextId);
    }

    [Fact]
    public void ChunkTable_RefusesToSplitWithoutProgress()
    {
        var table = new ChunkTable();
        table.Initialize(10_000_000);
        table.Claim(0);
        // Written = 0：切点未知，IDM 也不会在这里切
        Assert.Null(table.PickSplittable(128 * 1024));
    }
}
