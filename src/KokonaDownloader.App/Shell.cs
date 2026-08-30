using System.Diagnostics;
using System.Runtime.InteropServices;

namespace KokonaDownloader.App;

/// <summary>Shell 操作：打开文件 / 打开文件夹（可定位选中文件）。主界面与通知按钮共用。</summary>
public static class Shell
{
    public static void OpenFile(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { App.Log($"打开文件失败 {path}: {ex.Message}"); }
    }

    public static void OpenFolder(string? dir, string? selectFile = null)
    {
        // aria2 上报的路径可能用正斜杠，explorer 只认反斜杠（正斜杠会导致打开"此电脑"），先规范化
        selectFile = NormalizePath(selectFile);
        dir = NormalizePath(dir);

        try
        {
            // 优先：explorer /select 定位选中文件（路径已规范化为反斜杠，正斜杠会导致 explorer 打开默认位置）
            if (!string.IsNullOrEmpty(selectFile) && File.Exists(selectFile))
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"/select,\"{selectFile}\"",
                        UseShellExecute = false
                    });
                    return;
                }
                catch (Exception ex)
                {
                    App.Log($"explorer /select 失败 {selectFile}: {ex.Message}");
                    // 进程启动失败时退回原生 shell API
                    if (OpenFolderAndSelect(selectFile)) return;
                }
            }
            // dir 为空时从文件路径推导
            if (string.IsNullOrEmpty(dir) && !string.IsNullOrEmpty(selectFile))
                dir = Path.GetDirectoryName(selectFile);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                // UseShellExecute=true 让 Shell 直接打开目录，
                // 避免 Process.Start("explorer.exe", dir) 参数丢失导致跳转到"此电脑"
                Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
            }
            else
            {
                App.Log($"打开文件夹失败：目录不存在 dir={dir} selectFile={selectFile}");
            }
        }
        catch (Exception ex) { App.Log($"打开文件夹失败 {dir}: {ex.Message}"); }
    }

    /// <summary>规范化路径为绝对路径（统一反斜杠），非法路径原样返回。</summary>
    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        try { return Path.GetFullPath(path); }
        catch { return path; }
    }

    /// <summary>原生 shell API：打开文件夹并选中指定文件（SHOpenFolderAndSelectItems）。</summary>
    private static bool OpenFolderAndSelect(string filePath)
    {
        IntPtr folderPidl = IntPtr.Zero;
        IntPtr filePidl = IntPtr.Zero;
        try
        {
            var dir = Path.GetDirectoryName(filePath);
            if (string.IsNullOrEmpty(dir)) return false;
            folderPidl = ILCreateFromPathW(dir);
            filePidl = ILCreateFromPathW(filePath);
            if (folderPidl == IntPtr.Zero || filePidl == IntPtr.Zero) return false;
            var items = new[] { filePidl };
            return SHOpenFolderAndSelectItems(folderPidl, 1, items, 0) == 0; // S_OK
        }
        catch (Exception ex)
        {
            App.Log($"SHOpenFolderAndSelectItems 失败 {filePath}: {ex.Message}");
            return false;
        }
        finally
        {
            if (filePidl != IntPtr.Zero) ILFree(filePidl);
            if (folderPidl != IntPtr.Zero) ILFree(folderPidl);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr ILCreateFromPathW(string pszPath);

    [DllImport("shell32.dll")]
    private static extern int SHOpenFolderAndSelectItems(IntPtr pidlFolder, uint cidl, [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.SysInt)] IntPtr[] apidl, uint dwFlags);

    [DllImport("shell32.dll")]
    private static extern void ILFree(IntPtr pidl);
}
