using System.Net;
using System.Net.Sockets;
using System.Text;
using KokonaDownloader.Core.Engine.Idm;

namespace KokonaDownloader.Core.Tests;

/// <summary>
/// FTP 分段验收。以前这条只能靠外部 pyftpdlib，回环太快抓不到分段，等于没测。
/// 这里用**进程内** FTP 服务器（支持 REST + 限速 + 并发计数），可以确定性地证明
/// "FTP 确实开了多条连接、各自从不同偏移开始取"。
/// </summary>
public sealed class IdmFtpDownloaderTests
{
    /// <summary>最小 FTP 服务器：USER/PASS/TYPE/SIZE/PASV/REST/RETR，数据按块慢速下发。</summary>
    private sealed class MiniFtpServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly byte[] _payload;
        private readonly int _chunkBytes;
        private readonly int _chunkDelayMs;
        private readonly CancellationTokenSource _cts = new();
        private readonly object _lock = new();

        public readonly List<long> RestOffsets = new();
        public int MaxConcurrentRetr;
        private int _activeRetr;

        public MiniFtpServer(byte[] payload, int chunkBytes = 8 * 1024, int chunkDelayMs = 40)
        {
            _payload = payload;
            _chunkBytes = chunkBytes;
            _chunkDelayMs = chunkDelayMs;
            _listener.Start();
            _ = AcceptLoopAsync();
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public string Url(string name) => $"ftp://127.0.0.1:{Port}/{name}";

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
                catch { return; }
                _ = Task.Run(() => HandleControlAsync(client));
            }
        }

        private async Task HandleControlAsync(TcpClient control)
        {
            using var _ = control;
            var stream = control.GetStream();
            var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
            var writer = new StreamWriter(stream, Encoding.ASCII, 1024, leaveOpen: true)
            {
                NewLine = "\r\n",
                AutoFlush = true,
            };

            TcpListener? dataListener = null;
            long restOffset = 0;

            await writer.WriteLineAsync("220 MiniFTP ready");

            try
            {
                while (await reader.ReadLineAsync() is { } line)
                {
                    int sp = line.IndexOf(' ');
                    string cmd = (sp < 0 ? line : line[..sp]).ToUpperInvariant();
                    string arg = sp < 0 ? "" : line[(sp + 1)..];

                    switch (cmd)
                    {
                        case "USER": await writer.WriteLineAsync("331 need password"); break;
                        case "PASS": await writer.WriteLineAsync("230 logged in"); break;
                        case "TYPE": await writer.WriteLineAsync("200 type set"); break;
                        case "SIZE": await writer.WriteLineAsync($"213 {_payload.Length}"); break;
                        case "PASV":
                            dataListener?.Stop();
                            dataListener = new TcpListener(IPAddress.Loopback, 0);
                            dataListener.Start();
                            var ep = (IPEndPoint)dataListener.LocalEndpoint;
                            await writer.WriteLineAsync(
                                $"227 Entering Passive Mode (127,0,0,1,{ep.Port / 256},{ep.Port % 256})");
                            break;
                        case "REST":
                            restOffset = long.TryParse(arg.Trim(), out var off) ? off : 0;
                            lock (_lock) RestOffsets.Add(restOffset);
                            await writer.WriteLineAsync("350 restarting at " + restOffset);
                            break;
                        case "RETR":
                            if (dataListener is null) { await writer.WriteLineAsync("425 no PASV"); break; }
                            await writer.WriteLineAsync("150 opening data connection");
                            await SendDataAsync(dataListener, restOffset);
                            await writer.WriteLineAsync("226 transfer complete");
                            restOffset = 0;
                            break;
                        case "QUIT":
                            await writer.WriteLineAsync("221 bye");
                            return;
                        default:
                            await writer.WriteLineAsync("502 not implemented");
                            break;
                    }
                }
            }
            catch { /* 客户端提前断开是常态 */ }
            finally { dataListener?.Stop(); }
        }

        private async Task SendDataAsync(TcpListener listener, long offset)
        {
            TcpClient data;
            try { data = await listener.AcceptTcpClientAsync(_cts.Token); }
            catch { return; }

            lock (_lock)
            {
                _activeRetr++;
                if (_activeRetr > MaxConcurrentRetr) MaxConcurrentRetr = _activeRetr;
            }
            try
            {
                using (data)
                {
                    var s = data.GetStream();
                    long pos = Math.Clamp(offset, 0, _payload.Length);
                    while (pos < _payload.Length)
                    {
                        int n = (int)Math.Min(_chunkBytes, _payload.Length - pos);
                        await s.WriteAsync(_payload.AsMemory((int)pos, n), _cts.Token);
                        await s.FlushAsync(_cts.Token);
                        pos += n;
                        if (_chunkDelayMs > 0) await Task.Delay(_chunkDelayMs, _cts.Token);
                    }
                }
            }
            catch { /* 客户端读够本段就断开，服务器会收到 RST */ }
            finally
            {
                lock (_lock) _activeRetr--;
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { }
        }
    }

    private static byte[] Payload(int size)
    {
        var buf = new byte[size];
        new Random(4242).NextBytes(buf);
        return buf;
    }

    [Fact]
    public async Task FtpDownload_SplitsIntoMultipleConnections_AndIsByteExact()
    {
        string workDir = TestEnv.NewWorkDir();
        string dl = Path.Combine(workDir, "dl");
        string parts = Path.Combine(workDir, "parts");
        Directory.CreateDirectory(dl);

        var payload = Payload(2 * 1024 * 1024);
        using var server = new MiniFtpServer(payload);

        try
        {
            await using var downloader = new IdmHttpDownloader();
            var opts = new IdmDownloadOptions
            {
                Url = server.Url("file.bin"),
                Directory = dl,
                FileName = "file.bin",
                PartsDirectory = parts,
                PartKey = "idm-ftp1",
                MaxConnections = 4,
                MinSplitSize = 64 * 1024,
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            var result = await downloader.DownloadAsync(opts, null, cts.Token);

            Assert.Equal(IdmTaskState.Completed, result.State);
            Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(dl, "file.bin")));

            // 真·分段：至少两条数据连接同时开着
            Assert.True(server.MaxConcurrentRetr >= 2,
                $"并发数据连接峰值 {server.MaxConcurrentRetr}，FTP 分段没生效");

            // 且各自从不同偏移开始取（REST 0 是首段，不算）
            var offsets = server.RestOffsets.Where(o => o > 0).Distinct().OrderBy(o => o).ToList();
            Assert.True(offsets.Count >= 2,
                $"只出现了 {offsets.Count} 个非零 REST 偏移，说明只有一条连接在搬数据");
        }
        finally
        {
            try { Directory.Delete(workDir, true); } catch { }
        }
    }

    [Fact]
    public void ParsePasv_ParsesHostAndPort()
    {
        var (host, port) = FtpConnection.ParsePasv("Entering Passive Mode (127,0,0,1,195,80)");
        Assert.Equal("127.0.0.1", host);
        Assert.Equal(195 * 256 + 80, port);
    }
}
