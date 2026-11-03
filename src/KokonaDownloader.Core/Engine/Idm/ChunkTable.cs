using System.Text.Json;
using System.Text.Json.Serialization;

namespace KokonaDownloader.Core.Engine.Idm;

/// <summary>
/// IDM 分段表：一条记录对应一个 HTTP 区间请求。
/// 复刻自 IDM 任务日志中的 "chunk registry"（startPos / nextID）语义：
///   - 记录按 id 单链表串联，nextID = -1 表示链尾；
///   - 新连接到来时，把「当前正在下载且剩余最多」的段的**剩余区间对半切开**，
///     前段留给原连接继续读，后段交给新连接；
///   - 切点必须落在剩余区间的中间，不能落在"当前已写入位置"：后者会把正在传输的连接
///     当场截断（剩余归零→立刻退出），槽位被新连接顶替，实际只有单连接在传
///     （实测 6MB 只跑出 744KB/s ≈ 单连接限速）。
/// </summary>
public sealed class ChunkTable
{
    /// <summary>段：区间为 [Start, End)，End 不含。</summary>
    public sealed class Segment
    {
        public int Id { get; init; }
        public long Start { get; set; }
        /// <summary>
        /// 区间结束（不含）。管理员会截短它，worker 每轮重读，故用字段而非属性。
        /// 必须标 [JsonInclude]：System.Text.Json 默认只序列化属性，漏了这个字段会导致
        /// 续传状态里 End 全是 0 —— 恢复后所有段都被判成"已完成"，直接产出损坏文件。
        /// </summary>
        [JsonInclude]
        public long End;
        /// <summary>本段已写入字节数。</summary>
        public long Written { get; set; }
        public int NextId { get; set; } = -1;
        public bool Owned { get; set; }

        [JsonIgnore] public long Length => End - Start;
        [JsonIgnore] public long Remaining => Length - Written;
        [JsonIgnore] public bool IsComplete => Remaining <= 0;
    }

    private readonly Dictionary<int, Segment> _segments = new();
    private int _nextId;

    public IReadOnlyDictionary<int, Segment> Segments => _segments;
    public int HeadId { get; private set; } = 0;

    public long TotalLength { get; private set; }
    public long CompletedLength => _segments.Values.Sum(s => Math.Min(s.Written, s.Length));
    public long Remaining => TotalLength - CompletedLength;
    public int ActiveCount => _segments.Values.Count(s => s.Owned && !s.IsComplete);

    /// <summary>建初始状态：整个文件是单段，等价于 IDM 日志里的 "record 0: startPos 0, nextID -1"。</summary>
    public void Initialize(long totalLength)
    {
        _segments.Clear();
        TotalLength = totalLength;
        _nextId = 0;
        var head = new Segment { Id = 0, Start = 0, End = totalLength, NextId = -1 };
        _segments[0] = head;
        HeadId = 0;
        _nextId = 1;   // 关键：0 已被头段占用，新段必须从 1 开始，否则会覆盖头段
    }

    /// <summary>恢复已有分段（续传）。</summary>
    public void Restore(long totalLength, IEnumerable<Segment> segments)
    {
        _segments.Clear();
        TotalLength = totalLength;
        foreach (var s in segments)
        {
            s.Owned = false;
            _segments[s.Id] = s;
        }
        HeadId = _segments.Count == 0 ? -1 : _segments.Keys.Min();
        _nextId = _segments.Count == 0 ? 0 : _segments.Keys.Max() + 1;
    }

    /// <summary>挑一个可切的段：正在下载、剩余最多、且剩余量足以切成两半。</summary>
    public Segment? PickSplittable(long minTailBytes)
    {
        Segment? best = null;
        foreach (var s in _segments.Values)
        {
            if (!s.Owned || s.IsComplete) continue;
            // 只能用「已写到位置」当切点，因此未开始接收的段无法切
            if (s.Written <= 0) continue;
            long pos = s.Start + s.Written;
            if (s.End - pos < Math.Max(minTailBytes, 64 * 1024)) continue;
            if (best is null || s.Remaining > best.Remaining) best = s;
        }
        return best;
    }

    /// <summary>
    /// 在 pos 处切开一个段，返回新建的尾段（由新连接领走）。
    /// 原段被截断到 pos —— 这正是 IDM 让旧连接"读到自己边界就停"的做法。
    /// </summary>
    public Segment Split(int segmentId, long pos)
    {
        var seg = _segments[segmentId];
        if (pos <= seg.Start || pos >= seg.End)
            throw new ArgumentOutOfRangeException(nameof(pos), $"pos={pos} 不在段 [{seg.Start},{seg.End}) 内");

        var tail = new Segment
        {
            Id = _nextId++,
            Start = pos,
            End = seg.End,
            NextId = seg.NextId,
            Written = 0,
        };
        seg.End = pos;
        seg.NextId = tail.Id;
        _segments[tail.Id] = tail;
        return tail;
    }

    /// <summary>把没下完的段重新标记为未领取，供下一轮连接接手（用于断线重连）。</summary>
    public void Release(int segmentId)
    {
        if (_segments.TryGetValue(segmentId, out var s)) s.Owned = false;
    }

    public void Claim(int segmentId)
    {
        if (_segments.TryGetValue(segmentId, out var s)) s.Owned = true;
    }

    /// <summary>按顺序取下一个未完成且无人认领的段。</summary>
    public Segment? ClaimNextUnfinished()
    {
        foreach (var s in _segments.Values.OrderBy(s => s.Start))
        {
            if (!s.IsComplete && !s.Owned)
            {
                s.Owned = true;
                return s;
            }
        }
        return null;
    }

    public bool IsComplete => _segments.Count > 0 && _segments.Values.All(s => s.IsComplete);

    public List<Segment> Snapshot()
    {
        return _segments.Values
            .OrderBy(s => s.Start)
            .Select(s => new Segment
            {
                Id = s.Id, Start = s.Start, End = s.End,
                Written = s.Written, NextId = s.NextId,
            })
            .ToList();
    }
}
