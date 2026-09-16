using System.Security.Cryptography;
using System.Text;
using KokonaDownloader.Core.Engine;

namespace KokonaDownloader.Core.Tests;

/// <summary>
/// infohash 提取工具单测（纯函数，无需引擎）。
/// 该工具是 BT 重复任务预检的唯一输入来源：解析错误会导致"重复种子漏检"或"正常种子被误判重复"，
/// 而 .torrent 是用户提供的任意二进制，必须对畸形输入保持健壮（不得抛异常）。
/// </summary>
public class BtHashUtilTests
{
    private const string Hex40Lower = "0123456789abcdef0123456789abcdef01234567";
    private const string Hex40Upper = "0123456789ABCDEF0123456789ABCDEF01234567";

    // ---------- 磁力链接 ----------

    [Fact]
    public void 磁力_标准40位hex被识别()
    {
        Assert.Equal(Hex40Lower, BtHashUtil.FromMagnet($"magnet:?xt=urn:btih:{Hex40Lower}"));
    }

    [Fact]
    public void 磁力_大写hex归一化为小写()
    {
        Assert.Equal(Hex40Lower, BtHashUtil.FromMagnet($"magnet:?xt=urn:btih:{Hex40Upper}"));
    }

    [Fact]
    public void 磁力_参数顺序无关且截断于与号()
    {
        var magnet = $"magnet:?dn=name&xt=urn:btih:{Hex40Lower}&tr=http%3A%2F%2Ftracker.invalid";
        Assert.Equal(Hex40Lower, BtHashUtil.FromMagnet(magnet));
    }

    [Fact]
    public void 磁力_32位base32被转换为hex()
    {
        var raw = new byte[20];
        new Random(5).NextBytes(raw);
        var expected = Convert.ToHexString(raw).ToLowerInvariant();

        Assert.Equal(expected, BtHashUtil.FromMagnet($"magnet:?xt=urn:btih:{ToBase32(raw)}"));
        Assert.Equal(expected, BtHashUtil.FromMagnet($"magnet:?xt=urn:btih:{ToBase32(raw).ToLowerInvariant()}"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("magnet:?dn=no-hash-here")]
    [InlineData("magnet:?xt=urn:btih:tooshort")]
    [InlineData("magnet:?xt=urn:btmh:1220" + Hex40Lower)]           // v2 磁力：当前不支持，须返回 null 而不是误判
    [InlineData("magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef0123456g")] // 40 位但含非法字符
    [InlineData("https://example.com/file.zip")]
    public void 磁力_无法解析时返回null(string? input)
    {
        Assert.Null(BtHashUtil.FromMagnet(input));
    }

    // ---------- 种子文件 ----------

    [Fact]
    public void 种子_与构造器计算出的infohash一致()
    {
        var content = new byte[300 * 1024];
        new Random(9).NextBytes(content);
        var torrent = new TorrentBuilder("hash-test.bin", content, 128 * 1024);

        var hash = BtHashUtil.FromTorrent(torrent.TorrentBytes);
        Assert.Equal(torrent.InfoHashHex, hash);
    }

    [Fact]
    public void 种子_info字典前的其他键不影响结果()
    {
        var content = new byte[64 * 1024];
        var torrent = new TorrentBuilder("two-keys.bin", content, 64 * 1024, "http://tracker.invalid/announce");
        // 构造器已含 announce + info 两个键，顺序按字节序：announce 在前
        Assert.Equal(torrent.InfoHashHex, BtHashUtil.FromTorrent(torrent.TorrentBytes));
    }

    [Fact]
    public void 种子_无法解析时返回null()
    {
        Assert.Null(BtHashUtil.FromTorrent(null));
        Assert.Null(BtHashUtil.FromTorrent(Array.Empty<byte>()));
        Assert.Null(BtHashUtil.FromTorrent(Encoding.ASCII.GetBytes("not-a-torrent-at-all")));
        Assert.Null(BtHashUtil.FromTorrent(Encoding.ASCII.GetBytes("d4:name4:teste"))); // 缺 info 键
    }

    [Fact]
    public void 种子_截断的输入不抛异常()
    {
        var content = new byte[128 * 1024];
        var torrent = new TorrentBuilder("trunc.bin", content, 64 * 1024);

        for (var cut = 1; cut < torrent.TorrentBytes.Length; cut += Math.Max(1, torrent.TorrentBytes.Length / 20))
        {
            var slice = torrent.TorrentBytes.AsSpan(0, cut).ToArray();
            var ex = Record.Exception(() => BtHashUtil.FromTorrent(slice));
            Assert.Null(ex);
        }
    }

    [Fact]
    public void 种子_畸形长度前缀不抛异常()
    {
        // 回归护栏：bencode 字符串长度前缀接近 int.MaxValue 时，ReadString 内部
        // start + len 会溢出为负数，随后按该负数下标访问数组。这里断言"任何畸形输入都不得抛出"。
        var cases = new[]
        {
            "d4:name2147483647:x",                                   // 长度前缀使其越界溢出
            "d4:name2147483640:",
            "d4:info2147483647:x",
            "d4:name99999999999999999999:abc",                        // 超出 long 的字面量
            "d4:name-5:abc",                                          // 负数长度
            "di1e",
            "de",
            "l"
        };
        foreach (var raw in cases)
        {
            var ex = Record.Exception(() => BtHashUtil.FromTorrent(Encoding.ASCII.GetBytes(raw)));
            Assert.Null(ex);
        }
    }

    /// <summary>RFC 4648 Base32（无填充），20 字节 → 32 字符。</summary>
    private static string ToBase32(byte[] data)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var sb = new StringBuilder();
        int bits = 0, value = 0;
        foreach (var b in data)
        {
            value = (value << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                sb.Append(alphabet[(value >> bits) & 0x1F]);
            }
        }
        if (bits > 0) sb.Append(alphabet[(value << (5 - bits)) & 0x1F]);
        return sb.ToString();
    }
}
