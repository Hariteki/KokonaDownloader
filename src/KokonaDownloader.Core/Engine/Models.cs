using System.Text.Json;
using System.Text.Json.Serialization;

namespace KokonaDownloader.Core.Engine;

/// <summary>任务状态（对上层屏蔽 aria2 原始状态字符串）。</summary>
public enum TaskState
{
    Active,     // 下载中
    Waiting,    // 排队等待
    Paused,     // 已暂停
    Completed,  // 已完成
    Failed,     // 失败
    Removed,    // 已删除
    Seeding     // 做种中（BT 专属：下载已完成、正在上传分享）
}

/// <summary>下载任务的运行时快照，供 UI / API 使用。</summary>
public sealed class DownloadTaskInfo
{
    public string Gid { get; set; } = string.Empty;
    /// <summary>时间戳唯一编号（yyyyMMddHHmmssfff，时钟回拨时单调递增）。
    /// 每个新建任务分配一个新编号：即使 URL 与历史已完成任务完全相同也是不同任务，
    /// 重复检测只针对未结束任务，已完成任务不再阻塞相同链接的重新下载。</summary>
    public long TaskNumber { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Dir { get; set; }
    public string? FilePath { get; set; }
    public List<string> Urls { get; set; } = new();
    public string? Referer { get; set; }
    public TaskState State { get; set; }
    public long TotalLength { get; set; }
    public long CompletedLength { get; set; }
    public long DownloadSpeed { get; set; }
    /// <summary>上传速度（BT 专属，字节/秒）。</summary>
    public long UploadSpeed { get; set; }
    /// <summary>HTTP 服务器连接数；BT 任务时为已连接 peer 数。</summary>
    public int Connections { get; set; }
    /// <summary>BT 任务：种子内可用的做种者数量。</summary>
    public int NumSeeders { get; set; }
    public int ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public long SpeedLimit { get; set; }
    public int Split { get; set; }
    public DateTime AddedAt { get; set; } = DateTime.Now;
    public DateTime? FinishedAt { get; set; }

    // ---- BitTorrent 专属 ----
    /// <summary>是否为 BT/磁力任务。</summary>
    public bool IsBt { get; set; }
    /// <summary>种子 infohash（小写 hex），用于重复任务预检。</summary>
    public string? InfoHash { get; set; }
    /// <summary>分片位图（hex 字符串），渲染方块矩阵的核心数据。</summary>
    public string? BitField { get; set; }
    /// <summary>分片总数。</summary>
    public int NumPieces { get; set; }

    public double Progress => TotalLength > 0 ? (double)CompletedLength / TotalLength : 0;

    public TimeSpan? Eta =>
        DownloadSpeed > 0 && TotalLength > CompletedLength
            ? TimeSpan.FromSeconds((TotalLength - CompletedLength) / (double)DownloadSpeed)
            : null;
}

/// <summary>全局统计（速度、任务数）。</summary>
public sealed class GlobalStat
{
    public long DownloadSpeed { get; set; }
    public long UploadSpeed { get; set; }
    public int NumActive { get; set; }
    public int NumWaiting { get; set; }
    public int NumStopped { get; set; }
}

/// <summary>新建下载任务的请求参数。</summary>
public sealed class NewTaskRequest
{
    public required List<string> Urls { get; init; }
    public string? Directory { get; init; }
    public string? FileName { get; init; }
    /// <summary>单任务线程数（0 = 使用默认值）。</summary>
    public int Connections { get; init; }
    /// <summary>单任务限速，字节/秒（0 = 不限速）。</summary>
    public long SpeedLimit { get; init; }
    public string? Referer { get; init; }
    public List<string>? Headers { get; init; }
    /// <summary>附加 aria2 选项（BT 做种参数等，键值对透传给 RPC）。</summary>
    public Dictionary<string, string>? ExtraOptions { get; init; }
}

/// <summary>引擎启动配置。</summary>
public sealed class EngineConfig
{
    public required string Aria2Path { get; init; }
    /// <summary>工作目录：存放会话文件与日志。</summary>
    public required string WorkDir { get; init; }
    public required string DefaultDownloadDir { get; init; }
    public int RpcPort { get; init; } = 16800;
    public string RpcSecret { get; init; } = GenerateSecret();
    public int MaxConcurrentDownloads { get; init; } = 3;
    public int DefaultConnections { get; init; } = 8;
    /// <summary>全局限速，字节/秒（0 = 不限速）。</summary>
    public long GlobalSpeedLimit { get; init; }
    /// <summary>轮询间隔（毫秒）。</summary>
    public int PollIntervalMs { get; init; } = 800;

