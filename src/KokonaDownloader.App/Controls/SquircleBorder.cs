using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace KokonaDownloader.App.Controls;

/// <summary>
/// 连续曲率圆角矩形（Squircle，超椭圆近似）。
/// WinUI3 的 Border.CornerRadius 只能画标准圆弧角（直线段与角弧之间曲率突变），
/// 做不到苹果那种直线-圆角过渡处曲率连续变化的大圆角。
/// 本控件用 n=4 超椭圆逐点采样生成 Path 几何来绘制填充与描边，
/// 用法与 Border 类似：Fill / Stroke / BorderThickness / CornerRadius，子元素照常排布。
/// </summary>
public sealed class SquircleBorder : Grid
{
    private const double Exponent = 4.0;
    private const int SamplesPerCorner = 14;
    private const double E = 2.0 / Exponent;

    private readonly Microsoft.UI.Xaml.Shapes.Path _path;

    public static new readonly DependencyProperty CornerRadiusProperty =
        DependencyProperty.Register(nameof(CornerRadius), typeof(CornerRadius), typeof(SquircleBorder),
            new PropertyMetadata(default(CornerRadius), OnGeometryPropertyChanged));

    public static readonly DependencyProperty FillProperty =
        DependencyProperty.Register(nameof(Fill), typeof(Brush), typeof(SquircleBorder),
            new PropertyMetadata(null, OnPaintPropertyChanged));

    public static readonly DependencyProperty StrokeProperty =
        DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(SquircleBorder),
            new PropertyMetadata(null, OnPaintPropertyChanged));

    public static new readonly DependencyProperty BorderThicknessProperty =
        DependencyProperty.Register(nameof(BorderThickness), typeof(Thickness), typeof(SquircleBorder),
            new PropertyMetadata(default(Thickness), OnGeometryPropertyChanged));

    /// <summary>四角半径。与 Control.CornerRadius 同名同类型，方便从模板直接 TemplateBinding 转发。</summary>
    public new CornerRadius CornerRadius
    {
        get => (CornerRadius)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    public Brush Fill
    {
        get => (Brush)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public new Thickness BorderThickness
    {
        get => (Thickness)GetValue(BorderThicknessProperty);
        set => SetValue(BorderThicknessProperty, value);
    }

    public SquircleBorder()
    {
        _path = new Microsoft.UI.Xaml.Shapes.Path { IsHitTestVisible = false };
        Children.Add(_path);
        SizeChanged += (_, _) => RebuildGeometry();
    }

    private static void OnGeometryPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((SquircleBorder)d).RebuildGeometry();

    private static void OnPaintPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var self = (SquircleBorder)d;
        self._path.Fill = self.Fill;
        self._path.StrokeThickness = self.MaxStroke();
        self._path.Stroke = self.MaxStroke() > 0 ? self.Stroke : null;
    }

    private double MaxStroke()
    {
        Thickness t = BorderThickness;
        return Math.Max(Math.Max(t.Left, t.Right), Math.Max(t.Top, t.Bottom));
    }

    private void RebuildGeometry()
    {
        _path.Fill = Fill;
        double stroke = MaxStroke();
        _path.StrokeThickness = stroke;
        _path.Stroke = stroke > 0 ? Stroke : null;

        double w = ActualWidth;
        double h = ActualHeight;
        if (w <= 0 || h <= 0)
        {
            _path.Data = null;
            return;
        }

        double inset = stroke / 2;
        double rw = w - stroke;
        double rh = h - stroke;
        if (rw <= 0 || rh <= 0)
        {
            _path.Data = null;
            return;
        }

        double x = inset;
        double y = inset;
        double maxR = Math.Min(rw, rh) / 2;
        CornerRadius r = CornerRadius;

        var pts = new List<Point>(SamplesPerCorner * 4);
        double rtl = Clamp(r.TopLeft, maxR);
        double rtr = Clamp(r.TopRight, maxR);
        double rbr = Clamp(r.BottomRight, maxR);
        double rbl = Clamp(r.BottomLeft, maxR);

        // 顺时针一周：左上 -> 右上 -> 右下 -> 左下，相邻角之间自然形成矩形的直边
        AddCorner(pts, x + rtl, y + rtl, rtl, Math.PI, 1.5 * Math.PI);
        AddCorner(pts, x + rw - rtr, y + rtr, rtr, 1.5 * Math.PI, 2 * Math.PI);
        AddCorner(pts, x + rw - rbr, y + rh - rbr, rbr, 0, 0.5 * Math.PI);
        AddCorner(pts, x + rbl, y + rh - rbl, rbl, 0.5 * Math.PI, Math.PI);

        if (pts.Count < 2)
        {
            _path.Data = null;
            return;
        }

        var figure = new PathFigure { StartPoint = pts[0], IsClosed = true };
        var segment = new PolyLineSegment();
        for (int i = 1; i < pts.Count; i++)
        {
            segment.Points.Add(pts[i]);
        }
        figure.Segments.Add(segment);
        var geo = new PathGeometry();
        geo.Figures.Add(figure);
        _path.Data = geo;
    }

    private static double Clamp(double v, double max) => v <= 0 ? 0 : Math.Min(v, max);

    /// <summary>按超椭圆参数方程采样一个角（角度区间含端点）。r=0 时退化为角点一个点。</summary>
    private static void AddCorner(List<Point> pts, double cx, double cy, double rr, double a0, double a1)
    {
        if (rr <= 0)
        {
            pts.Add(new Point(cx, cy));
            return;
        }

        for (int i = 0; i < SamplesPerCorner; i++)
        {
            double a = a0 + (a1 - a0) * i / (SamplesPerCorner - 1);
            double c = Math.Cos(a);
            double s = Math.Sin(a);
            pts.Add(new Point(
                cx + rr * Math.Sign(c) * Math.Pow(Math.Abs(c), E),
                cy + rr * Math.Sign(s) * Math.Pow(Math.Abs(s), E)));
        }
    }
}
