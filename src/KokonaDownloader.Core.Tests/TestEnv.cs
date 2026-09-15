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
    /// 以及把某个路径配置成 302 重定向（模拟"编号链接 → 真实文件名"的下载站）。</summary>
    public sealed class FileServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Dictionary<string, (byte[] Content, Dictionary<string, string> Headers)> _files = new();
        private readonly Dictionary<string, string> _redirects = new();
        public int Port { get; }

        public FileServer()
        {
            Port = GetFreePort();
            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _listener.Start();
            Task.Run(Loop);
        }

        public void AddFile(string path, byte[] content, Dictionary<string, string>? headers = null)
            => _files[path.TrimStart('/')] = (content, headers ?? new Dictionary<string, string>());
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
                _ = Task.Run(() => Handle(ctx));
            }
        }

        private void Handle(HttpListenerContext ctx)
        {
            try
            {
                var path = ctx.Request.Url!.AbsolutePath.TrimStart('/');
                if (_redirects.TryGetValue(path, out var location))
                {
                    ctx.Response.StatusCode = 302;
                    ctx.Response.Headers["Location"] = location;
                }
                else if (_files.TryGetValue(path, out var entry))
                {
                    ctx.Response.StatusCode = 200;
                    foreach (var kv in entry.Headers)
                        ctx.Response.Headers[kv.Key] = kv.Value;
                    ctx.Response.ContentLength64 = entry.Content.Length;
                    ctx.Response.OutputStream.Write(entry.Content);
                }
                else
                {
                    ctx.Response.StatusCode = 404;
                }
                ctx.Response.Close();
            }
            catch { }
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
