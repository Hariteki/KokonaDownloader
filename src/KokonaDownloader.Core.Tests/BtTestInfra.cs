using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace KokonaDownloader.Core.Tests;

/// <summary>极简 Bencode 编码器：仅覆盖构造 .torrent 与 tracker 响应所需能力（字典键按字节序排序）。</summary>
internal static class Bencode
{
    public static byte[] Encode(long value) => Encoding.ASCII.GetBytes($"i{value}e");

    public static byte[] Encode(string s) => Encode(Encoding.UTF8.GetBytes(s));

    public static byte[] Encode(byte[] bytes)
    {
        var head = Encoding.ASCII.GetBytes($"{bytes.Length}:");
        var result = new byte[head.Length + bytes.Length];
        Buffer.BlockCopy(head, 0, result, 0, head.Length);
        Buffer.BlockCopy(bytes, 0, result, head.Length, bytes.Length);
        return result;
    }

    public static byte[] EncodeDict(IEnumerable<KeyValuePair<string, byte[]>> pairs)
    {
        using var ms = new MemoryStream();
        ms.WriteByte((byte)'d');
        foreach (var (key, value) in pairs.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            ms.Write(Encode(key));
            ms.Write(value);
        }
        ms.WriteByte((byte)'e');
        return ms.ToArray();
    }
}

/// <summary>单文件 .torrent 构造器：生成合法 bencode、分片 SHA1 与 infohash。</summary>
internal sealed class TorrentBuilder
{
    public string Name { get; }
    public byte[] Content { get; }
    public int PieceLength { get; }
    public byte[] TorrentBytes { get; }
    public int NumPieces { get; }

    /// <summary>info 字典的 SHA1（hex 小写），即磁力链接 xt=urn:btih 参数。</summary>
    public string InfoHashHex { get; }

    public TorrentBuilder(string name, byte[] content, int pieceLength = 256 * 1024, string? announceUrl = null)
    {
        Name = name;
        Content = content;
        PieceLength = pieceLength;
        NumPieces = (content.Length + pieceLength - 1) / pieceLength;

        var pieces = new byte[NumPieces * 20];
        for (var i = 0; i < NumPieces; i++)
        {
            var offset = i * pieceLength;
            var len = Math.Min(pieceLength, content.Length - offset);
            SHA1.HashData(content.AsSpan(offset, len)).CopyTo(pieces.AsSpan(i * 20));
        }

        var info = Bencode.EncodeDict(new[]
        {
            new KeyValuePair<string, byte[]>("length", Bencode.Encode((long)content.Length)),
            new KeyValuePair<string, byte[]>("name", Bencode.Encode(name)),
            new KeyValuePair<string, byte[]>("piece length", Bencode.Encode((long)pieceLength)),
            new KeyValuePair<string, byte[]>("pieces", Bencode.Encode(pieces))
        });

        TorrentBytes = Bencode.EncodeDict(new[]
        {
            new KeyValuePair<string, byte[]>("announce", Bencode.Encode(announceUrl ?? "http://tracker.invalid/announce")),
            new KeyValuePair<string, byte[]>("info", info)
        });
        InfoHashHex = Convert.ToHexString(SHA1.HashData(info)).ToLowerInvariant();
    }
}

