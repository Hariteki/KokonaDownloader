using KokonaDownloader.Core.Settings;

namespace KokonaDownloader.Core.Tests;

public class SettingsTests : IDisposable
{
    private readonly string _dir = TestEnv.NewWorkDir();

    [Fact]
    public void 默认设置合理()
    {
        var s = new AppSettings();
        Assert.Equal(3, s.MaxConcurrentDownloads);
        Assert.Equal(8, s.DefaultConnections);
        Assert.True(s.NotificationsEnabled);
        Assert.True(s.InterceptBrowserDownloads);
        // 主题系统固定深色基底（8 套内置调色板均为深色），默认 Dark 与设计一致
        Assert.Equal(ThemeMode.Dark, s.Theme);
        Assert.Equal(KokonaDownloader.Core.Themes.ThemeCatalog.SystemId, s.ThemeColorId);
        Assert.Equal(32, s.ApiSecret.Length);
    }

    [Fact]
    public void 保存与重新加载()
    {
        var path = Path.Combine(_dir, "settings.json");
        var store = new SettingsStore(path);
        store.Update(s => { s.MaxConcurrentDownloads = 5; s.Theme = ThemeMode.Dark; s.ApiPort = 17999; return true; });

        var store2 = new SettingsStore(path);
        Assert.Equal(5, store2.Current.MaxConcurrentDownloads);
        Assert.Equal(ThemeMode.Dark, store2.Current.Theme);
        Assert.Equal(17999, store2.Current.ApiPort);
    }

    [Fact]
    public void 损坏文件回退默认值()
    {
        var path = Path.Combine(_dir, "bad.json");
        File.WriteAllText(path, "garbage!!!");
        var store = new SettingsStore(path);
        Assert.Equal(3, store.Current.MaxConcurrentDownloads);
    }

    [Fact]
    public void Update返回false不触发变更()
    {
        var path = Path.Combine(_dir, "s2.json");
        var store = new SettingsStore(path);
        var fired = false;
        store.Changed += (_, _) => fired = true;
        store.Update(_ => false);
        Assert.False(fired);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }
}
