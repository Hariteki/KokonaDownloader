using System.Text;

namespace KokonaDownloader.Core.Notifications;

/// <summary>
/// Windows Toast 通知 XML 构建与按钮参数协议（纯逻辑，不依赖 WinRT，可单元测试）。
/// 按钮参数协议：action=openFolder&amp;dir={UriEscape(目录)}&amp;file={UriEscape(文件)}
/// </summary>
public static class ToastPayload
{
    /// <summary>下载完成通知：带"打开文件夹"按钮。</summary>
    public static string BuildDownloadCompleted(string fileName, string dir, string? filePath = null)
    {
        var sb = new StringBuilder();
        sb.Append("<toast>");
        sb.Append("<visual><binding template=\"ToastGeneric\">");
        sb.Append("<text>下载完成</text>");
        sb.Append("<text>").Append(Escape(fileName)).Append("</text>");
        sb.Append("</binding></visual>");
        if (!string.IsNullOrEmpty(dir))
        {
            sb.Append("<actions><action content=\"打开文件夹\" arguments=\"")
              .Append(Escape(OpenFolderArgument(dir, filePath)))
              .Append("\"/></actions>");
        }
        sb.Append("</toast>");
        return sb.ToString();
    }

    /// <summary>下载失败通知（无按钮）。</summary>
    public static string BuildDownloadFailed(string fileName, string error)
    {
        var sb = new StringBuilder();
        sb.Append("<toast>");
        sb.Append("<visual><binding template=\"ToastGeneric\">");
        sb.Append("<text>下载失败</text>");
        sb.Append("<text>").Append(Escape(fileName)).Append("</text>");
        if (!string.IsNullOrEmpty(error))
            sb.Append("<text>").Append(Escape(error)).Append("</text>");
        sb.Append("</binding></visual>");
        sb.Append("</toast>");
        return sb.ToString();
    }

    public static string OpenFolderArgument(string dir, string? filePath = null)
    {
        var arg = $"action=openFolder&dir={Uri.EscapeDataString(dir)}";
        if (!string.IsNullOrEmpty(filePath))
            arg += $"&file={Uri.EscapeDataString(filePath)}";
        return arg;
    }

    /// <summary>解析通知按钮参数。仅当协议为 openFolder 且含目录时返回 true。</summary>
    public static bool TryParseOpenFolder(string? argument, out string dir, out string? file)
    {
        dir = string.Empty;
        file = null;
        if (string.IsNullOrEmpty(argument)) return false;

        var isOpenFolder = false;
        foreach (var part in argument.Split('&'))
        {
            var idx = part.IndexOf('=');
            if (idx <= 0) continue;
            var key = part[..idx];
            var val = part[(idx + 1)..];
            switch (key)
            {
                case "action":
                    if (val != "openFolder") return false;
                    isOpenFolder = true;
                    break;
                case "dir":
                    try { dir = Uri.UnescapeDataString(val); } catch { return false; }
                    break;
                case "file":
                    try { file = Uri.UnescapeDataString(val); } catch { file = null; }
                    break;
            }
        }
        return isOpenFolder && dir.Length > 0;
    }

    private static string Escape(string s) => s
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;")
        .Replace("'", "&apos;");
}
