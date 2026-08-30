using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KokonaDownloader.Core.Engine;

/// <summary>
/// 删除任务墓碑：记录"用户明确删除过的 URL+目录"哈希。
/// aria2 会话文件每 10 秒落盘，异常退出（断电/强杀/系统重启）时已删除任务仍留在其中，
/// 下次启动 --input-file 会把它们重新加载回来（任务复活）。
/// 引擎启动 aria2 前用墓碑过滤会话文件阻断回流；同 URL+目录被再次添加时自动撤销，
/// 不影响正常重新下载。仅存哈希不存明文，并设条目上限防止无限增长。
/// </summary>
public sealed class TombstoneStore
{
    private sealed class Record
    {
        public string Hash { get; set; } = string.Empty;
        public DateTime RemovedAt { get; set; }
    }

    private const int MaxRecords = 1000;
    private readonly string _filePath;
    private readonly object _lock = new();
    private Dictionary<string, DateTime> _hashes = new();
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public TombstoneStore(string filePath)
    {
        _filePath = filePath;
        Load();
    }

    private static string HashOf(string url, string? dir)
    {
        var raw = $"{url.Trim()}|{dir ?? string.Empty}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
    }

    /// <summary>任务被删除时登记墓碑。</summary>
    public void Mark(IEnumerable<string> urls, string? dir)
    {
        lock (_lock)
        {
            var now = DateTime.Now;
            foreach (var u in urls)
            {
                if (string.IsNullOrWhiteSpace(u)) continue;
                _hashes[HashOf(u, dir)] = now;
            }
            if (_hashes.Count > MaxRecords)
            {
                _hashes = _hashes.OrderByDescending(kv => kv.Value).Take(MaxRecords)
                    .ToDictionary(kv => kv.Key, kv => kv.Value);
            }
        }
        Save();
    }

    /// <summary>同 URL+目录被再次添加时撤销墓碑。</summary>
    public void Unmark(IEnumerable<string> urls, string? dir)
    {
        bool any;
        lock (_lock)
        {
            any = urls.Select(u => HashOf(u, dir)).Any(_hashes.Remove);
        }
        if (any) Save();
    }

    /// <summary>
    /// 启动 aria2 前过滤会话文件：按条目块（URI 首行 + 缩进选项行）解析，
    /// 命中墓碑的块整块移除，返回清除的条目数。
    /// </summary>
    public int PurgeSessionFile(string sessionFile)
    {
        lock (_lock)
        {
            if (_hashes.Count == 0 || !File.Exists(sessionFile)) return 0;
            try
            {
                var lines = File.ReadAllLines(sessionFile);
                var kept = new List<string>(lines.Length);
                var purged = 0;
                var i = 0;
                while (i < lines.Length)
                {
                    var block = new List<string> { lines[i] };
                    i++;
                    while (i < lines.Length && (lines[i].StartsWith(' ') || lines[i].StartsWith('\t')))
                    {
                        block.Add(lines[i]);
                        i++;
                    }
                    var dirLine = block.Select(l => l.TrimStart())
                        .FirstOrDefault(l => l.StartsWith("dir=", StringComparison.OrdinalIgnoreCase));
                    var dir = dirLine is null ? null : dirLine["dir=".Length..].Trim();
                    if (block.Any(l => Matches(l, dir)))
                    {
                        purged++;
                        continue;
                    }
                    kept.AddRange(block);
                }
                if (purged > 0)
                {
                    var tmp = sessionFile + ".tmp";
                    File.WriteAllText(tmp, kept.Count == 0 ? string.Empty : string.Join("\n", kept) + "\n");
                    File.Move(tmp, sessionFile, overwrite: true);
                }
                return purged;
            }
            catch { return 0; }
        }
    }

    private bool Matches(string line, string? dir)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith('#')) return false;
        foreach (var token in trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (_hashes.ContainsKey(HashOf(token, dir))) return true;
        }
        return false;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return;
            var list = JsonSerializer.Deserialize<List<Record>>(File.ReadAllText(_filePath));
            if (list == null) return;
            foreach (var r in list)
                if (!string.IsNullOrEmpty(r.Hash))
                    _hashes[r.Hash] = r.RemovedAt;
        }
        catch { /* 文件损坏时忽略，重新积累 */ }
    }

    private void Save()
    {
        lock (_lock)
        {
            try
            {
                var dir = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var records = _hashes.OrderBy(kv => kv.Value)
                    .Select(kv => new Record { Hash = kv.Key, RemovedAt = kv.Value }).ToList();
                var tmp = _filePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(records, Opts));
                File.Move(tmp, _filePath, overwrite: true);
            }
            catch { /* 写盘失败不致命，下次再试 */ }
        }
    }
}
