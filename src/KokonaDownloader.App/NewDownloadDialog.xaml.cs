using KokonaDownloader.Core;
using KokonaDownloader.Core.Engine;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace KokonaDownloader.App;

public partial class NewDownloadDialog : ContentDialog
{
    private readonly AppHost _host;
    private byte[]? _torrentData;
    private string? _torrentName;

    public NewDownloadDialog(AppHost host, string? initialUrl = null)
    {
        _host = host;
        InitializeComponent();
        if (!string.IsNullOrEmpty(initialUrl)) UrlBox.Text = initialUrl;
        DirBox.Text = host.Settings.Current.DefaultDownloadDir;
        ConnBox.Value = host.Settings.Current.DefaultConnections;
        PrimaryButtonClick += OnPrimary;
    }

    private async void OnBrowseDir(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads;
        picker.FileTypeFilter.Add("*");
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWin);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        var folder = await picker.PickSingleFolderAsync();
        if (folder != null) DirBox.Text = folder.Path;
    }

    private async void OnBrowseTorrent(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads;
        picker.FileTypeFilter.Add(".torrent");
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWin);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        var file = await picker.PickSingleFileAsync();
        if (file == null) return;
        try
        {
            var buf = await Windows.Storage.FileIO.ReadBufferAsync(file);
            _torrentData = new byte[buf.Length];
            using var reader = Windows.Storage.Streams.DataReader.FromBuffer(buf);
            reader.ReadBytes(_torrentData);
            _torrentName = file.Name;
            TorrentFileText.Text = file.Name;
            TorrentFileText.Visibility = Visibility.Visible;
            ClearTorrentBtn.Visibility = Visibility.Visible;
            TorrentHint.Visibility = Visibility.Visible;
            FileNamePanel.Visibility = Visibility.Collapsed;
            UrlBox.IsEnabled = false;
        }
        catch (Exception ex)
        {
            ShowError($"读取种子文件失败: {ex.Message}");
        }
    }

    private void OnClearTorrent(object sender, RoutedEventArgs e)
    {
        _torrentData = null;
        _torrentName = null;
        TorrentFileText.Visibility = Visibility.Collapsed;
        ClearTorrentBtn.Visibility = Visibility.Collapsed;
        TorrentHint.Visibility = Visibility.Collapsed;
        FileNamePanel.Visibility = Visibility.Visible;
        UrlBox.IsEnabled = true;
    }

    private async void OnPrimary(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true; // 手动关闭，避免提前消失
        App.Log($"[dialog] OnPrimary 触发 torrent={(_torrentData != null ? _torrentName : "无")}");
        long limit = 0;
        if (LimitBox.SelectedItem is ComboBoxItem item && item.Tag is string tag && long.TryParse(tag, out var l))
            limit = l;
        var dir = string.IsNullOrWhiteSpace(DirBox.Text) ? null : DirBox.Text.Trim();

        try
        {
            // .torrent 文件优先
            if (_torrentData != null)
            {
                await _host.Engine.AddTorrentAsync(_torrentData, new NewTaskRequest
                {
                    Urls = new List<string> { $"torrent://file/{Uri.EscapeDataString(_torrentName ?? "task.torrent")}" },
                    Directory = dir,
                    SpeedLimit = limit
                });
                Hide();
                return;
            }

            var urls = UrlBox.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(u => u.Length > 0).Distinct().ToList();
            if (urls.Count == 0)
            {
                ShowError("请输入至少一个下载地址，或选择 .torrent 文件");
                return;
            }

            foreach (var u in urls)
            {
                if (u.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
                {
                    if (!u.Contains("xt=", StringComparison.OrdinalIgnoreCase))
                    {
                        ShowError($"磁力链接缺少 xt 参数（无效）: {Truncate(u, 60)}");
                        return;
                    }
                    continue;
                }
                if (!Uri.TryCreate(u, UriKind.Absolute, out var uri) ||
                    (uri.Scheme != "http" && uri.Scheme != "https" && uri.Scheme != "ftp"))
                {
                    App.Log($"[dialog] 校验失败 不支持: {Truncate(u, 60)}");
                    ShowError($"不支持的地址: {Truncate(u, 60)}");
                    return;
                }
            }

            var magnets = urls.Where(u => u.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)).ToList();
            var normal = urls.Except(magnets).ToList();

            // 重复预检：相同链接仍在下载中/排队/暂停时弹窗提醒并跳过，不重复添加；
            // 已完成/失败的历史任务不算重复（用户可能已删除文件，需要再次下载）
            if (normal.Count > 0)
            {
                var dups = await _host.Engine.FindActiveDuplicatesAsync(normal);
                if (dups.Count > 0)
                {
                    var dupUrls = dups.SelectMany(t => t.Urls)
                        .Where(u => !string.IsNullOrWhiteSpace(u))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    normal = normal.Where(u => !dupUrls.Contains(u)).ToList();
                    await ShowDuplicateNoticeAsync(
                        $"以下 {dups.Count} 个任务已在下载列表中，无需重复添加：\n{string.Join("\n", dups.Select(FormatTaskLine))}");
                    if (normal.Count == 0 && magnets.Count == 0) return;
                }
            }

            // 磁力链接逐个走 AddTaskAsync（引擎内做 BT 参数特判），不与普通 URL 混批
            foreach (var m in magnets)
            {
                await _host.Engine.AddTaskAsync(new NewTaskRequest
                {
                    Urls = new List<string> { m },
                    Directory = dir,
                    SpeedLimit = limit
                });
            }

            if (normal.Count == 1)
            {
                await _host.Engine.AddTaskAsync(new NewTaskRequest
                {
                    Urls = normal,
                    Directory = dir,
                    FileName = string.IsNullOrWhiteSpace(FileNameBox.Text) ? null : FileNameBox.Text.Trim(),
                    Connections = (int)ConnBox.Value,
                    SpeedLimit = limit
                });
            }
            else if (normal.Count > 1)
            {
                await _host.Engine.AddTasksAsync(normal.Select(u => new NewTaskRequest
                {
                    Urls = new List<string> { u },
                    Directory = dir,
                    Connections = (int)ConnBox.Value,
                    SpeedLimit = limit
                }));
            }
            App.Log("[dialog] 提交完成 → Hide");
            Hide();
        }
        catch (KokonaDownloader.Core.Engine.DuplicateTaskException dex)
        {
            App.Log($"[dialog] 重复任务被拦截: {dex.Message}");
            await ShowDuplicateNoticeAsync(dex.Message);
        }
        catch (Exception ex)
        {
            App.Log($"[dialog] OnPrimary 异常: {ex.Message}");
            ShowError($"添加失败: {ex.Message}");
        }
    }

    /// <summary>重复任务提醒弹窗：盖在新建下载对话框之上，仅告知不重复添加。</summary>
    private async Task ShowDuplicateNoticeAsync(string message)
    {
        var dlg = new ContentDialog
        {
            Title = "任务已在下载中",
            Content = message,
            CloseButtonText = "知道了",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };
        await dlg.ShowAsync();
    }

    private static string FormatTaskLine(DownloadTaskInfo t)
    {
        var name = !string.IsNullOrEmpty(t.Name) ? t.Name : (t.Urls.FirstOrDefault() ?? t.Gid);
        return $"• {Truncate(name, 60)}（{(t.State == TaskState.Paused ? "已暂停" : t.State == TaskState.Seeding ? "做种中" : "下载中")}）";
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private void ShowError(string msg)
    {
        ErrorText.Text = msg;
        ErrorText.Visibility = Visibility.Visible;
    }
}
