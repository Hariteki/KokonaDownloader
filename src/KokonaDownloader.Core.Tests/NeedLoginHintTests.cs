using System.Net;
using KokonaDownloader.Core.Engine;

namespace KokonaDownloader.Core.Tests;

/// <summary>
/// 401/403 且请求没带 Cookie 时，失败信息要给出可操作提示。
/// 背景：浏览器扩展的站点权限是"可选"的，用户不点「授权访问网站」就永远拿不到 Cookie，
/// 转发过去的请求自然 401；只报一句 HTTP 401 用户根本不知道该做什么。
/// 注意必须走 IDM 引擎（aria2 路径会把消息换成它自己的 "Authorization failed."）。
/// </summary>
public sealed class NeedLoginHintTests
{
    [Fact]
    public async Task 需要登录的站点_失败信息里给出授权提示()
    {
        int port = TestEnv.GetFreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var serve = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); } catch { break; }
                _ = Task.Run(() =>
                {
                    try { ctx.Response.StatusCode = 401; ctx.Response.Close(); } catch { }
                });
            }
        });

        var engine = IdmTestEnv.CreateEngine(out _, out var workDir, out _);
        try
        {
            await engine.StartAsync();
            var task = await engine.AddTaskAsync(new NewTaskRequest
            {
                Urls = new List<string> { $"http://127.0.0.1:{port}/need-login.bin" }
            });

            var deadline = DateTime.Now.AddSeconds(25);
            DownloadTaskInfo? info = null;
            while (DateTime.Now < deadline)
            {
                info = await engine.GetTaskAsync(task.Gid);
                if (info?.State == TaskState.Failed) break;
                await Task.Delay(200);
            }

            Assert.Equal(TaskState.Failed, info!.State);
            Assert.Contains("401", info.ErrorMessage ?? "");
            Assert.Contains("授权访问网站", info.ErrorMessage ?? "");
        }
        finally
        {
            try { listener.Stop(); } catch { }
            await engine.DisposeAsync();
            try { Directory.Delete(workDir, true); } catch { }
        }
    }
}
