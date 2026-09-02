using System.Collections.Concurrent;
using System.Text.Json;

namespace KokonaDownloader.Core.Engine;

/// <summary>任务元数据（aria2 不保存的自定义信息）。</summary>
public sealed class TaskMeta
{
    public string Gid { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public List<string> Urls { get; set; } = new();
    public string? Referer { get; set; }
    public DateTime AddedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public string? FinalState { get; set; }
    /// <summary>是否为 BT 任务（磁力/种子）。</summary>
    public bool IsBt { get; set; }
    /// <summary>BT 任务来源磁力链接（删除墓碑匹配、重新下载用）。</summary>
    public string? SourceMagnet { get; set; }
}

/// <summary>
/// 任务元数据持久化：JSON 文件存储，线程安全，变更时防抖写盘。
/// 设计决策：不依赖数据库，单文件 tasks.json 足够且便于排查；
/// 写入采用"先写临时文件再原子替换"避免损坏。
/// </summary>
public sealed class TaskStore
{
    private readonly string _filePath;
    private readonly ConcurrentDictionary<string, TaskMeta> _metas = new();
    private readonly object _writeLock = new();
    private CancellationTokenSource? _debounceCts;
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public TaskStore(string filePath)
    {
        _filePath = filePath;
        Load();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return;
            var json = File.ReadAllText(_filePath);
            var list = JsonSerializer.Deserialize<List<TaskMeta>>(json);
            if (list == null) return;
            foreach (var m in list) _metas[m.Gid] = m;
        }
        catch { /* 文件损坏时忽略，重新积累 */ }
    }

    public TaskMeta? GetMeta(string gid) => _metas.TryGetValue(gid, out var m) ? m : null;

    public IReadOnlyCollection<TaskMeta> All() => _metas.Values.ToList();

    public void AddMeta(string gid, TaskMeta meta)
    {
        _metas[gid] = meta;
        ScheduleSave();
    }

    public void UpdateFinished(DownloadTaskInfo task)
    {
        if (!_metas.TryGetValue(task.Gid, out var m)) return;
        m.FinishedAt = DateTime.Now;
        m.FinalState = task.State.ToString();
        if (!string.IsNullOrEmpty(task.Name)) m.Name = task.Name;
        ScheduleSave();
    }

    public void RemoveMeta(string gid)
    {
        _metas.TryRemove(gid, out _);
        ScheduleSave();
    }

    /// <summary>防抖保存：500ms 内多次变更只写一次盘。</summary>
    private void ScheduleSave()
    {
        _debounceCts?.Cancel();
        var cts = new CancellationTokenSource();
        _debounceCts = cts;
        Task.Delay(500, cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled) SaveNow();
        }, TaskScheduler.Default);
    }

    public void SaveNow()
    {
        lock (_writeLock)
        {
            try
            {
                var dir = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var json = JsonSerializer.Serialize(_metas.Values.OrderBy(m => m.AddedAt).ToList(), Opts);
                var tmp = _filePath + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, _filePath, overwrite: true);
            }
            catch { /* 写盘失败不致命，下次再试 */ }
        }
    }
}
