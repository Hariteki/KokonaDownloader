using KokonaDownloader.Core.Themes;

namespace KokonaDownloader.Core.Tests;

public class ThemeCatalogTests
{
    [Fact]
    public void 内置主题_至少包含system与7个配色()
    {
        Assert.True(ThemeCatalog.BuiltIn.Count >= 8);
        Assert.Equal(ThemeCatalog.SystemId, ThemeCatalog.BuiltIn[0].Id);
    }

    [Fact]
    public void 内置主题_Id唯一且非空()
    {
        var ids = ThemeCatalog.BuiltIn.Select(p => p.Id).ToList();
        Assert.All(ids, id => Assert.False(string.IsNullOrWhiteSpace(id)));
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Theory]
    [InlineData(0), InlineData(1), InlineData(2), InlineData(3),
     InlineData(4), InlineData(5), InlineData(6), InlineData(7)]
    public void 内置主题_所有颜色字段均为合法hex(int index)
    {
        var p = ThemeCatalog.BuiltIn[index];
        foreach (var hex in new[] { p.Accent, p.WindowFill, p.LayerFill, p.CardFill, p.CardStroke, p.TitleText, p.OnAccent })
        {
            Assert.True(PaletteColor.TryParseHex(hex, out _), $"{p.Id}.{hex} 不是合法颜色");
        }
    }

    [Fact]
    public void 窗口填充_保持低透明度磨砂区间()
    {
        foreach (var p in ThemeCatalog.BuiltIn)
        {
            var fill = PaletteColor.ParseHexOrThrow(p.WindowFill);
            Assert.True(fill.A is >= 0xE6 and <= 0xF5, $"{p.Id} WindowFill alpha={fill.A:X2} 超出磨砂区间");
        }
    }

    [Fact]
    public void 标题文字_对窗口填充对比度不低于45()
    {
        foreach (var p in ThemeCatalog.BuiltIn)
        {
            var title = PaletteColor.ParseHexOrThrow(p.TitleText);
            var fill = PaletteColor.ParseHexOrThrow(p.WindowFill);
            var ratio = ThemeColorMath.ContrastRatio(title, fill);
            Assert.True(ratio >= 4.5, $"{p.Id} 标题对比度 {ratio:F2} 不足 4.5");
        }
    }

    [Fact]
    public void 按钮文字_对强调色对比度不低于30()
    {
        foreach (var p in ThemeCatalog.BuiltIn)
        {
            var accent = PaletteColor.ParseHexOrThrow(p.Accent);
            var onAccent = PaletteColor.ParseHexOrThrow(p.OnAccent);
            var ratio = ThemeColorMath.ContrastRatio(onAccent, accent);
            Assert.True(ratio >= 3.0, $"{p.Id} OnAccent 对比度 {ratio:F2} 不足 3.0");
        }
    }

    [Fact]
    public void 卡片填充_比窗口填充更亮以形成层次()
    {
        foreach (var p in ThemeCatalog.BuiltIn)
        {
            if (p.Id == ThemeCatalog.SystemId) continue;
            var card = PaletteColor.ParseHexOrThrow(p.CardFill);
            var win = PaletteColor.ParseHexOrThrow(p.WindowFill);
            Assert.True(ThemeColorMath.Luminance(card) > ThemeColorMath.Luminance(win),
                $"{p.Id} 卡片亮度未高于窗口");
        }
    }

    [Fact]
    public void Find_未知id返回null_空值返回null()
    {
        Assert.Null(ThemeCatalog.Find("not-exist"));
        Assert.Null(ThemeCatalog.Find(null));
        Assert.Null(ThemeCatalog.Find(""));
        Assert.NotNull(ThemeCatalog.Find("orchid"));
    }

    [Fact]
    public void Resolve_未知id回退system()
    {
        var t = ThemeCatalog.Resolve("not-exist");
        Assert.Equal(ThemeCatalog.SystemId, t.Id);
    }

    [Fact]
    public void Resolve_Id不区分大小写()
    {
        Assert.Equal("orchid", ThemeCatalog.Resolve("ORCHID").Id);
    }

    [Fact]
    public void Resolve_system使用系统强调色()
    {
        var os = new PaletteColor(0xFF, 0xFF, 0x8C, 0x00);
        var t = ThemeCatalog.Resolve(ThemeCatalog.SystemId, os);
        Assert.Equal(os, t.Accent);
    }

    [Fact]
    public void Resolve_系统主题无强调色时用兜底色()
    {
        var t = ThemeCatalog.Resolve(ThemeCatalog.SystemId);
        Assert.Equal(PaletteColor.ParseHexOrThrow(ThemeCatalog.Find(ThemeCatalog.SystemId)!.Accent), t.Accent);
    }

