using System.Text;

namespace KokonaDownloader.Core.Engine;

/// <summary>
/// Content-Disposition 文件名解析（RFC 6266）。
///
/// 为什么自己写：实测（<c>.verify/nameprobe</c>，同一份打包 aria2c 1.37.0、同一套启动参数、23 种响应头）
/// 得出 aria2 的两条硬行为——
///  1) 只有 <c>filename*=UTF-8''…</c>（不带 <c>filename=</c>）时 aria2 **是能**解析的，
///     CJK、空格、语言标记、<c>inline</c>、小写 charset、跟 302 重定向全部正确；
///  2) 但只要 ext-value 里出现 attr-char 集合之外的字符（最常见的就是把 <c>(1)</c> 直接写进去，
///     RFC 6266 的 attr-char 不含圆括号），aria2 会**把整条 Content-Disposition 判为无效**，
///     连旁边完全合法的 <c>filename=</c> 也不认，于是落回 URL 末段（往往是临时链接的 token）。
/// 浏览器（含 Edge）对第 2 点是宽容的，所以用户看到的现象是"浏览器里名字对、客户端里名字错"。
/// 早先记的"这是上游 aria2 的限制、超出范围"是错的，错在当时的测试夹具自己写了非法 ext-value
/// （见测试报告 §16 的更正）。
///
/// 这里刻意实现成**比 aria2 宽容**：ext-value 非法字符照样收下，只要还能百分号解码出可用名字就用它；
/// 并且优先采用 <c>filename*</c>（RFC 6266 §5 的建议）。解析不出就返回 null，让调用方退回 aria2 自己决定，
/// 保证最坏情况与修复前完全一致。
/// </summary>
public static class ContentDispositionParser
{
    /// <summary>Windows 不允许出现在文件名里的字符；控制字符一并拒绝。</summary>
    private static readonly char[] Forbidden = { '<', '>', ':', '"', '|', '?', '*' };

    /// <summary>名字长度上限：NTFS 单段 255，留余量给目录与 " (1)" 这类改名后缀。</summary>
    private const int MaxNameLength = 200;

    /// <summary>
    /// 从一条 Content-Disposition 头里取出文件名；取不到、或取到的名字不安全/不可用时返回 null。
    /// </summary>
    public static string? ParseFileName(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return null;

        string? star = null, plain = null;
        foreach (var (key, value) in SplitParameters(header))
        {
            if (key.Equals("filename*", StringComparison.OrdinalIgnoreCase)) star = value;
            else if (key.Equals("filename", StringComparison.OrdinalIgnoreCase) && plain == null) plain = value;
        }

        string? name;
        if (star != null) name = DecodeExtValue(star);
        else if (plain != null) name = DecodePlainValue(plain);
        else return null;

        return Sanitize(name);
    }

    /// <summary>
    /// 把 <c>filename</c> 与 <c>filename*</c> 拆成 (参数名, 原始值) 序列。
    /// 按分号切分时**必须跳过双引号内部**——合法文件名里就常有分号（<c>attachment; filename="a;b.bin"</c>）。
    /// </summary>
    private static IEnumerable<(string Key, string Value)> SplitParameters(string header)
    {
        var start = 0;
        var inQuotes = false;
        var pieces = new List<string>();
        for (var i = 0; i <= header.Length; i++)
        {
            var c = i < header.Length ? header[i] : ';';
            if (c == '"') inQuotes = !inQuotes;
            else if (c == ';' && !inQuotes)
            {
                pieces.Add(header.Substring(start, i - start));
                start = i + 1;
            }
        }
        foreach (var piece in pieces)
        {
            var eq = piece.IndexOf('=');
            if (eq <= 0) continue;
            var key = piece.Substring(0, eq).Trim();
            var value = piece.Substring(eq + 1).Trim();
            if (key.Length == 0) continue;
            yield return (key, value);
        }
    }

    /// <summary>
    /// ext-value = "UTF-8" 'lang' 百分号编码串。缺 charset、charset 不认识、甚至整段没引号
    /// 都不放弃：一律按 UTF-8 尽力解码（这正是与 aria2 的差异所在）。
    /// </summary>
    private static string? DecodeExtValue(string extValue)
    {
        var body = extValue;
        var encoding = Encoding.UTF8;
        var first = extValue.IndexOf('\'');
        var second = first >= 0 ? extValue.IndexOf('\'', first + 1) : -1;
        if (first >= 0 && second > first)
        {
            var charset = extValue.Substring(0, first).Trim();
            body = extValue.Substring(second + 1);
            encoding = ResolveCharset(charset);
        }
        return PercentDecode(body.Trim(), encoding);
    }

