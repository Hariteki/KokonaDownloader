using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace KokonaDownloader.Core.Engine.Idm;

/// <summary>
/// 最小 FTP 客户端：只实现分段下载需要的那几个动词。
/// IDM 在 FTP 上没有 Range 头，等价能力是 REST（设置偏移）+ RETR（从这里取到文件尾），
/// 因此每条连接 = 一条独立控制连接 + 一次 REST/RETR，取够本段字节数就断开。
/// </summary>
public sealed class FtpConnection : IDisposable
{
    private readonly TcpClient _control;
    private readonly NetworkStream _stream;
    private readonly byte[] _buffer = new byte[4096];
    private int _bufLen;
    private int _bufPos;

    private FtpConnection(TcpClient control)
    {
        _control = control;
        _stream = control.GetStream();
    }

    public static async Task<FtpConnection> ConnectAsync(Uri uri, CancellationToken ct)
    {
        var client = new TcpClient { NoDelay = true };
        int port = uri.Port > 0 ? uri.Port : 21;
        await client.ConnectAsync(uri.Host, port, ct).ConfigureAwait(false);

        var conn = new FtpConnection(client);
        var (code, text) = await conn.ReadResponseAsync(ct).ConfigureAwait(false);
        if (code != 220) throw new FtpException($"FTP 握手失败: {code} {text}");

        string user = string.IsNullOrEmpty(uri.UserInfo) ? "anonymous" : uri.UserInfo.Split(':')[0];
        string pass = uri.UserInfo.Contains(':') ? uri.UserInfo.Split(':', 2)[1] : "anonymous@";

        code = (await conn.SendAsync($"USER {user}", ct).ConfigureAwait(false)).Code;
        if (code == 331)
            code = (await conn.SendAsync($"PASS {pass}", ct).ConfigureAwait(false)).Code;
        if (code != 230 && code != 202) throw new FtpException($"FTP 登录失败: {code}");

        // 二进制模式：分段偏移依赖字节精确，ASCII 模式会被行尾转换破坏
        await conn.SendAsync("TYPE I", ct).ConfigureAwait(false);
        return conn;
    }

    /// <summary>SIZE 返回文件字节数；服务器不支持时抛 <see cref="FtpException"/>。</summary>
    public async Task<long> SizeAsync(string path, CancellationToken ct)
    {
        var (code, text) = await SendAsync($"SIZE {path}", ct).ConfigureAwait(false);
        if (code != 213) throw new FtpException($"SIZE 不支持: {code} {text}");
        string digits = text.Trim();
        return long.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size)
            ? size
            : throw new FtpException($"SIZE 返回值无法解析: {text}");
    }

    /// <summary>REST offset + RETR，返回数据连接的读取流（调用方读完自行 Dispose）。</summary>
    public async Task<Stream> OpenReadAsync(string path, long offset, CancellationToken ct)
    {
        var (pasvCode, pasvText) = await SendAsync("PASV", ct).ConfigureAwait(false);
        if (pasvCode != 227) throw new FtpException($"PASV 失败: {pasvCode} {pasvText}");
        var (host, port) = ParsePasv(pasvText);

        var data = new TcpClient { NoDelay = true };
        await data.ConnectAsync(host, port, ct).ConfigureAwait(false);

        if (offset > 0)
        {
            var (restCode, restText) = await SendAsync($"REST {offset}", ct).ConfigureAwait(false);
            if (restCode != 350) throw new FtpException($"REST 失败: {restCode} {restText}");
        }

        var (retrCode, retrText) = await SendAsync($"RETR {path}", ct).ConfigureAwait(false);
        if (retrCode is not (125 or 150))
        {
            data.Dispose();
            throw new FtpException($"RETR 失败: {retrCode} {retrText}");
        }

        return new FtpDataStream(data);
    }

    /// <summary>解析 227 响应里的 (h1,h2,h3,h4,p1,p2)。</summary>
    public static (string host, int port) ParsePasv(string text)
    {
        int l = text.IndexOf('('), r = text.IndexOf(')');
        if (l < 0 || r < l) throw new FtpException($"PASV 响应无法解析: {text}");
        var parts = text[(l + 1)..r].Split(',');
        if (parts.Length < 6) throw new FtpException($"PASV 响应字段不足: {text}");
        string host = string.Join('.', parts[0..4].Select(p => p.Trim()));
        int port = int.Parse(parts[4].Trim(), CultureInfo.InvariantCulture) * 256
                   + int.Parse(parts[5].Trim(), CultureInfo.InvariantCulture);
        return (host, port);
    }

    private async Task<(int Code, string Text)> SendAsync(string command, CancellationToken ct)
    {
        var bytes = Encoding.ASCII.GetBytes(command + "\r\n");
        await _stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await _stream.FlushAsync(ct).ConfigureAwait(false);
        return await ReadResponseAsync(ct).ConfigureAwait(false);
    }

    /// <summary>读一条（可能是多行的）响应，返回最终行状态码。</summary>
    private async Task<(int Code, string Text)> ReadResponseAsync(CancellationToken ct)
    {
        string? last = null;
        while (true)
        {
            string? line = await ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) throw new FtpException("FTP 控制连接被关闭");
            if (line.Length < 3) continue;

            // 多行响应：状态码后紧跟 '-'，以"同码 + 空格"结束
            if (line.Length > 3 && line[3] == '-') { last = line; continue; }
            if (int.TryParse(line[..3], out int code)) return (code, line.Length > 4 ? line[4..] : "");
            return (0, line);
        }
    }

    private async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        var sb = new StringBuilder();
        while (true)
        {
            if (_bufPos >= _bufLen)
            {
                _bufLen = await _stream.ReadAsync(_buffer, ct).ConfigureAwait(false);
                _bufPos = 0;
                if (_bufLen <= 0) return sb.Length > 0 ? sb.ToString() : null;
            }
            byte b = _buffer[_bufPos++];
            if (b == (byte)'\n')
            {
                if (sb.Length > 0 && sb[^1] == '\r') sb.Length--;
                return sb.ToString();
            }
            sb.Append((char)b);
            if (sb.Length > 8192) return sb.ToString();
        }
    }

    public void Dispose()
    {
        // 先关流再关 socket：CA2213 要求自有可释放字段显式释放
        try { _stream.Dispose(); } catch { }
        try { _control.Dispose(); } catch { }
    }
}

/// <summary>把数据连接包装成流：读端由调用方控制，Dispose 时一起关掉 socket。</summary>
internal sealed class FtpDataStream : Stream
{
    private readonly TcpClient _client;
    private readonly NetworkStream _inner;

    public FtpDataStream(TcpClient client)
    {
        _client = client;
        _inner = client.GetStream();
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        => _inner.ReadAsync(buffer, ct);
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        => _inner.ReadAsync(buffer, offset, count, ct);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { _inner.Dispose(); } catch { }
            try { _client.Dispose(); } catch { }
        }
        base.Dispose(disposing);
    }
}

public sealed class FtpException : Exception
{
    public FtpException(string message) : base(message) { }
}
