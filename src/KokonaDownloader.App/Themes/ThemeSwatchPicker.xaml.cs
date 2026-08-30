using System.Collections.ObjectModel;
using KokonaDownloader.Core.Themes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.UI.ViewManagement;

namespace KokonaDownloader.App.Themes;

/// <summary>主题色卡选择器：点击即时切换（ThemeService.SetThemeColor → 全窗口刷新）。</summary>
public sealed partial class ThemeSwatchPicker : UserControl
{
    private readonly ObservableCollection<ThemeSwatchViewModel> _items = new();

    public ThemeSwatchPicker()
    {
        InitializeComponent();
        var osAccent = GetOsAccent();
        foreach (var p in ThemeCatalog.BuiltIn)
        {
            // "跟随系统" 色卡显示当前系统强调色；取不到则退回内置兜底色
            var accent = p.Id == ThemeCatalog.SystemId && osAccent is { } os
                ? os
                : PaletteColor.ParseHexOrThrow(p.Accent);
            _items.Add(new ThemeSwatchViewModel(p, accent, PaletteColor.ParseHexOrThrow(p.OnAccent)));
        }
        Items = _items;
        RefreshChecked();
        ThemeService.ThemeChanged += OnThemeChanged;
        Unloaded += (_, _) => ThemeService.ThemeChanged -= OnThemeChanged;
    }

    public ObservableCollection<ThemeSwatchViewModel> Items { get; }

    private void OnThemeChanged() => DispatcherQueue.TryEnqueue(RefreshChecked);

    private void RefreshChecked()
    {
        var currentId = ThemeService.Current.Id;
        foreach (var it in _items)
            it.IsChecked = string.Equals(it.Id, currentId, StringComparison.OrdinalIgnoreCase);
    }

    private void OnSwatchClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ThemeSwatchViewModel vm) return;
        if (string.Equals(vm.Id, ThemeService.Current.Id, StringComparison.OrdinalIgnoreCase))
        {
            vm.IsChecked = true; // 点击已选中的色卡：恢复选中态即可
            return;
        }
        ThemeService.SetThemeColor(vm.Id);
    }

    private static PaletteColor? GetOsAccent()
    {
        try
        {
            var c = new UISettings().GetColorValue(UIColorType.Accent);
            return new PaletteColor(c.A, c.R, c.G, c.B);
        }
        catch { return null; }
    }
}