/// <summary>
/// 本机 mini BT tracker（BEP-3 子集）：接受 announce，返回 127.0.0.1 上其他 peer 的 compact 列表。
/// 全部流量走回环地址，测试不依赖外网 DHT/tracker，结果确定。
/// </summary>
internal sealed class MiniTracker : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Dictionary<string, List<(byte[] PeerId, int Port)>> _swarms = new();
    private readonly object _lock = new();

    public int Port { get; }
    public string AnnounceUrl => $"http://127.0.0.1:{Port}/announce";

    public MiniTracker()
    {
        Port = TestEnv.GetFreePort();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _ = Task.Run(LoopAsync);
    }

    /// <summary>等待指定 info_hash 至少有一个 peer 注册（用于确认做种进程已就绪）。</summary>
    public async Task<bool> WaitForPeerAsync(string infoHashHex, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            lock (_lock)
            {
                if (_swarms.TryGetValue(infoHashHex, out var peers) && peers.Count > 0) return true;
            }
            await Task.Delay(200);
        }
        return false;
    }

    private async Task LoopAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { break; }
            _ = Task.Run(() => Handle(ctx));
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        try
        {
            var q = ParseQuery(ctx.Request.Url!.Query);
            var infoHash = q.TryGetValue("info_hash", out var ih) ? Convert.ToHexString(ih).ToLowerInvariant() : null;
            var peerId = q.TryGetValue("peer_id", out var pi) ? pi : Array.Empty<byte>();
            var port = q.TryGetValue("port", out var p) && int.TryParse(Encoding.ASCII.GetString(p), out var pv) ? pv : 0;
            var evt = q.TryGetValue("event", out var e) ? Encoding.ASCII.GetString(e) : string.Empty;

            var peersBin = Array.Empty<byte>();
            if (infoHash != null && port > 0)
            {
                lock (_lock)
                {
                    if (!_swarms.TryGetValue(infoHash, out var peers))
                        _swarms[infoHash] = peers = new List<(byte[], int)>();
                    peers.RemoveAll(x => x.PeerId.AsSpan().SequenceEqual(peerId));
                    if (evt != "stopped") peers.Add((peerId, port));

                    // compact peers：每项 6 字节 = 4 字节 IPv4 + 2 字节端口（大端），排除请求者自身
                    using var ms = new MemoryStream();
                    foreach (var peer in peers)
                    {
                        if (peer.PeerId.AsSpan().SequenceEqual(peerId)) continue;
                        ms.WriteByte(127);
                        ms.WriteByte(0);
                        ms.WriteByte(0);
                        ms.WriteByte(1);
                        ms.WriteByte((byte)(peer.Port >> 8));
                        ms.WriteByte((byte)(peer.Port & 0xFF));
                    }
                    peersBin = ms.ToArray();
                }
            }

            var body = Bencode.EncodeDict(new[]
            {
                new KeyValuePair<string, byte[]>("complete", Bencode.Encode(1L)),
                new KeyValuePair<string, byte[]>("incomplete", Bencode.Encode(0L)),
                new KeyValuePair<string, byte[]>("interval", Bencode.Encode(1L)),
                new KeyValuePair<string, byte[]>("min interval", Bencode.Encode(1L)),
                new KeyValuePair<string, byte[]>("peers", Bencode.Encode(peersBin))
            });
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/octet-stream";
            ctx.Response.ContentLength64 = body.Length;
            ctx.Response.OutputStream.Write(body);
        }
        catch { }
        finally
        {
            try { ctx.Response.Close(); } catch { }
        }
    }

    /// <summary>解析查询串为原始字节（info_hash/peer_id 是非 UTF-8 的原始二进制，不能用 UnescapeDataString 解码值）。</summary>
    private static Dictionary<string, byte[]> ParseQuery(string query)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            result[Uri.UnescapeDataString(pair[..eq])] = PercentDecode(pair[(eq + 1)..]);
        }
        return result;
    }

    private static byte[] PercentDecode(string s)
    {
        var list = new List<byte>(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '+') list.Add((byte)' ');
            else if (c == '%' && i + 2 < s.Length && Uri.IsHexDigit(s[i + 1]) && Uri.IsHexDigit(s[i + 2]))
            {
                list.Add(Convert.ToByte(s.Substring(i + 1, 2), 16));
                i += 2;
            }
            else list.Add((byte)c);
        }
        return list.ToArray();
    }

    public void Dispose()
    {
        try { _listener.Stop(); _listener.Close(); } catch { }
    }
}

/// <summary>以独立 aria2c 进程做种：校验本地数据后持续做种，供被测引擎下载。</summary>
internal sealed class Aria2Seeder : IDisposable
{
    private readonly Process _process;
    private readonly string _logPath;

    private Aria2Seeder(Process process, string logPath)
    {
        _process = process;
        _logPath = logPath;
    }

    /// <summary>
    /// 启动做种进程。aria2 约定 seed-ratio 与 seed-time 同时指定时任一条件满足即结束，
    /// 故取 seed-ratio=0.0（不限分享率）+ seed-time=525600（一年），保证测试期间持续做种。
    /// </summary>
    public static Aria2Seeder Start(string aria2Path, string torrentPath, string dataDir, string announceUrl, int listenPort, string logPath)
    {
        var args =
            "--enable-dht=false --bt-enable-lpd=false --enable-peer-exchange=false " +
            $"--listen-port={listenPort} --bt-tracker=\"{announceUrl}\" " +
            "--seed-ratio=0.0 --seed-time=525600 --check-integrity=true --file-allocation=none " +
            "--console-log-level=warn --summary-interval=0 " +
            $"--log=\"{logPath}\" --log-level=info " +
            $"--dir=\"{dataDir}\" \"{torrentPath}\"";
        var process = Process.Start(new ProcessStartInfo(aria2Path, args)
        {
            CreateNoWindow = true,
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("无法启动做种 aria2c 进程");
        return new Aria2Seeder(process, logPath);
    }

    public string ReadLog()
    {
        try
        {
            using var fs = new FileStream(_logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            return sr.ReadToEnd();
        }
        catch { return "(做种进程无日志)"; }
    }

    public void Dispose()
    {
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { }
        try { _process.Dispose(); } catch { }
    }
}
