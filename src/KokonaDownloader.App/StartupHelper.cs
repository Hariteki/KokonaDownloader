using Microsoft.Win32;

namespace KokonaDownloader.App;

/// <summary>开机自启：写入/移除 HKCU Run 注册表项。</summary>
public static class StartupHelper
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "KokonaDownloader";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) != null;
        }
        catch { return false; }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key == null) return;
            if (enabled)
                key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" --minimized"); // 开机只进托盘，不弹主窗口
            else
                key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception ex) { App.Log($"设置开机自启失败: {ex.Message}"); }
    }
}
