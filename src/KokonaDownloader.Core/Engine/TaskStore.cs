using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KokonaDownloader.Core.Engine;

/// <summary>任务元数据（aria2 不保存的自定义信息）。</summary>
public sealed class TaskMeta
{
    public string Gid { get; set; } = string.Empty;
    /// <summary>时间戳唯一编号（见 DownloadTaskInfo.TaskNumber），随元数据持久化，重启后按 Gid 恢复关联。</summary>
    public long TaskNumber { get; set; }
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

    // ---- 终态快照（第八轮审计 P2-4：本地历史）----
    // aria2 的 --save-session 实测**不会**写入已完成任务（探针：4 个 complete 任务，会话文件 0 字节），
    // 所以重启后"已完成"这一整段列表必然消失。客户端自己把终态关键字段留档，重启后由
    // DownloadEngine 合成历史行，界面与 /api/tasks 才能继续一致地看到它们。
    /// <summary>任务总长度（字节）。0 表示旧版本元数据没记录，历史行按未知处理。</summary>
    public long TotalLength { get; set; }
    /// <summary>已完成长度（字节）。</summary>
    public long CompletedLength { get; set; }
    /// <summary>落盘完整路径（打开文件/打开文件夹/删除文件都要用它）。</summary>
    public string? FilePath { get; set; }
    /// <summary>任务实际所在目录。</summary>
    public string? Dir { get; set; }
    /// <summary>失败时的 aria2 错误码与文案（历史行要能解释"为什么失败"）。</summary>
    public int ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    /// <summary>是否为"由本地历史合成"的行（不落 JSON，仅运行期标记；见 DownloadEngine.HistoryRow）。</summary>
    [JsonIgnore]
    public bool FromHistory { get; set; }
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
        // 终态快照：aria2 重启后不会把已完成任务带回来，这些字段就是本地历史的唯一来源
        m.TotalLength = task.TotalLength;
        m.CompletedLength = task.TotalLength > 0 ? task.TotalLength : task.CompletedLength;
        if (!string.IsNullOrEmpty(task.FilePath)) m.FilePath = task.FilePath;
        if (!string.IsNullOrEmpty(task.Dir)) m.Dir = task.Dir;
        m.ErrorCode = task.ErrorCode;
        m.ErrorMessage = task.ErrorMessage;
        if (task.IsBt) m.IsBt = true;
        // Urls 保持添加时写入的原值（磁力任务的 aria2 上报会混进 tracker announce 地址，
        // 覆盖掉会让"重新下载"拿到错误的 URI），只在缺失时兜底补一次
        if (m.Urls.Count == 0) m.Urls = task.Urls.ToList();
        ScheduleSave();
    }

    /// <summary>已留下终态快照的元数据（本地历史的数据源），按完成时间倒序。</summary>
    public List<TaskMeta> Finished() => _metas.Values
        .Where(m => m.FinishedAt != null && !string.IsNullOrEmpty(m.FinalState))
        .OrderByDescending(m => m.FinishedAt)
        .ToList();

    /// <summary>删除指定 gid 集合的元数据，返回删除条数（历史修剪 / 失效元数据回收用）。</summary>
    public int RemoveMany(IEnumerable<string> gids)
    {
        var n = 0;
        foreach (var g in gids)
            if (_metas.TryRemove(g, out _)) n++;
        if (n > 0) ScheduleSave();
        return n;
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
