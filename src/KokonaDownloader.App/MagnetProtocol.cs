using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using Microsoft.Win32;

namespace KokonaDownloader.App;

/// <summary>
/// magnet: 协议注册（HKCU\Software\Classes\magnet）。
/// 未打包应用无法通过 MSIX 声明协议，采用注册表方式，每次启动刷新可执行文件路径（无需管理员权限）。
/// </summary>
public static class MagnetProtocol
{
    public static void Register()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\magnet");
            key.SetValue(null, "URL:magnet", RegistryValueKind.String);
            key.SetValue("URL Protocol", string.Empty, RegistryValueKind.String);
            using var cmd = key.CreateSubKey(@"shell\open\command");
            cmd.SetValue(null, $"\"{exe}\" \"%1\"", RegistryValueKind.String);
            using var icon = key.CreateSubKey("DefaultIcon");
            icon.SetValue(null, $"{exe},0", RegistryValueKind.String);
        }
        catch (Exception ex)
        {
            App.Log($"magnet 协议注册失败: {ex.Message}");
        }
    }
}

/// <summary>
/// 单实例间磁力链接转发：第二个实例写入命名管道后退出，运行中的实例常驻读取并弹出预填的新建任务对话框。
/// </summary>
public static class MagnetIpc
{
    private static string PipeName => $"KokonaDownloader_Magnet_S{Process.GetCurrentProcess().SessionId}";

    /// <summary>第二实例调用：把磁力链接转发给运行中的实例。</summary>
    public static bool TrySend(string url)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(2000);
            using var writer = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true };
            writer.WriteLine(url);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>首实例调用：启动常驻管道服务，收到 magnet: 链接即回调。</summary>
    public static void StartServer(Action<string> onMagnet)
    {
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(
                        PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync().ConfigureAwait(false);
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
                    {
                        var url = line.Trim();
                        if (url.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) && url.Length > 8)
                            onMagnet(url);
                    }
                }
                catch (Exception ex)
                {
                    App.Log($"magnet 管道服务异常: {ex.Message}");
                    await Task.Delay(1000).ConfigureAwait(false);
                }
            }
        });
    }
}
