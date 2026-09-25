using System.Net;
using KokonaDownloader.Core.Engine;

namespace KokonaDownloader.Core.Tests;

/// <summary>测试辅助：定位 aria2c.exe、启动本地 HTTP 文件服务器。</summary>
internal static class TestEnv
{
    public static string Aria2Path
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "vendor", "aria2", "aria2c.exe");
                if (File.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
            throw new FileNotFoundException("找不到 vendor/aria2/aria2c.exe");
        }
    }

    public static string NewWorkDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kokona_dl_test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>获取一个当前空闲的端口（测试类并行运行时避免随机端口撞车）。</summary>
    public static int GetFreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>简易本地文件服务器，用于真实下载测试。支持为每个文件附加响应头（如 Content-Disposition），
    /// 以及把某个路径配置成 302 重定向（模拟"编号链接 → 真实文件名"的下载站）。
    /// 支持 HTTP Range（206 Partial Content）——断点续传/多线程分片都依赖它：
    /// 缺少 206 时 aria2 的续传请求会因"服务端返回整文件 200"报 errorCode=8 Invalid range header，
    /// 这也是历史上"暂停与恢复"用例偶发失败（而非产品缺陷）的根因。</summary>
    public sealed class FileServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Dictionary<string, (byte[] Content, Dictionary<string, string> Headers, int ChunkBytes, int ChunkDelayMs, bool IgnoreRange)> _files = new();
        private readonly Dictionary<string, string> _redirects = new();
        public int Port { get; }

        /// <summary>收到的带 Range 头的请求数（用于断言"确实走了分片/续传"）。</summary>
        private int _rangeRequests;
        public int RangeRequestCount => Volatile.Read(ref _rangeRequests);

        /// <summary>每个路径被请求的次数（第十轮：用于断言"文件名预解析真的多发了一次请求"，
        /// 以及带显式文件名时不该多发）。</summary>
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _requestCounts = new();
        public int RequestCountFor(string path) =>
            _requestCounts.TryGetValue(path.TrimStart('/'), out var n) ? Volatile.Read(ref n) : 0;

        public FileServer()
        {
            Port = GetFreePort();
            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _listener.Start();
            Task.Run(Loop);
        }

        /// <param name="ignoreRange">true 时**收到 Range 也照样回整份 200**（仍声明 Accept-Ranges: bytes）：
        /// 这就是现实里"不支持断点续传的服务器"，aria2 会报 errorCode=8 Invalid range header。
        /// 第八轮 P2-3（失败后留下大小正确、内容损坏的文件）只能靠它复现。</param>
        public void AddFile(string path, byte[] content, Dictionary<string, string>? headers = null,
            int chunkBytes = 0, int chunkDelayMs = 0, bool ignoreRange = false)
            => _files[path.TrimStart('/')] = (content, headers ?? new Dictionary<string, string>(), chunkBytes, chunkDelayMs, ignoreRange);
        public string Url(string path) => $"http://127.0.0.1:{Port}/{path.TrimStart('/')}";
        /// <summary>把 path 配置为 302 重定向到 location（通常是另一个 Url(...)）。</summary>
        public void AddRedirect(string path, string location) => _redirects[path.TrimStart('/')] = location;

        private async Task Loop()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch { break; }
                _ = Task.Run(() => HandleAsync(ctx));
            }
        }

        private async Task HandleAsync(HttpListenerContext ctx)
        {
            try
            {
                var path = ctx.Request.Url!.AbsolutePath.TrimStart('/');
                _requestCounts.AddOrUpdate(path, 1, (_, n) => n + 1);
                if (_redirects.TryGetValue(path, out var location))
                {
                    ctx.Response.StatusCode = 302;
                    ctx.Response.Headers["Location"] = location;
                }
                else if (_files.TryGetValue(path, out var entry))
                {
                    foreach (var kv in entry.Headers)
                        ctx.Response.Headers[kv.Key] = kv.Value;
                    ctx.Response.Headers["Accept-Ranges"] = "bytes";

                    // Range: bytes=start-end（end 可省略）→ 206 + Content-Range；无法解析则按整文件 200 返回
                    var rangeHeader = ctx.Request.Headers["Range"];
                    var askedForRange = !string.IsNullOrWhiteSpace(rangeHeader);
                    if (askedForRange) Interlocked.Increment(ref _rangeRequests);
                    var range = entry.IgnoreRange ? null : TryParseRange(rangeHeader, entry.Content.Length);
                    if (range is { } r)
                    {
                        var len = r.End - r.Start + 1;
                        ctx.Response.StatusCode = 206;
                        ctx.Response.Headers["Content-Range"] = $"bytes {r.Start}-{r.End}/{entry.Content.Length}";
                        ctx.Response.ContentLength64 = len;
                        await WriteBodyAsync(ctx, entry.Content, r.Start, len, entry.ChunkBytes, entry.ChunkDelayMs);
                    }
                    else
                    {
                        ctx.Response.StatusCode = 200;
                        ctx.Response.ContentLength64 = entry.Content.Length;
                        await WriteBodyAsync(ctx, entry.Content, 0, entry.Content.Length, entry.ChunkBytes, entry.ChunkDelayMs);
                    }
                }
                else
                {
                    ctx.Response.StatusCode = 404;
                }
                ctx.Response.Close();
            }
            catch { }
        }

        /// <summary>
        /// 写响应体。chunkBytes&gt;0 时按块慢速下发（用于制造"可稳定暂停"的在途下载）。
        /// 全程 async + Task.Delay：**不能用 Thread.Sleep**——处理请求跑在线程池线程上，
        /// 慢速下发一旦阻塞线程，多个并发下载就会把线程池占满，
        /// 连带把同一进程里其它测试的 HTTP 调用一起拖死（表现为大面积超时）。
        /// </summary>
        private static async Task WriteBodyAsync(HttpListenerContext ctx, byte[] content, int offset, int length,
            int chunkBytes, int chunkDelayMs)
        {
            var outStream = ctx.Response.OutputStream;
            if (chunkBytes <= 0)
            {
                await outStream.WriteAsync(content, offset, length).ConfigureAwait(false);
                return;
            }
            var sent = 0;
            while (sent < length)
            {
                var n = Math.Min(chunkBytes, length - sent);
                await outStream.WriteAsync(content.AsMemory(offset + sent, n)).ConfigureAwait(false);
                await outStream.FlushAsync().ConfigureAwait(false);
                sent += n;
                if (chunkDelayMs > 0) await Task.Delay(chunkDelayMs).ConfigureAwait(false);
            }
        }

        /// <summary>解析 "bytes=start-end"（只支持单区间，够 aria2 使用）；越界/非法返回 null（走整文件 200）。</summary>
        private static (int Start, int End)? TryParseRange(string? header, int totalLength)
        {
            if (string.IsNullOrWhiteSpace(header) || totalLength <= 0) return null;
            var h = header.Trim();
            if (!h.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) return null;
            h = h["bytes=".Length..];
            if (h.Contains(',')) return null; // 多区间：不实现，退回整文件
            var dash = h.IndexOf('-');
            if (dash < 0) return null;

            var startText = h[..dash].Trim();
            var endText = h[(dash + 1)..].Trim();

            int start, end;
            if (startText.Length == 0)
            {
                // "bytes=-N"：最后 N 字节
                if (!int.TryParse(endText, out var suffix) || suffix <= 0) return null;
                start = Math.Max(0, totalLength - suffix);
                end = totalLength - 1;
            }
            else
            {
                if (!int.TryParse(startText, out start) || start < 0 || start >= totalLength) return null;
                if (endText.Length == 0 || !int.TryParse(endText, out end)) end = totalLength - 1;
                end = Math.Min(end, totalLength - 1);
            }
            if (end < start) return null;
            return (start, end);
        }

        private static int GetFreePort()
        {
            var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        public void Dispose()
        {
            try { _listener.Stop(); _listener.Close(); } catch { }
        }
    }
}
