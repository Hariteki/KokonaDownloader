using System.Globalization;

namespace KokonaDownloader.Core.Themes;

/// <summary>UI 无关的颜色结构（核心层可测，App 层转换为 WinUI Color）。</summary>
public readonly record struct PaletteColor(byte A, byte R, byte G, byte B)
{
    public static bool TryParseHex(string? hex, out PaletteColor color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        var s = hex.Trim().TrimStart('#');
        // 支持 RRGGBB 与 AARRGGBB
        if (s.Length == 6) s = "FF" + s;
        if (s.Length != 8) return false;
        if (!int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return false;
        color = new PaletteColor((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }

    public static PaletteColor ParseHexOrThrow(string hex) =>
        TryParseHex(hex, out var c) ? c : throw new FormatException($"非法颜色: {hex}");
}

/// <summary>颜色数学工具：混合、HSL 亮度调整、对比度。</summary>
public static class ThemeColorMath
{
    /// <summary>线性插值。t=0 返回 a，t=1 返回 b。</summary>
    public static PaletteColor Mix(PaletteColor a, PaletteColor b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return new PaletteColor(
            Lerp(a.A, b.A, t), Lerp(a.R, b.R, t), Lerp(a.G, b.G, t), Lerp(a.B, b.B, t));
    }

    /// <summary>向白色做伽马空间混合（amount 0~1），与 WinUI accent 色阶一致的提亮方式，保证亮度单调递增。</summary>
    public static PaletteColor Lighten(PaletteColor c, double amount)
    {
        var white = new PaletteColor(c.A, 255, 255, 255);
        return Mix(c, white, Math.Clamp(Math.Abs(amount), 0, 1));
    }

    /// <summary>向黑色做伽马空间混合（amount 0~1），保证亮度单调递减。</summary>
    public static PaletteColor Darken(PaletteColor c, double amount)
    {
        var black = new PaletteColor(c.A, 0, 0, 0);
        return Mix(c, black, Math.Clamp(Math.Abs(amount), 0, 1));
    }

    /// <summary>相对亮度（WCAG 定义）。</summary>
    public static double Luminance(PaletteColor c)
    {
        static double Lin(byte ch)
        {
            var s = ch / 255.0;
            return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
    }

    /// <summary>WCAG 对比度（1~21）。</summary>
    public static double ContrastRatio(PaletteColor a, PaletteColor b)
    {
        var la = Luminance(a);
        var lb = Luminance(b);
        var (hi, lo) = la >= lb ? (la, lb) : (lb, la);
        return (hi + 0.05) / (lo + 0.05);
    }

    private static byte Lerp(byte a, byte b, double t) => (byte)Math.Round(a + (b - a) * t);
}

/// <summary>主题调色板静态定义（深色基底 + 每主题专属色罩，窗口填充 alpha≈F2 保持低透明度）。</summary>
public sealed record ThemePalette(
    string Id,
    string Name,
    string Accent,
    string WindowFill,
    string LayerFill,
    string CardFill,
    string CardStroke,
    string TitleText,
    string OnAccent);

/// <summary>解析后的完整主题（含 accent 亮暗变体与交互态），可直接映射到 WinUI 资源。</summary>
public sealed record ResolvedTheme(
    string Id,
    string Name,
    PaletteColor Accent,
    PaletteColor WindowFill,
    PaletteColor LayerFill,
    PaletteColor CardFill,
    PaletteColor CardStroke,
    PaletteColor TitleText,
    PaletteColor OnAccent,
    PaletteColor AccentLight1,
    PaletteColor AccentLight2,
    PaletteColor AccentLight3,
    PaletteColor AccentDark1,
    PaletteColor AccentDark2,
    PaletteColor AccentDark3,
    PaletteColor AccentFillSecondary,
    PaletteColor AccentFillTertiary,
    PaletteColor AccentFillDisabled);

/// <summary>内置主题目录与解析逻辑。</summary>
public static class ThemeCatalog
{
    public const string SystemId = "system";

    /// <summary>内置主题（system 必须在首位；system 的 Accent 仅作系统取色失败时的兜底）。</summary>
    public static readonly IReadOnlyList<ThemePalette> BuiltIn = new[]
    {
        new ThemePalette(SystemId, "跟随系统", "#FF0078D4",
            "#F2202020", "#F0282828", "#F02E2E2E", "#24FFFFFF", "#FFFFFFFF", "#FFFFFFFF"),
        new ThemePalette("orchid", "星岚紫", "#FF8B7CF7",
            "#F21B1A26", "#F0222130", "#F02A2939", "#29FFFFFF", "#FFF3F1FF", "#FFFFFFFF"),
        new ThemePalette("ocean", "海雾青", "#FF35BCCB",
            "#F2121F22", "#F019282B", "#F0203136", "#26FFFFFF", "#FFEAF9FB", "#FF0B2226"),
        new ThemePalette("sakura", "樱花粉", "#FFF472B6",
            "#F2231A21", "#F02B2028", "#F0332730", "#28FFFFFF", "#FFFDEFF6", "#FF2B1620"),
        new ThemePalette("sunset", "落日橙", "#FFF97316",
            "#F2231B14", "#F02B2219", "#F033291E", "#27FFFFFF", "#FFFDF2E8", "#FF26170A"),
        new ThemePalette("forest", "松翠绿", "#FF2FBF8F",
            "#F2122019", "#F0192721", "#F020302A", "#26FFFFFF", "#FFEAF8F2", "#FF0B2419"),
        new ThemePalette("crimson", "绯红", "#FFEF4444",
            "#F2211516", "#F0281B1D", "#F0302324", "#27FFFFFF", "#FFFDECEC", "#FFFFFFFF"),
        new ThemePalette("amber", "琥珀金", "#FFF5B944",
            "#F2221D12", "#F02A2419", "#F0312B1F", "#27FFFFFF", "#FFFFF8E6", "#FF241B05"),
    };

    public static ThemePalette? Find(string? id) =>
        string.IsNullOrEmpty(id) ? null : BuiltIn.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 解析主题为完整数值。system 主题用 osAccent（为空则用内置兜底色），
    /// 其余主题用自身 accent。未知 id 回退 system。
    /// </summary>
    public static ResolvedTheme Resolve(string? id, PaletteColor? osAccent = null)
    {
        var def = Find(id) ?? Find(SystemId)!;
        var accent = def.Id == SystemId
            ? (osAccent ?? PaletteColor.ParseHexOrThrow(def.Accent))
            : PaletteColor.ParseHexOrThrow(def.Accent);

        return new ResolvedTheme(
            def.Id, def.Name, accent,
            PaletteColor.ParseHexOrThrow(def.WindowFill),
            PaletteColor.ParseHexOrThrow(def.LayerFill),
            PaletteColor.ParseHexOrThrow(def.CardFill),
            PaletteColor.ParseHexOrThrow(def.CardStroke),
            PaletteColor.ParseHexOrThrow(def.TitleText),
            PaletteColor.ParseHexOrThrow(def.OnAccent),
            ThemeColorMath.Lighten(accent, 0.15),
            ThemeColorMath.Lighten(accent, 0.30),
            ThemeColorMath.Lighten(accent, 0.45),
            ThemeColorMath.Darken(accent, 0.15),
            ThemeColorMath.Darken(accent, 0.30),
            ThemeColorMath.Darken(accent, 0.45),
            ThemeColorMath.Lighten(accent, 0.10),   // 指针悬停（Secondary）
            ThemeColorMath.Darken(accent, 0.10),    // 按下（Tertiary）
            accent with { A = 0x5D });              // 禁用态
    }
}
