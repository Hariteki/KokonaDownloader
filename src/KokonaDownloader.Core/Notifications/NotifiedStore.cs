using System.Text.Json;

namespace KokonaDownloader.Core.Notifications;

/// <summary>
/// 已通知任务 gid 持久化记录：防止进程重启后对同一任务重复弹通知。
/// 保留最近 1000 条，足够覆盖会话恢复窗口。
/// </summary>
public sealed class NotifiedStore
{
    private readonly string _path;
    private readonly List<string> _gids = new();
    private readonly object _lock = new();

    public NotifiedStore(string path)
    {
        _path = path;
        try
        {
            if (File.Exists(path))
            {
                var list = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path));
                if (list != null) _gids.AddRange(list);
            }
        }
        catch { /* 损坏则从头积累 */ }
    }

    public bool Contains(string gid)
    {
        lock (_lock) return _gids.Contains(gid);
    }

    public void Mark(string gid)
    {
        lock (_lock)
        {
            if (_gids.Contains(gid)) return;
            _gids.Add(gid);
            if (_gids.Count > 1000) _gids.RemoveRange(0, _gids.Count - 1000);
            try
            {
                var dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(_path, JsonSerializer.Serialize(_gids));
            }
            catch { }
        }
    }
}
