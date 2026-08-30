using System.Runtime.InteropServices;
using KokonaDownloader.Core.Notifications;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace KokonaDownloader.App;

/// <summary>
/// 系统通知服务：Windows Toast 通知（未打包应用经典方案）。
/// 实测结论（本机 Win11 25H2）：
///  - WASDK AppNotificationManager 未打包通道注册"成功"但静默不弹横幅（通知设置里无应用条目）；
///  - .NET 8 NotifyIcon 气泡送达但不弹横幅（.NET Framework 进程的气泡正常）；
///  - 注册 AppUserModelID 快捷方式后，ToastNotificationManager 通道稳定弹横幅。
/// 首次启动在开始菜单创建带 AUMID 的快捷方式，随后以该 AUMID 投递 Toast；
/// 点击"打开文件夹"按钮（或通知正文）回调到本进程处理。
/// </summary>
public sealed class NotificationService
{
    private const string AppId = "Kokona.Downloader";

    private readonly Action<string> _log;
    private readonly bool _ready;
    private readonly List<ToastNotification> _live = new();

    public NotificationService(Action<string>? log = null)
    {
        _log = log ?? (_ => { });
        try
        {
            SetCurrentProcessExplicitAppUserModelID(AppId);
            EnsureShortcutWithAumid();
            _ready = true;
        }
        catch (Exception ex)
        {
            _log($"通知服务初始化失败（通知将不可用）: {ex.Message}");
        }
    }

    public void ShowDownloadCompleted(string fileName, string dir, string? filePath)
        => Show(ToastPayload.BuildDownloadCompleted(fileName, dir, filePath));

    public void ShowDownloadFailed(string fileName, string error)
        => Show(ToastPayload.BuildDownloadFailed(fileName, error));

    private void Show(string xml)
    {
        if (!_ready) return;
        try
        {
            var doc = new XmlDocument();
            doc.LoadXml(xml);
            var toast = new ToastNotification(doc);
            toast.Activated += OnToastActivated;
            toast.Dismissed += (_, _) => _live.Remove(toast);
            toast.Failed += (_, e) => _log($"通知显示失败: {e.ErrorCode}");
            _live.Add(toast); // 保活，事件才能回调
            ToastNotificationManager.CreateToastNotifier(AppId).Show(toast);
            _log("通知已投递");
        }
        catch (Exception ex) { _log($"通知发送失败: {ex.Message}"); }
    }

    private void OnToastActivated(ToastNotification sender, object args)
    {
        try
        {
            var argument = (args as ToastActivatedEventArgs)?.Arguments ?? string.Empty;
            _log($"通知激活: argument={argument}");
            if (ToastPayload.TryParseOpenFolder(argument, out var dir, out var file))
            {
                _log($"通知打开文件夹: dir={dir} file={file}");
                Shell.OpenFolder(dir, file);
            }
            else
            {
                // 点击正文：弹出主界面
                App.ShowMainWindow();
            }
        }
        catch (Exception ex) { _log($"通知激活处理失败: {ex.Message}"); }
    }

    #region AUMID 快捷方式注册（未打包应用必需）

    [DllImport("shell32.dll")]
    private static extern void SetCurrentProcessExplicitAppUserModelID([MarshalAs(UnmanagedType.LPWStr)] string appId);

    /// <summary>在开始菜单创建带 AppUserModelID 的快捷方式（已存在且 AUMID 正确则跳过）。</summary>
    private static void EnsureShortcutWithAumid()
    {
        var shortcutPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            "海兔下载器.lnk");
        var exePath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "KokonaDownloader.exe");

        // 已存在且 AUMID 正确则跳过（避免每次启动重写）
        if (File.Exists(shortcutPath) && ReadShortcutAumid(shortcutPath) == AppId) return;

        var link = (IShellLinkW)new ShellLink();
        link.SetPath(exePath);
        link.SetDescription("海兔下载器");
        link.SetWorkingDirectory(Path.GetDirectoryName(exePath) ?? string.Empty);
        link.SetIconLocation(exePath, 0); // 通知横幅/开始菜单图标跟随应用图标（exe 内嵌 app.ico）

        var key = new PROPERTYKEY { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 5 }; // PKEY_AppUserModel_ID
        var pv = PropVariant.FromString(AppId);
        try
        {
            var store = (IPropertyStore)link;
            store.SetValue(ref key, ref pv);
            store.Commit();
        }
        finally { pv.Clear(); }

        ((IPersistFile)link).Save(shortcutPath, true);
    }

    private static string? ReadShortcutAumid(string shortcutPath)
    {
        try
        {
            var link = (IShellLinkW)new ShellLink();
            ((IPersistFile)link).Load(shortcutPath, 0);
            var key = new PROPERTYKEY { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 5 };
            var store = (IPropertyStore)link;
            if (store.GetValue(ref key, out var pv) != 0) return null;
            try { return pv.vt == 31 ? Marshal.PtrToStringUni(pv.pwszVal) : null; }
            finally { pv.Clear(); }
        }
        catch { return null; }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszFile, int cchMaxPath, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszName, int cchMaxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszDir, int cchMaxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszArgs, int cchMaxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszIconPath, int cchIconPath, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport, Guid("0000010B-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint cProps);
        [PreserveSig] int GetAt(uint iProp, out PROPERTYKEY pkey);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PropVariant pv);
        [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PropVariant pv);
        [PreserveSig] int Commit();
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PROPERTYKEY { public Guid fmtid; public uint pid; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort vt;
        public ushort wReserved1;
        public ushort wReserved2;
        public ushort wReserved3;
        public IntPtr pwszVal;

        public static PropVariant FromString(string s) =>
            new() { vt = 31 /* VT_LPWSTR */, pwszVal = Marshal.StringToCoTaskMemUni(s) };

        public void Clear()
        {
            if (pwszVal != IntPtr.Zero) { Marshal.FreeCoTaskMem(pwszVal); pwszVal = IntPtr.Zero; }
        }
    }

    #endregion
}
