using System.Text.Json;
using System.Text.Json.Serialization;

namespace KokonaDownloader.Core.Settings;

public enum ThemeMode { System, Light, Dark }

/// <summary>应用设置（持久化到 %APPDATA%/KokonaDownloader/settings.json）。</summary>
public sealed class AppSettings
{
    public string DefaultDownloadDir { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    public int MaxConcurrentDownloads { get; set; } = 3;
    public int DefaultConnections { get; set; } = 8;
    public int ApiPort { get; set; } = 16800;
    /// <summary>aria2 RPC 内部端口（与 ApiPort 分离，避免冲突）。</summary>
    public int RpcPort { get; set; } = 16801;
    public string ApiSecret { get; set; } = Engine.EngineConfig.GenerateSecret();
    public bool NotificationsEnabled { get; set; } = true;
    public ThemeMode Theme { get; set; } = ThemeMode.Dark;
    /// <summary>主题配色 id（见 ThemeCatalog，"system" 表示跟随系统强调色）。</summary>
    public string ThemeColorId { get; set; } = KokonaDownloader.Core.Themes.ThemeCatalog.SystemId;
    public long GlobalSpeedLimit { get; set; }
    public bool LaunchAtStartup { get; set; }
    public bool MinimizeToTrayOnClose { get; set; } = true;
    /// <summary>扩展捕获下载后是否自动拦截浏览器原生下载。</summary>
    public bool InterceptBrowserDownloads { get; set; } = true;
}

/// <summary>设置存储：加载/保存/变更通知。</summary>
public sealed class SettingsStore
{
    private readonly string _filePath;
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public AppSettings Current { get; private set; }
    public event EventHandler? Changed;

    public SettingsStore(string filePath)
    {
        _filePath = filePath;
        Current = Load();
    }

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                var json = File.ReadAllText(_filePath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, Opts);
                if (loaded != null) return loaded;
            }
        }
        catch { /* 损坏则用默认值 */ }
        return new AppSettings();
    }

    /// <summary>应用修改并保存。modifier 返回 false 则不保存。</summary>
    public void Update(Func<AppSettings, bool> modifier)
    {
        if (!modifier(Current)) return;
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = _filePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Current, Opts));
            File.Move(tmp, _filePath, overwrite: true);
        }
        catch { }
    }
}

/// <summary>应用数据目录统一管理。</summary>
public static class AppPaths
{
    public static string DataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KokonaDownloader");

    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string TasksFile => Path.Combine(DataDir, "tasks.json");
    public static string NotifiedFile => Path.Combine(DataDir, "notified.json");
    public static string EngineWorkDir => Path.Combine(DataDir, "engine");
    public static string LogFile => Path.Combine(DataDir, "app.log");
}