    /// <summary>
    /// 普通记法：RFC 6266 规定它是"原样字节串"，**不做百分号解码**（与 aria2 一致：
    /// 服务器写 <c>filename="%E6%8A%A5.pdf"</c> 时磁盘上就该是那个字面名）。
    /// 头里的非 ASCII 字节会被 .NET 按 Latin-1 读入，这里还原成 UTF-8 再返回（对应 aria2 的
    /// <c>--content-disposition-default-utf8=true</c> 行为）。
    /// </summary>
    private static string? DecodePlainValue(string plainValue)
    {
        var s = plainValue.Trim();
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"') s = s.Substring(1, s.Length - 2);
        if (s.Length == 0) return null;
        return RepairMojibake(s);
    }

    private static Encoding ResolveCharset(string charset)
    {
        if (string.IsNullOrWhiteSpace(charset)) return Encoding.UTF8;
        var key = charset.Trim().ToLowerInvariant();
        // us-ascii 实际常被用来标 CJK/latin 混排，按 UTF-8 解更贴近浏览器行为
        if (key is "utf-8" or "utf8" or "us-ascii" or "ascii" or "") return Encoding.UTF8;
        try { return Encoding.GetEncoding(key); }
        catch (ArgumentException) { return Encoding.UTF8; }
    }

    /// <summary>
    /// 把 .NET 按 Latin-1 读进来的原始 UTF-8 字节头还原成真字符；已经是正常文本时原样返回。
    /// 判定条件保守：仅当结果不含替换符、且原串确实只落在 U+0080–U+00FF 区间时才替换。
    /// </summary>
    internal static string RepairMojibake(string s)
    {
        if (s.All(c => c < 0x80)) return s;
        var looksLatin1 = s.Any(c => c is >= '\u0080' and <= '\u00FF');
        if (!looksLatin1) return s;
        try
        {
            var bytes = Encoding.Latin1.GetBytes(s);
            var repaired = Encoding.UTF8.GetString(bytes);
            return repaired.Contains('\uFFFD') ? s : repaired;
        }
        catch (Exception) { return s; }
    }

    /// <summary>百分号解码（按指定字符集处理多字节序列）；遇到非法转义按字面保留，绝不抛异常。</summary>
    internal static string PercentDecode(string s, Encoding encoding)
    {
        if (s.IndexOf('%') < 0) return s;
        using var ms = new MemoryStream(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '%' && i + 2 < s.Length &&
                TryHex(s[i + 1], out var hi) && TryHex(s[i + 2], out var lo))
            {
                ms.WriteByte((byte)(hi << 4 | lo));
                i += 2;
            }
            else ms.WriteByte((byte)s[i]); // 非 ASCII 字符此时已被上游按单字节截断，仅出现在畸形头里
        }
        return encoding.GetString(ms.ToArray());
    }

    private static bool TryHex(char c, out int value)
    {
        value = c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'a' and <= 'f' => c - 'a' + 10,
            >= 'A' and <= 'F' => c - 'A' + 10,
            _ => -1
        };
        return value >= 0;
    }

    /// <summary>只取 basename，拒绝空名/点号/控制字符/Windows 非法字符；过长按 ... 截断（保留扩展名）。</summary>
    private static string? Sanitize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var s = name.Replace('\\', '/');
        var slash = s.LastIndexOf('/');
        if (slash >= 0) s = s.Substring(slash + 1);
        // 控制字符先判（含 NUL）：必须在 Trim 之前，否则 "%00bad.pdf" 会被 Trim 洗成看起来无害的 "bad.pdf"
        if (s.Any(c => c < 32 || c == 127)) return null;
        // 零宽字符/BOM：看不见却会让"同一个文件名"在去重、重命名、搜索时行为诡异，直接剥掉首尾
        s = s.Trim('\u200B', '\u200C', '\u200D', '\uFEFF', ' ', '\t');
        if (s.Length == 0 || s == "." || s == "..") return null;
        if (s.IndexOfAny(Forbidden) >= 0) return null;
        // Windows 保留名（CON/PRN/AUX/NUL/COM1…）会让 CreateFile 直接失败
        var stem = Path.GetFileNameWithoutExtension(s);
        if (stem.Length >= 3 && stem.Length <= 4 && int.TryParse(stem[3..], out var n) && stem[..3] is "COM" or "LPT") return null;
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)) return null;
        if (s.Length > MaxNameLength)
        {
            var ext = Path.GetExtension(s);
            if (ext.Length > 12) ext = string.Empty;
            s = s[..Math.Max(0, MaxNameLength - ext.Length)] + ext;
        }
        return s.Length == 0 ? null : s;
    }
}
