using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace KokonaDownloader.App.Converters;

/// <summary>WinUI 3 没有内置 BooleanToVisibilityConverter，这里自定义实现。</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var b = value is bool x && x;
        // parameter="invert" 时反转
        if (parameter is string s && s.Equals("invert", StringComparison.OrdinalIgnoreCase)) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => value is Visibility.Visible;
}
