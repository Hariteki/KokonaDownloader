using KokonaDownloader.Core;
using KokonaDownloader.Core.Engine;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace KokonaDownloader.App;

public partial class NewDownloadDialog : ContentDialog
{
    private readonly AppHost _host;

    public NewDownloadDialog(AppHost host)
    {
        _host = host;
        InitializeComponent();
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

    private async void OnPrimary(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var urls = UrlBox.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(u => u.Length > 0).Distinct().ToList();
        if (urls.Count == 0)
        {
            args.Cancel = true;
            ShowError("请输入至少一个下载地址");
            return;
        }
        foreach (var u in urls)
        {
            if (!Uri.TryCreate(u, UriKind.Absolute, out var uri) ||
                (uri.Scheme != "http" && uri.Scheme != "https" && uri.Scheme != "ftp"))
            {
                args.Cancel = true;
                ShowError($"不支持的地址: {u}");
                return;
            }
        }

        args.Cancel = true; // 手动关闭，避免提前消失
        long limit = 0;
        if (LimitBox.SelectedItem is ComboBoxItem item && item.Tag is string tag && long.TryParse(tag, out var l))
            limit = l;

        try
        {
            if (urls.Count == 1)
            {
                await _host.Engine.AddTaskAsync(new NewTaskRequest
                {
                    Urls = urls,
                    Directory = string.IsNullOrWhiteSpace(DirBox.Text) ? null : DirBox.Text.Trim(),
                    FileName = string.IsNullOrWhiteSpace(FileNameBox.Text) ? null : FileNameBox.Text.Trim(),
                    Connections = (int)ConnBox.Value,
                    SpeedLimit = limit
                });
            }
            else
            {
                await _host.Engine.AddTasksAsync(urls.Select(u => new NewTaskRequest
                {
                    Urls = new List<string> { u },
                    Directory = string.IsNullOrWhiteSpace(DirBox.Text) ? null : DirBox.Text.Trim(),
                    Connections = (int)ConnBox.Value,
                    SpeedLimit = limit
                }));
            }
            Hide();
        }
        catch (Exception ex)
        {
            ShowError($"添加失败: {ex.Message}");
        }
    }

    private void ShowError(string msg)
    {
        ErrorText.Text = msg;
        ErrorText.Visibility = Visibility.Visible;
    }
}