    [Fact]
    public void Resolve_所有主题产出完整变体()
    {
        foreach (var p in ThemeCatalog.BuiltIn)
        {
            var t = ThemeCatalog.Resolve(p.Id);
            Assert.Equal(p.Id, t.Id);
            Assert.Equal(p.Name, t.Name);
            Assert.All(new[] { t.AccentLight1, t.AccentLight2, t.AccentLight3,
                               t.AccentDark1, t.AccentDark2, t.AccentDark3,
                               t.AccentFillSecondary, t.AccentFillTertiary },
                c => Assert.Equal(0xFF, c.A));
            Assert.Equal(0x5D, t.AccentFillDisabled.A);
        }
    }

    [Fact]
    public void Resolve_亮暗变体单调递变()
    {
        foreach (var p in ThemeCatalog.BuiltIn)
        {
            var t = ThemeCatalog.Resolve(p.Id);
            double lum(PaletteColor c) => ThemeColorMath.Luminance(c with { A = 0xFF });
            Assert.True(lum(t.AccentLight1) > lum(t.Accent), $"{p.Id} Light1 未变亮");
            Assert.True(lum(t.AccentLight2) > lum(t.AccentLight1), $"{p.Id} Light2 未变亮");
            Assert.True(lum(t.AccentLight3) > lum(t.AccentLight2), $"{p.Id} Light3 未变亮");
            Assert.True(lum(t.AccentDark1) < lum(t.Accent), $"{p.Id} Dark1 未变暗");
            Assert.True(lum(t.AccentDark2) < lum(t.AccentDark1), $"{p.Id} Dark2 未变暗");
            Assert.True(lum(t.AccentDark3) < lum(t.AccentDark2), $"{p.Id} Dark3 未变暗");
        }
    }

    [Fact]
    public void Hex解析_支持6位8位与非法输入()
    {
        Assert.True(PaletteColor.TryParseHex("#FF0000", out var c6));
        Assert.Equal((0xFF, 0xFF, 0x00, 0x00), (c6.A, c6.R, c6.G, c6.B));
        Assert.True(PaletteColor.TryParseHex("80FF0000", out var c8));
        Assert.Equal(0x80, c8.A);
        Assert.False(PaletteColor.TryParseHex("#12345", out _));
        Assert.False(PaletteColor.TryParseHex("#GGHHII", out _));
        Assert.False(PaletteColor.TryParseHex(null, out _));
        Assert.False(PaletteColor.TryParseHex("", out _));
        Assert.Throws<FormatException>(() => PaletteColor.ParseHexOrThrow("#XYZ"));
    }

    [Fact]
    public void Mix_端点与中点()
    {
        var black = new PaletteColor(0xFF, 0, 0, 0);
        var white = new PaletteColor(0xFF, 255, 255, 255);
        Assert.Equal(black, ThemeColorMath.Mix(black, white, 0));
        Assert.Equal(white, ThemeColorMath.Mix(black, white, 1));
        var mid = ThemeColorMath.Mix(black, white, 0.5);
        Assert.Equal(128, mid.R);
        Assert.Equal(255, ThemeColorMath.Mix(black, white, 1.7).R);
        Assert.Equal(0, ThemeColorMath.Mix(black, white, -0.7).R);
    }

    [Fact]
    public void Luminance_黑白端点()
    {
        var black = new PaletteColor(0xFF, 0, 0, 0);
        var white = new PaletteColor(0xFF, 255, 255, 255);
        Assert.Equal(0, ThemeColorMath.Luminance(black), 6);
        Assert.Equal(1, ThemeColorMath.Luminance(white), 6);
        Assert.Equal(21, ThemeColorMath.ContrastRatio(black, white), 4);
        Assert.Equal(1, ThemeColorMath.ContrastRatio(black, black), 4);
    }

    [Fact]
    public void LightenDarken_保持alpha且受01约束()
    {
        var c = new PaletteColor(0x80, 0x40, 0x80, 0xC0);
        Assert.Equal(0x80, ThemeColorMath.Lighten(c, 0.3).A);
        Assert.Equal(0x80, ThemeColorMath.Darken(c, 0.3).A);
        Assert.Equal(new PaletteColor(0x80, 255, 255, 255), ThemeColorMath.Lighten(c, 5));
        Assert.Equal(new PaletteColor(0x80, 0, 0, 0), ThemeColorMath.Darken(c, 5));
    }
}
