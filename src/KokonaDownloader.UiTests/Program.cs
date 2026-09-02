using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using KokonaDownloader.Core.Engine;
using KokonaDownloader.Core.Settings;
using KokonaDownloader.Core.Tests;

namespace KokonaDownloader.UiTests;

/// <summary>
/// UI 截图验证宿主：本机回环做种 → magnet 命令行唤起真实应用（预填对话框）→ UIA 驱动限速与提交 →
/// 分阶段截取对话框/进度窗口方块矩阵/主界面任务卡片/BT 专用页 → 清理现场（杀进程、恢复设置、清理任务记录）。
/// </summary>
internal static class Program
{
    private const string TorrentName = "kokona-ui-matrix.bin";
    private const string SpeedOption = "500 KB/s";

    [STAThread]
    private static int Main()
    {
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
        SetProcessDPIAware();
        try
        {
            RunAsync().GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FAIL] {ex.Message}");
            Console.WriteLine(ex);
            return 1;
        }
    }

    private static async Task RunAsync()
    {
        var repo = FindRepoRoot();
        var appExe = FindNewestAppExe(repo);
        var shotDir = Path.Combine(repo, "screenshot");
        Directory.CreateDirectory(shotDir);
        Console.WriteLine($"[环境] 应用 {appExe}");

        KillExistingApp();
        CleanEngineSession();

        var work = TestEnv.NewWorkDir();
        var dlDir = Path.Combine(work, "downloads");
        Directory.CreateDirectory(dlDir);

        var settingsFile = AppPaths.SettingsFile;
        var settingsBackup = File.Exists(settingsFile) ? File.ReadAllBytes(settingsFile) : null;
        var settingsExisted = settingsBackup != null;

        TorrentBuilder? torrent = null;
        MiniTracker? tracker = null;
        Aria2Seeder? seeder = null;
        Process? appProcess = null;
        var pid = 0;
        try
        {
            // 1) 回环做种：8MB / 128KB 分片 = 64 片，配合 500KB/s 限速把下载窗口拉长到 ~16s，便于采集中间态
            tracker = new MiniTracker();
            var pieceLength = 128 * 1024;
            var content = new byte[pieceLength * 64];
            Random.Shared.NextBytes(content);
            var seedDir = Path.Combine(work, "seeder");
            Directory.CreateDirectory(seedDir);
            torrent = new TorrentBuilder(TorrentName, content, pieceLength, announceUrl: tracker.AnnounceUrl);
            var torrentPath = Path.Combine(work, $"{TorrentName}.torrent");
            await File.WriteAllBytesAsync(torrentPath, torrent.TorrentBytes);
            await File.WriteAllBytesAsync(Path.Combine(seedDir, TorrentName), content);
            seeder = Aria2Seeder.Start(TestEnv.Aria2Path, torrentPath, seedDir, tracker.AnnounceUrl,
                TestEnv.GetFreePort(), Path.Combine(work, "seeder.log"));
            if (!await tracker.WaitForPeerAsync(torrent.InfoHashHex, 20_000))
                throw new TimeoutException($"做种进程 20s 内未注册到 tracker:\n{seeder.ReadLog()}");
            Console.WriteLine($"[OK] 回环做种就绪 infohash={torrent.InfoHashHex} 分片数={torrent.NumPieces}");

            // 2) 测试期把默认下载目录指向临时目录，结束后恢复用户原始设置
            var store = new SettingsStore(settingsFile);
            store.Update(s => { s.DefaultDownloadDir = dlDir; return true; });

            // 3) magnet 命令行唤起真实应用：单实例首启路径 → HandleExternalMagnet → 预填新建下载对话框
            var magnet = $"magnet:?xt=urn:btih:{torrent.InfoHashHex}" +
                         $"&dn={Uri.EscapeDataString(TorrentName)}&tr={Uri.EscapeDataString(tracker.AnnounceUrl)}";
            appProcess = Process.Start(new ProcessStartInfo(appExe, $"\"{magnet}\"") { UseShellExecute = false })
                ?? throw new InvalidOperationException("应用进程启动失败");
            Console.WriteLine($"[OK] 应用已启动 pid={appProcess.Id}");
            pid = appProcess.Id;

            // 4) 等待主窗口与预填对话框，校验磁力已进地址栏
            var appWin = Wait(() => FindAppWindow(pid, ""), TimeSpan.FromSeconds(60), "主窗口");
            var appHwnd = HwndOf(appWin);
            var btnStart = Wait(() => FindIn(appWin, ControlType.Button, "开始下载"),
                TimeSpan.FromSeconds(60), "新建下载对话框");
            VerifyPrefill(appWin, torrent.InfoHashHex);
            BringToFront(appHwnd);
            Thread.Sleep(600);
            Shot(appWin, Path.Combine(shotDir, "ui_01_dialog_magnet_prefilled.png"));

            // 5) 限速 500 KB/s 并提交
            SelectSpeed(appWin, SpeedOption);
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 触发开始下载按钮 Invoke");
            ((InvokePattern)btnStart.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
            try
            {
                WaitTrue(() => FindIn(appWin, ControlType.Button, "开始下载") == null,
                    TimeSpan.FromSeconds(10), "对话框关闭");
            }
            catch (TimeoutException)
            {
                DumpErrorTexts(appWin);
                throw;
            }
            Console.WriteLine("[OK] 对话框已提交，任务已创建");

            // 6) 应用为新任务自动打开进度窗口；分阶段截取方块矩阵中间态
            var prog = Wait(() => FindAppWindow(pid, TorrentName), TimeSpan.FromSeconds(30), "进度窗口");
            var progHwnd = HwndOf(prog);
            Thread.Sleep(5000);
            Shot(prog, Path.Combine(shotDir, "ui_02_progress_matrix_partial.png"));

            BringToFront(appHwnd);
            Thread.Sleep(1000);
            Shot(appWin, Path.Combine(shotDir, "ui_03_main_card_matrix_compact.png"));

            var btTab = Wait(() => FindIn(appWin, ControlType.RadioButton, "BT"), TimeSpan.FromSeconds(10), "BT 标签");
            ((SelectionItemPattern)btTab.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
            Thread.Sleep(1000);
            Shot(appWin, Path.Combine(shotDir, "ui_04_btpage_matrix_partial.png"));

            // 7) 等待下载完成（aria2 默认 prealloc 预分配，文件尺寸不可靠，以进度窗口百分比为准）
            WaitTrue(() => UiPercentDone(prog),
                TimeSpan.FromSeconds(150), $"下载完成（{SpeedOption} 限速约需 20s）");
            Thread.Sleep(2500);

            BringToFront(progHwnd);
            Thread.Sleep(600);
            Shot(prog, Path.Combine(shotDir, "ui_05_progress_matrix_complete.png"));

            BringToFront(appHwnd);
            Thread.Sleep(800);
            Shot(appWin, Path.Combine(shotDir, "ui_06_btpage_matrix_complete.png"));

            if (FindIn(appWin, ControlType.RadioButton, "全部") is { } allTab)
            {
                ((SelectionItemPattern)allTab.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
                Thread.Sleep(800);
                Shot(appWin, Path.Combine(shotDir, "ui_07_main_card_matrix_complete.png"));
            }

            Console.WriteLine("[DONE] 全部截图完成");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FAIL] {ex.Message}");
            try { Diagnose(pid, shotDir); } catch { }
            throw;
        }
        finally
        {
            // 8) 清理现场：杀应用进程树 → 恢复设置 → 按应用自身格式清理测试任务记录 → 关闭回环设施 → 删临时目录
            if (appProcess != null) KillTree(appProcess);
            seeder?.Dispose();
            tracker?.Dispose();
            try
            {
                if (settingsExisted && settingsBackup != null) File.WriteAllBytes(settingsFile, settingsBackup);
                else if (!settingsExisted) File.Delete(settingsFile);
            }
            catch (Exception ex) { Console.WriteLine($"[WARN] 恢复设置失败: {ex.Message}"); }
            try { if (torrent != null) CleanTaskRecords(torrent.InfoHashHex); }
            catch (Exception ex) { Console.WriteLine($"[WARN] 清理任务记录失败: {ex.Message}"); }
            try { Directory.Delete(work, true); } catch { }
        }
    }

    // ---- UIA 辅助 ----

    private static T Wait<T>(Func<T?> probe, TimeSpan timeout, string what) where T : class
    {
        var deadline = DateTime.Now + timeout;
        while (DateTime.Now < deadline)
        {
            T? result = null;
            try { result = probe(); }
            catch (ElementNotAvailableException) { }
            catch (COMException) { }
            if (result != null) return result;
            Thread.Sleep(300);
        }
        throw new TimeoutException($"等待超时: {what}");
    }

    private static void WaitTrue(Func<bool> probe, TimeSpan timeout, string what)
    {
        var deadline = DateTime.Now + timeout;
        while (DateTime.Now < deadline)
        {
            var ok = false;
            try { ok = probe(); }
            catch (ElementNotAvailableException) { }
            catch (COMException) { }
            if (ok) return;
            Thread.Sleep(300);
        }
        throw new TimeoutException($"等待超时: {what}");
    }

    private static AutomationElement? FindAppWindow(int pid, string title)
    {
        var windows = AutomationElement.RootElement.FindAll(TreeScope.Children,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window));
        foreach (AutomationElement w in windows)
        {
            try
            {
                if (w.Current.ProcessId != pid) continue;
                if (title.Length == 0 || w.Current.Name.Contains(title)) return w;
            }
            catch (ElementNotAvailableException) { }
        }
        return null;
    }

    private static AutomationElement? FindIn(AutomationElement root, ControlType type, string name) =>
        root.FindFirst(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, type),
            new PropertyCondition(AutomationElement.NameProperty, name)));

    private static void VerifyPrefill(AutomationElement appWin, string infoHash)
    {
        var edits = appWin.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
        foreach (AutomationElement e in edits)
        {
            try
            {
                var value = ((ValuePattern)e.GetCurrentPattern(ValuePattern.Pattern)).Current.Value;
                if (value.Contains($"urn:btih:{infoHash}", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("[OK] 磁力链接已预填至地址栏");
                    return;
                }
            }
            catch { }
        }
        throw new InvalidOperationException("磁力预填校验失败：地址栏中未找到本次 infohash");
    }

    private static void SelectSpeed(AutomationElement appWin, string optionName)
    {
        var combo = Wait(() => appWin.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ComboBox)),
            TimeSpan.FromSeconds(10), "限速下拉框");
        var expand = (ExpandCollapsePattern)combo.GetCurrentPattern(ExpandCollapsePattern.Pattern);
        var item = FindIn(combo, ControlType.ListItem, optionName);
        if (item == null)
        {
            expand.Expand();
            item = Wait(() => FindIn(combo, ControlType.ListItem, optionName)
                    ?? appWin.FindFirst(TreeScope.Descendants, new AndCondition(
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
                        new PropertyCondition(AutomationElement.NameProperty, optionName))),
                TimeSpan.FromSeconds(5), $"限速选项 {optionName}");
        }
        ((SelectionItemPattern)item.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
        Thread.Sleep(200);
        try { expand.Collapse(); } catch { }
        Console.WriteLine($"[OK] 限速已选择 {optionName}");
    }

    private static bool UiPercentDone(AutomationElement prog)
    {
        try
        {
            var texts = prog.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text));
            foreach (AutomationElement t in texts)
            {
                var m = Regex.Match(t.Current.Name ?? string.Empty, @"^(\d+(?:\.\d+)?)\s*%$");
                if (m.Success && double.TryParse(m.Groups[1].Value, out var v) && v >= 99.9) return true;
            }
        }
        catch { }
        return false;
    }

    private static void DumpErrorTexts(AutomationElement appWin)
    {
        try
        {
            var texts = appWin.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text));
            foreach (AutomationElement t in texts)
            {
                var name = t.Current.Name;
                if (!string.IsNullOrWhiteSpace(name) &&
                    (name.Contains("失败") || name.Contains("无效") || name.Contains("错误")))
                    Console.WriteLine($"[诊断] 对话框错误信息: {name}");
            }
        }
        catch { }
    }

    /// <summary>失败现场诊断：截取应用全部顶层窗口并递归 dump UIA 元素名，进程被清理前保留证据。</summary>
    private static void Diagnose(int pid, string shotDir)
    {
        if (pid == 0) return;
        Console.WriteLine("[诊断] 失败现场：");
        var windows = AutomationElement.RootElement.FindAll(TreeScope.Children,
            new PropertyCondition(AutomationElement.ProcessIdProperty, pid));
        var index = 1;
        foreach (AutomationElement w in windows)
        {
            try
            {
                var name = w.Current.Name;
                Console.WriteLine($"  窗口[{index}] Name=\"{name}\" Class=\"{w.Current.ClassName}\"");
                try { Shot(w, Path.Combine(shotDir, $"diag_{index:00}.png")); }
                catch (Exception ex) { Console.WriteLine($"  截图失败: {ex.Message}"); }
                DumpTree(w, "    ", 0);
                DumpEditValues(w);
                index++;
            }
            catch (ElementNotAvailableException) { }
        }
    }

    private static void DumpTree(AutomationElement root, string indent, int depth)
    {
        if (depth >= 10) return;
        try
        {
            var children = root.FindAll(TreeScope.Children, Condition.TrueCondition);
            var count = 0;
            foreach (AutomationElement c in children)
            {
                if (count++ >= 30) { Console.WriteLine($"{indent}…（截断）"); return; }
                try
                {
                    var name = c.Current.Name;
                    if (!string.IsNullOrWhiteSpace(name))
                        Console.WriteLine($"{indent}{c.Current.ControlType.ProgrammaticName}: {Truncate(name, 70)}");
                    DumpTree(c, indent + "  ", depth + 1);
                }
                catch (ElementNotAvailableException) { }
            }
        }
        catch { }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    // ---- 窗口截图与前台控制 ----

    private static IntPtr HwndOf(AutomationElement el) => (IntPtr)el.Current.NativeWindowHandle;

    private static void BringToFront(IntPtr hwnd)
    {
        ShowWindow(hwnd, 5);
        keybd_event(0x12, 0, 0, UIntPtr.Zero);
        SetForegroundWindow(hwnd);
        keybd_event(0x12, 0, 2, UIntPtr.Zero);
    }

    private static RECT BoundsOf(IntPtr hwnd)
    {
        if (DwmGetWindowAttribute(hwnd, 9, out var r, Marshal.SizeOf<RECT>()) == 0 && r.Right > r.Left)
            return r;
        GetWindowRect(hwnd, out r);
        return r;
    }

    private static void Shot(AutomationElement el, string path)
    {
        var rect = BoundsOf(HwndOf(el));
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0) throw new InvalidOperationException($"窗口尺寸异常，无法截图 {path}");
        using var bmp = new Bitmap(width, height);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(rect.Left, rect.Top, 0, 0, new Size(width, height));
        bmp.Save(path, ImageFormat.Png);
        Console.WriteLine($"[OK] 截图 {Path.GetFileName(path)} ({width}x{height})");
    }

    // ---- 进程与现场清理 ----

    private static void DumpEditValues(AutomationElement root)
    {
        try
        {
            var edits = root.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
            var i = 0;
            foreach (AutomationElement e in edits)
            {
                try
                {
                    var v = ((ValuePattern)e.GetCurrentPattern(ValuePattern.Pattern)).Current.Value;
                    Console.WriteLine($"  [Edit{i}] Value=\"{Truncate(v, 90)}\"");
                    i++;
                }
                catch { }
            }
        }
        catch { }
    }

    private static void CleanEngineSession()
    {
        try
        {
            var session = Path.Combine(AppPaths.EngineWorkDir, "aria2.session");
            if (File.Exists(session))
            {
                File.Delete(session);
                Console.WriteLine("[OK] 已清理上一轮 aria2 会话文件");
            }
        }
        catch (Exception ex) { Console.WriteLine($"[WARN] 清理会话文件失败: {ex.Message}"); }
    }

    private static void KillExistingApp()
    {
        foreach (var p in Process.GetProcessesByName("KokonaDownloader"))
        {
            try { KillTree(p); } catch { }
        }
        Thread.Sleep(1500);
    }

    private static void KillTree(Process p)
    {
        try
        {
            if (p.HasExited) return;
            using var killer = Process.Start(new ProcessStartInfo("taskkill", $"/PID {p.Id} /T /F")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            });
            killer?.WaitForExit(8000);
        }
        catch { }
        finally
        {
            try { p.Dispose(); } catch { }
        }
    }

    private static void CleanTaskRecords(string infoHash)
    {
        var tasksFile = AppPaths.TasksFile;
        if (!File.Exists(tasksFile)) return;
        var store = new TaskStore(tasksFile);
        foreach (var meta in store.All())
        {
            var hit = meta.Name == TorrentName
                || meta.SourceMagnet?.Contains(infoHash, StringComparison.OrdinalIgnoreCase) == true
                || meta.Urls.Any(u => u.Contains(infoHash, StringComparison.OrdinalIgnoreCase));
            if (hit) store.RemoveMeta(meta.Gid);
        }
        store.SaveNow();
        Console.WriteLine("[OK] 测试任务记录已清理");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "vendor", "aria2", "aria2c.exe"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("未定位到仓库根目录（vendor/aria2/aria2c.exe）");
    }

    private static string FindNewestAppExe(string repo)
    {
        var binDir = Path.Combine(repo, "src", "KokonaDownloader.App", "bin");
        if (!Directory.Exists(binDir)) throw new DirectoryNotFoundException("未找到应用输出目录，请先构建 KokonaDownloader.App");
        return Directory.EnumerateFiles(binDir, "KokonaDownloader.exe", SearchOption.AllDirectories)
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .First().FullName;
    }

    // ---- Win32 ----

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT rect, int sizeOfRect);
}