    // ---- BitTorrent ----
    /// <summary>是否启用 BT/磁力支持（禁用时 aria2 不加载 DHT 等模块）。</summary>
    public bool BtEnabled { get; init; } = true;
    /// <summary>BT 监听端口（TCP/UDP 共用）。</summary>
    public int BtListenPort { get; init; } = 51413;
    /// <summary>是否做种。</summary>
    public bool BtSeedEnabled { get; init; } = true;
    /// <summary>做种分享率（0 = 不限）。</summary>
    public double SeedRatio { get; init; } = 1.0;
    /// <summary>做种时长（分钟，0 = 不限时）。</summary>
    public double SeedTimeMinutes { get; init; }
    /// <summary>BT 最大 peer 数。</summary>
    public int BtMaxPeers { get; init; } = 80;
    /// <summary>额外 tracker 列表（逗号分隔），提升磁力连通性。</summary>
    public string? BtTrackers { get; init; }

    public static string GenerateSecret()
    {
        var bytes = new byte[16];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

#region aria2 JSON-RPC 原始 DTO

public sealed class Aria2TaskStatus
{
    [JsonPropertyName("gid")] public string Gid { get; set; } = string.Empty;
    [JsonPropertyName("status")] public string Status { get; set; } = string.Empty;
    [JsonPropertyName("totalLength")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public long TotalLength { get; set; }
    [JsonPropertyName("completedLength")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public long CompletedLength { get; set; }
    [JsonPropertyName("downloadSpeed")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public long DownloadSpeed { get; set; }
    [JsonPropertyName("uploadSpeed")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public long UploadSpeed { get; set; }
    [JsonPropertyName("connections")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public int Connections { get; set; }
    [JsonPropertyName("errorCode")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public int ErrorCode { get; set; }
    [JsonPropertyName("errorMessage")] public string? ErrorMessage { get; set; }
    [JsonPropertyName("dir")] public string? Dir { get; set; }
    [JsonPropertyName("files")] public List<Aria2File>? Files { get; set; }
    [JsonPropertyName("followedBy")] public List<string>? FollowedBy { get; set; }
    [JsonPropertyName("option")] public Dictionary<string, string>? Option { get; set; }

    // ---- BitTorrent 字段 ----
    /// <summary>种子 infohash（小写 hex），tell* 全字段返回时携带。</summary>
    [JsonPropertyName("infoHash")] public string? InfoHash { get; set; }
    /// <summary>分片位图（hex 字符串），渲染方块矩阵的核心数据。</summary>
    [JsonPropertyName("bitfield")] public string? BitField { get; set; }
    [JsonPropertyName("numPieces")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public long NumPieces { get; set; }
    [JsonPropertyName("pieceLength")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public long PieceLength { get; set; }
    [JsonPropertyName("numSeeders")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public int NumSeeders { get; set; }
    /// <summary>本机是否已为做种者（aria2 JSON-RPC 以字符串 "true"/"false" 返回）。</summary>
    [JsonPropertyName("seeder")] public string? Seeder { get; set; }
    [JsonPropertyName("bittorrent")] public Aria2BittorrentInfo? Bittorrent { get; set; }
}

public sealed class Aria2BittorrentInfo
{
    [JsonPropertyName("mode")] public string? Mode { get; set; }
    [JsonPropertyName("announceList")] public List<List<string>>? AnnounceList { get; set; }
    [JsonPropertyName("info")] public Aria2BittorrentInfoData? Info { get; set; }
}

public sealed class Aria2BittorrentInfoData
{
    [JsonPropertyName("name")] public string? Name { get; set; }
}

public sealed class Aria2File
{
    [JsonPropertyName("path")] public string? Path { get; set; }
    [JsonPropertyName("length")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public long Length { get; set; }
    [JsonPropertyName("completedLength")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public long CompletedLength { get; set; }
    [JsonPropertyName("uris")] public List<Aria2Uri>? Uris { get; set; }
}

public sealed class Aria2Uri
{
    [JsonPropertyName("uri")] public string Uri { get; set; } = string.Empty;
    [JsonPropertyName("status")] public string? Status { get; set; }
}

public sealed class Aria2GlobalStat
{
    [JsonPropertyName("downloadSpeed")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public long DownloadSpeed { get; set; }
    [JsonPropertyName("uploadSpeed")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public long UploadSpeed { get; set; }
    [JsonPropertyName("numActive")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public int NumActive { get; set; }
    [JsonPropertyName("numWaiting")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public int NumWaiting { get; set; }
    [JsonPropertyName("numStopped")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public int NumStopped { get; set; }
}

public sealed class Aria2Version
{
    [JsonPropertyName("version")] public string Version { get; set; } = string.Empty;
    [JsonPropertyName("enabledFeatures")] public List<string>? EnabledFeatures { get; set; }
}

#endregion

/// <summary>aria2 JSON-RPC 调用失败时抛出。</summary>
public sealed class Aria2RpcException : Exception
{
    public int Code { get; }
    public Aria2RpcException(int code, string message) : base($"aria2 RPC 错误 [{code}]: {message}") => Code = code;
}

/// <summary>添加重复任务（同 infohash 的种子任务已存在）时抛出，Message 为可直接展示的友好文案。</summary>
public sealed class DuplicateTaskException : Exception
{
    public string ExistingGid { get; }
    public DuplicateTaskException(string message, string existingGid = "") : base(message) => ExistingGid = existingGid;
}
