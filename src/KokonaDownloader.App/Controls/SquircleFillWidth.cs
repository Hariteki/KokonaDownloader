using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace KokonaDownloader.App.Controls;

/// <summary>
/// ProgressBar 填充宽度附加属性：Value / Maximum × 轨道（Track）实际宽度自驱填充 Width。
/// App.xaml 的 squircle 进度条模板里，框架对 PART_Indicator 的内置测宽机制
/// （依赖特定类型强转 / MatrixTransform）在 SquircleBorder 上失效，导致填充恒为 0 宽；
/// 且 WinUI 3 未提供 MultiBinding，无法用多值绑定换算宽度，
/// 因此改由本附加属性监听三个输入源并在代码中统一计算宽度。
/// </summary>
public static class SquircleFillWidth
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.RegisterAttached(
        "Value", typeof(double), typeof(SquircleFillWidth), new PropertyMetadata(0.0, OnValueChanged));

    public static double GetValue(DependencyObject obj) => (double)obj.GetValue(ValueProperty);

    public static void SetValue(DependencyObject obj, double value) => obj.SetValue(ValueProperty, value);

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.RegisterAttached(
        "Maximum", typeof(double), typeof(SquircleFillWidth), new PropertyMetadata(100.0, OnMaximumChanged));

    public static double GetMaximum(DependencyObject obj) => (double)obj.GetValue(MaximumProperty);

    public static void SetMaximum(DependencyObject obj, double value) => obj.SetValue(MaximumProperty, value);

    public static readonly DependencyProperty TrackProperty = DependencyProperty.RegisterAttached(
        "Track", typeof(FrameworkElement), typeof(SquircleFillWidth), new PropertyMetadata(null, OnTrackChanged));

    public static FrameworkElement GetTrack(DependencyObject obj) => (FrameworkElement)obj.GetValue(TrackProperty);

    public static void SetTrack(DependencyObject obj, FrameworkElement value) => obj.SetValue(TrackProperty, value);

    private sealed class State
    {
        public FrameworkElement? SubscribedTrack;
        public SizeChangedEventHandler? Handler;
    }

    private static readonly ConditionalWeakTable<DependencyObject, State> States = new();

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => Update(d);

    private static void OnMaximumChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => Update(d);

    private static void OnTrackChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var state = States.GetValue(d, _ => new State());
        if (state.SubscribedTrack is { } oldTrack && state.Handler is { } oldHandler)
        {
            oldTrack.SizeChanged -= oldHandler;
        }

        if (e.NewValue is FrameworkElement newTrack)
        {
            state.Handler = (_, _) => Update(d);
            newTrack.SizeChanged += state.Handler;
            state.SubscribedTrack = newTrack;
        }
        else
        {
            state.Handler = null;
            state.SubscribedTrack = null;
        }

        Update(d);
    }

    private static void Update(DependencyObject d)
    {
        if (d is not FrameworkElement element)
        {
            return;
        }

        var maximum = GetMaximum(element);
        var track = GetTrack(element);
        if (maximum > 0 && track is { ActualWidth: > 0 })
        {
            element.Width = Math.Clamp(GetValue(element) / maximum, 0, 1) * track.ActualWidth;
        }
        else
        {
            element.Width = 0;
        }
    }
}
