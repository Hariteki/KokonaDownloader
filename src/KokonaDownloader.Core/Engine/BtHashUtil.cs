using System.Security.Cryptography;
using System.Text;

namespace KokonaDownloader.Core.Engine;

/// <summary>
/// BT infohash 提取工具：添加任务前做重复预检（aria2 对已注册种子的重复添加会以失败任务收场，
/// 提前拦截可以给出友好提示且不产生噪音任务）。
/// infohash = 种子文件 bencoded info 字典的 SHA1；磁力链接 xt=urn:btih: 为 40 位 hex 或 32 位 base32。
/// </summary>
public static class BtHashUtil
{
    /// <summary>从磁力链接提取 infohash（小写 hex）；无法解析返回 null。</summary>
    public static string? FromMagnet(string? magnet)
    {
        if (string.IsNullOrWhiteSpace(magnet)) return null;
        var idx = magnet.IndexOf("btih:", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        var start = idx + 5;
        var end = start;
        while (end < magnet.Length && magnet[end] != '&' && magnet[end] != '#') end++;
        var hash = magnet[start..end];
        if (hash.Length == 40 && IsHex(hash)) return hash.ToLowerInvariant();
        if (hash.Length == 32) return Base32ToHex(hash);
        return null;
    }

    /// <summary>从 .torrent 文件原始字节计算 infohash（小写 hex）；解析失败返回 null。</summary>
    public static string? FromTorrent(byte[]? data)
    {
        // 顶层必须是字典：顺序扫描键值对，捕获 "info" 键值的字节区间后做 SHA1
        if (data == null || data.Length < 12 || data[0] != (byte)'d') return null;
        var pos = 1;
        while (pos < data.Length && data[pos] != (byte)'e')
        {
            if (!ReadString(data, ref pos, out var ks, out var kl)) return null;
            var valueStart = pos;
            if (!SkipValue(data, ref pos)) return null;
            if (kl == 4 && data[ks] == (byte)'i' && data[ks + 1] == (byte)'n' &&
                data[ks + 2] == (byte)'f' && data[ks + 3] == (byte)'o')
            {
                var len = pos - valueStart;
                if (len <= 0) return null;
                var info = new byte[len];
                Buffer.BlockCopy(data, valueStart, info, 0, len);
                return Convert.ToHexString(SHA1.HashData(info)).ToLowerInvariant();
            }
        }
        return null;
    }

    private static bool IsHex(string s)
    {
        foreach (var c in s)
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F'))) return false;
        return true;
    }

    private static string? Base32ToHex(string b32)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = new byte[20];
        var bits = 0;
        var value = 0;
        var idx = 0;
        foreach (var ch in b32.ToUpperInvariant())
        {
            var v = alphabet.IndexOf(ch);
            if (v < 0) return null;
            value = (value << 5) | v;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                bytes[idx++] = (byte)((value >> bits) & 0xFF);
            }
        }
        return idx == 20 ? Convert.ToHexString(bytes).ToLowerInvariant() : null;
    }

    /// <summary>读取一个 bencode 字符串（len:bytes），返回内容区间并把 pos 推到其后。</summary>
    private static bool ReadString(byte[] d, ref int pos, out int start, out int len)
    {
        start = -1; len = 0;
        var p = pos;
        while (p < d.Length && d[p] >= (byte)'0' && d[p] <= (byte)'9') p++;
        if (p >= d.Length || p == pos || d[p] != (byte)':') return false;
        if (!int.TryParse(Encoding.ASCII.GetString(d, pos, p - pos), out var l) || l < 0) return false;
        start = p + 1;
        len = l;
        pos = start + l;
        return pos <= d.Length;
    }

    /// <summary>跳过一个任意类型的 bencode 值。</summary>
    private static bool SkipValue(byte[] d, ref int pos)
    {
        if (pos >= d.Length) return false;
        switch (d[pos])
        {
            case (byte)'i':
                var e = Array.IndexOf(d, (byte)'e', pos);
                if (e < 0) return false;
                pos = e + 1;
                return true;
            case (byte)'d':
            case (byte)'l':
                pos++;
                while (pos < d.Length && d[pos] != (byte)'e')
                    if (!SkipValue(d, ref pos)) return false; // 字典键也是字符串，统一走 SkipValue
                if (pos >= d.Length) return false;
                pos++;
                return true;
            default:
                return ReadString(d, ref pos, out _, out _);
        }
    }
}
