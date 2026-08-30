using KokonaDownloader.App.Themes;
using KokonaDownloader.Core;
using KokonaDownloader.Core.Settings;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;

namespace KokonaDownloader.App;

/// <summary>独立设置窗口：Win11 原生圆角窗口 + 深色主题，替代旧版 ContentDialog（其圆角无法生效）。</summary>
public sealed partial class SettingsWindow : Window
{
    private readonly AppHost _host;

    public SettingsWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();

        var s = host.Settings.Current;
        DirBox.Text = s.DefaultDownloadDir;
        MaxConcBox.Value = s.MaxConcurrentDownloads;
        ConnBox.Value = s.DefaultConnections;
        // 存储单位为字节/秒，界面按 MB 展示（四舍五入到整数 MB）
        GlobalLimitBox.Text = s.GlobalSpeedLimit > 0
            ? ((long)Math.Round(s.GlobalSpeedLimit / 1048576.0)).ToString()
            : string.Empty;
        NotifySwitch.IsOn = s.NotificationsEnabled;
        InterceptSwitch.IsOn = s.InterceptBrowserDownloads;
        MinimizeSwitch.IsOn = s.MinimizeToTrayOnClose;
        StartupSwitch.IsOn = StartupHelper.IsEnabled();
        ApiAddressText.Text = $"http://127.0.0.1:{s.ApiPort}";
        SecretText.Text = s.ApiSecret;

        // 注册到主题服务：改主题色卡后本窗口立即刷新（否则 Apply() 不会遍历到设置窗）
        ThemeService.Register(this);
        Closed += (_, _) => ThemeService.Unregister(this);

        // 标题栏融入内容区：无色带、随主题背景，顶部 40px 为拖拽区
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleDrag);
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
        }

        AppWindow.Resize(new SizeInt32(680, 816));
        CenterOnPrimaryDisplay();
    }

    private void CenterOnPrimaryDisplay()
    {
        try
        {
            var area = DisplayArea.Primary;
            var size = AppWindow.Size;
            var x = area.WorkArea.X + (area.WorkArea.Width - size.Width) / 2;
            var y = area.WorkArea.Y + (area.WorkArea.Height - size.Height) / 2;
            AppWindow.Move(new PointInt32(Math.Max(0, x), Math.Max(0, y)));
        }
        catch { }
    }

    // 自定义限速只接受数字：任何非数字字符（含粘贴内容）一律静默取消，不做提示
    private void OnLimitBeforeTextChanging(TextBox sender, TextBoxBeforeTextChangingEventArgs args)
    {
        foreach (var c in args.NewText)
        {
            if (c < '0' || c > '9')
            {
                args.Cancel = true;
                return;
            }
        }
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

    private void OnCopySecret(object sender, RoutedEventArgs e)
    {
        var pkg = new DataPackage();
        pkg.SetText(_host.Settings.Current.ApiSecret);
        Clipboard.SetContent(pkg);
        CopySecretBtn.Content = "已复制 ✓";
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        var dir = DirBox.Text.Trim();
        if (string.IsNullOrEmpty(dir))
        {
            ErrorText.Text = "下载目录不能为空";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        // 空 = 不限速（0）；界面 MB 换算回字节/秒存储
        long globalLimit = 0;
        if (long.TryParse(GlobalLimitBox.Text.Trim(), out var mb) && mb > 0)
            globalLimit = mb * 1048576L;

        _host.Settings.Update(s =>
        {
            s.DefaultDownloadDir = dir;
            s.MaxConcurrentDownloads = (int)MaxConcBox.Value;
            s.DefaultConnections = (int)ConnBox.Value;
            s.GlobalSpeedLimit = globalLimit;
            s.NotificationsEnabled = NotifySwitch.IsOn;
            s.InterceptBrowserDownloads = InterceptSwitch.IsOn;
            s.MinimizeToTrayOnClose = MinimizeSwitch.IsOn;
            s.LaunchAtStartup = StartupSwitch.IsOn;
            return true;
        });
        // 注册表立即生效
        StartupHelper.SetEnabled(StartupSwitch.IsOn);
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();
}
