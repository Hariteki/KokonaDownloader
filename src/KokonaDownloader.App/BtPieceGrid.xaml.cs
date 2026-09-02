using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using KokonaDownloader.App.Themes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace KokonaDownloader.App;

/// <summary>单个分片块的可渲染状态：0=未完成 1=部分完成 2=完成；Side 为方块边长。</summary>
public sealed class PieceVm : INotifyPropertyChanged
{
    private int _state;
    public int State
    {
        get => _state;
        set { if (_state != value) { _state = value; OnPropertyChanged(); } }
    }

    private double _side = 12;
    /// <summary>方块边长；紧凑模式下随控件宽度动态调整，需 OneWay 通知。</summary>
    public double Side
    {
        get => _side;
        set { if (Math.Abs(_side - value) > 0.01) { _side = value; OnPropertyChanged(); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Motrix 风格分片方块矩阵：每个小方块代表一个（或一组）piece，
/// 完成块显示为主题强调色实色，进行中显示为半透明强调色，未完成为半透明黑色。
/// 数据来源为 aria2 的 bitfield（hex，最高位对应 piece 0）。
/// 超过块数上限时自动把相邻 piece 聚合为一个块，控制元素数量上限；
/// 紧凑模式（Compact=true）用于任务卡片内的单行进度条形态。
/// </summary>
public sealed partial class BtPieceGrid : UserControl
{
    private const int MaxBlocks = 400;
    private const double FullSide = 12, FullGap = 3, CompactGap = 2;
    private const double CompactMinSide = 5, CompactMaxSide = 12;

    private readonly ObservableCollection<PieceVm> _pieces = new();
    private int _blocks;
    private int _per;
    private double _lastSide = -1;
    private double _lastCompactWidth = -1;
    private int _lastBrushVersion = -1;

    public static readonly DependencyProperty BitFieldProperty =
        DependencyProperty.Register(nameof(BitField), typeof(string), typeof(BtPieceGrid),
            new PropertyMetadata(null, OnDataChanged));
    /// <summary>aria2 返回的分片位图（hex 字符串）。</summary>
    public string? BitField
    {
        get => (string?)GetValue(BitFieldProperty);
        set => SetValue(BitFieldProperty, value);
    }

    public static readonly DependencyProperty NumPiecesProperty =
        DependencyProperty.Register(nameof(NumPieces), typeof(long), typeof(BtPieceGrid),
            new PropertyMetadata(0L, OnDataChanged));
    /// <summary>分片总数（0 = 元数据未就绪）。</summary>
    public long NumPieces
    {
        get => (long)GetValue(NumPiecesProperty);
        set => SetValue(NumPiecesProperty, value);
    }

    public static readonly DependencyProperty IsActiveProperty =
        DependencyProperty.Register(nameof(IsActive), typeof(bool), typeof(BtPieceGrid),
            new PropertyMetadata(false, OnDataChanged));
    /// <summary>任务是否处于活动状态（活动时块矩阵才有"部分完成"态）。</summary>
    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public static readonly DependencyProperty MetaVisibleProperty =
        DependencyProperty.Register(nameof(MetaVisible), typeof(bool), typeof(BtPieceGrid),
            new PropertyMetadata(true, OnDataChanged));
    /// <summary>是否显示"n 分片 · 完成率"统计文本。</summary>
    public bool MetaVisible
    {
        get => (bool)GetValue(MetaVisibleProperty);
        set => SetValue(MetaVisibleProperty, value);
    }

    public static readonly DependencyProperty CompactProperty =
        DependencyProperty.Register(nameof(Compact), typeof(bool), typeof(BtPieceGrid),
            new PropertyMetadata(false, OnDataChanged));
    /// <summary>紧凑模式：方块宽度铺满控件、按可用宽度动态排布（可多行）；完整模式 12px、最多 400 块。</summary>
    public bool Compact
    {
        get => (bool)GetValue(CompactProperty);
        set => SetValue(CompactProperty, value);
    }

    public BtPieceGrid()
    {
        InitializeComponent();
        // 深浅色翻转由 ActualThemeChanged 触发；强调色单独变化（深浅色不变）时它不触发，
        // 需订阅 ThemeService.ThemeChanged 补上刷新（卸载时退订，避免静态事件持有实例）
        ActualThemeChanged += (_, _) => Refresh();
        ThemeService.ThemeChanged += OnThemeServiceChanged;
        Unloaded += (_, _) => ThemeService.ThemeChanged -= OnThemeServiceChanged;
        // 紧凑模式的排布依赖控件实际宽度，列表列宽变化时重新计算
        SizeChanged += (_, _) =>
        {
            if (Compact && NumPieces > 0 && Math.Abs(ActualWidth - _lastCompactWidth) > 0.5) Refresh();
        };
    }

    private void OnThemeServiceChanged() => DispatcherQueue.TryEnqueue(Refresh);

    private static void OnDataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((BtPieceGrid)d).Refresh();

    private void Refresh()
    {
        // 深浅色环境同步给转换器（本项目各窗口固定深色，此处按控件实际主题兜底）；
        // 立即求值画刷缓存，版本变化说明画刷刚重建，需强制重估所有块的绑定
        PieceStateToBrushConverter.Dark = ActualTheme == ElementTheme.Dark;
        var brushVersion = PieceStateToBrushConverter.EnsureBrushes();
        var brushesChanged = brushVersion != _lastBrushVersion;
        _lastBrushVersion = brushVersion;

        var numPieces = NumPieces > int.MaxValue ? int.MaxValue : (int)NumPieces;
        var active = IsActive;

        double side, gap;
        int blocks;
        if (Compact)
        {
            // 紧凑模式：方块总宽严格铺满控件（与进度条同宽）。
            // UniformGridLayout 列数 = floor((可用宽 + 列间距) / (方块宽 + 列间距))，
            // 先按最小方块算出可容纳的最大列数，方块数上限两排，再反解精确方块边长。
            var w = ActualWidth > 40 ? ActualWidth : 720;
            _lastCompactWidth = ActualWidth;
            var maxCols = Math.Max((int)Math.Floor((w + CompactGap) / (CompactMinSide + CompactGap)), 1);
            blocks = Math.Min(numPieces, maxCols * 2);
            var cols = blocks <= maxCols ? Math.Max(blocks, 1) : (blocks + 1) / 2;
            // 向下取整到 0.01px，保证 cols 个方块 + 间距不超出可用宽（防浮点误差多换一排）
            side = Math.Floor(((w - (cols - 1) * CompactGap) / cols) * 100) / 100;
            gap = CompactGap;
            if (side > CompactMaxSide)
            {
                // 方块较少时：边长封顶后按比例拉开间距补足宽度
                side = CompactMaxSide;
                gap = Math.Clamp((w - cols * side) / Math.Max(cols - 1, 1), CompactGap, 10);
                while (gap > CompactGap && Math.Floor((w + gap) / (side + gap)) < cols) gap -= 0.1;
            }
        }
        else
        {
            side = FullSide;
            gap = FullGap;
            blocks = Math.Min(numPieces, MaxBlocks);
        }
        if (Repeater.Layout is UniformGridLayout layout)
        {
            layout.MinItemWidth = side;
            layout.MinItemHeight = side;
            layout.MinRowSpacing = gap;
            layout.MinColumnSpacing = gap;
        }

        if (numPieces <= 0)
        {
            Repeater.ItemsSource = null;
            if (_pieces.Count > 0) _pieces.Clear();
            _blocks = 0;
            _per = 0;
            MetaText.Text = active ? "正在获取种子元数据…" : "尚未获取到分片信息";
            MetaText.Visibility = MetaVisible ? Visibility.Visible : Visibility.Collapsed;
            Repeater.Visibility = Visibility.Collapsed;
            return;
        }

        var per = (numPieces + blocks - 1) / blocks;
        if (blocks != _blocks || per != _per || _pieces.Count != blocks)
        {
            _blocks = blocks;
            _per = per;
            _lastSide = side;
            _pieces.Clear();
            for (var i = 0; i < blocks; i++) _pieces.Add(new PieceVm { Side = side });
            Repeater.ItemsSource = _pieces;
            Repeater.Visibility = Visibility.Visible;
        }
        else if (Math.Abs(side - _lastSide) > 0.01)
        {
            // 仅方块边长变化（列表宽度调整）：原地通知即可，无需重建集合
            _lastSide = side;
            foreach (var p in _pieces) p.Side = side;
        }

        var bits = ParseBits(BitField, numPieces);
        var doneTotal = 0;
        for (var b = 0; b < blocks; b++)
        {
            var start = b * per;
            var end = Math.Min(start + per, numPieces);
            var done = 0;
            for (var p = start; p < end; p++)
                if (bits[p]) done++;
            doneTotal += done;

            var state = done == end - start ? 2 : done > 0 ? 1 : 0;
            if (!active && state == 1) state = 0; // 非活动任务不显示进行中态
            var vm = _pieces[b];
            // 画刷刚重建时，同值 State 不会触发 PropertyChanged，先用哨兵值强制绑定重估
            if (brushesChanged) vm.State = -1;
            vm.State = state;
        }

        MetaText.Text = $"{numPieces} 分片 · 已完成 {(double)doneTotal * 100 / numPieces:0.#}%";
        MetaText.Visibility = MetaVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>解析 aria2 bitfield：hex，最高有效位对应 piece 0，依此向后。</summary>
    private static bool[] ParseBits(string? hex, int count)
    {
        var bits = new bool[count];
        if (string.IsNullOrWhiteSpace(hex)) return bits;
        var bytes = Math.Min((count + 7) / 8, hex.Length / 2);
        for (var i = 0; i < bytes; i++)
        {
            var hi = HexVal(hex[i * 2]);
            var lo = i * 2 + 1 < hex.Length ? HexVal(hex[i * 2 + 1]) : 0;
            if (hi < 0 || lo < 0) continue;
            var value = (hi << 4) | lo;
            for (var bit = 0; bit < 8; bit++)
            {
                var piece = i * 8 + bit;
                if (piece < count && (value & (0x80 >> bit)) != 0) bits[piece] = true;
            }
        }
        return bits;
    }

    private static int HexVal(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1
    };
}

/// <summary>
/// 分片状态 → 画刷。使用应用当前主题强调色（ThemeService，含自定义色板），
/// 主题/强调色变化时由 BtPieceGrid 的刷新（ActualThemeChanged / ThemeChanged）触发缓存比对自动重建。
/// </summary>
public sealed class PieceStateToBrushConverter : IValueConverter
{
    private static Brush? _done;
    private static Brush? _partial;
    private static Brush? _empty;
    private static bool _dark = true;
    private static Color _accent;
    private static int _version;

    /// <summary>当前深浅色环境（由 BtPieceGrid 按控件 ActualTheme 同步）。</summary>
    public static bool Dark { get; set; } = true;

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        EnsureBrushes();
        return (value as int?) switch
        {
            2 => _done!,
            1 => _partial!,
            _ => _empty!
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();

    /// <summary>确保静态画刷与当前深浅色/强调色一致；每次重建版本号 +1 并返回。</summary>
    internal static int EnsureBrushes()
    {
        Color accent;
        try
        {
            accent = ThemeService.ToColor(ThemeService.Current.Accent);
        }
        catch
        {
            try
            {
                accent = new global::Windows.UI.ViewManagement.UISettings()
                    .GetColorValue(global::Windows.UI.ViewManagement.UIColorType.Accent);
            }
            catch
            {
                accent = Color.FromArgb(255, 0, 120, 212);
            }
        }
        if (_done != null && _dark == Dark && _accent == accent) return _version;

        _dark = Dark;
        _accent = accent;
        _done = new SolidColorBrush(accent);
        _partial = new SolidColorBrush(Color.FromArgb(0x5A, accent.R, accent.G, accent.B));
        _empty = new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0));
        return ++_version;
    }
}
