using System.ComponentModel;
using System.Runtime.CompilerServices;
using KokonaDownloader.Core.Themes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace KokonaDownloader.App.Themes;

/// <summary>主题色卡条目（选中态由 ThemeService.ThemeChanged 驱动刷新）。</summary>
public sealed class ThemeSwatchViewModel : INotifyPropertyChanged
{
    private static readonly SolidColorBrush TransparentBrush = new(Microsoft.UI.Colors.Transparent);

    private readonly SolidColorBrush _ringOn;
    private bool _isChecked;

    public ThemeSwatchViewModel(ThemePalette palette, PaletteColor accent, PaletteColor glyph)
    {
        Id = palette.Id;
        Name = palette.Name;
        SwatchBrush = new SolidColorBrush(ThemeService.ToColor(accent));
        CheckGlyphBrush = new SolidColorBrush(ThemeService.ToColor(glyph));
        _ringOn = new SolidColorBrush(ThemeService.ToColor(accent));
        RingBrush = TransparentBrush;
    }

    public string Id { get; }
    public string Name { get; }
    public SolidColorBrush SwatchBrush { get; }
    public SolidColorBrush CheckGlyphBrush { get; }
    public SolidColorBrush RingBrush { get; private set; }

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value) return;
            _isChecked = value;
            RingBrush = _isChecked ? _ringOn : TransparentBrush;
            OnPropertyChanged();
            OnPropertyChanged(nameof(RingBrush));
            OnPropertyChanged(nameof(CheckVisibility));
        }
    }

    public Visibility CheckVisibility => _isChecked ? Visibility.Visible : Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name!));
}
