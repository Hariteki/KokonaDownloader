using System.Runtime.CompilerServices;
using KokonaDownloader.Core.Settings;
using KokonaDownloader.Core.Themes;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace KokonaDownloader.App.Themes;

/// <summary>
/// 主题服务：把 ThemeCatalog 解析出的主题注入应用级资源字典（覆盖 WinUI 标准键，
/// 所有控件经 ThemeResource 引用自动联动），并为已注册窗口着色原生标题栏。
/// 切换 = SettingsStore.Update(ThemeColorId) → Changed → Apply()，全窗口实时刷新。
/// </summary>
public static class ThemeService
{
    /// <summary>进度窗 Acrylic 上的低透明主题色罩（保留磨砂质感同时染上主题色）。</summary>
    public const string AcrylicTintBrushKey = "AppAcrylicTintBrush";

    /// <summary>主题应用完成后的通知（UI 可订阅以刷新选中态等）。</summary>
    public static event Action? ThemeChanged;

    private static readonly List<WeakReference<Window>> Windows = new();

    /// <summary>每个窗口当前挂载的原生背景控制器（切换透明度模式时需要释放）。</summary>
    private static readonly Dictionary<Window, ISystemBackdropControllerWithTargets> _backdrops = new();
    private static ResourceDictionary? _overrides;
    private static UISettings? _uiSettings;
    /// <summary>UI 线程的 DispatcherQueue（Initialize 时捕获，供跨线程设置变更调度回 UI 线程）。</summary>
    private static Microsoft.UI.Dispatching.DispatcherQueue? _uiQueue;

    public static ResolvedTheme Current { get; private set; } =
        ThemeCatalog.Resolve(ThemeCatalog.SystemId);

    public static void Initialize()
    {
        // 捕获 UI 线程调度队列（Initialize 在 OnLaunched 中于 UI 线程调用）
        try { _uiQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread(); }
        catch (Exception ex) { App.Log($"捕获 UI 调度队列失败: {ex.Message}"); }

        _overrides = new ResourceDictionary();
        Application.Current.Resources.MergedDictionaries.Add(_overrides);

        if (App.Host?.Settings != null)
            App.Host.Settings.Changed += OnSettingsChanged;

        // 系统强调色变化时（"跟随系统"主题）重新解析
        try
        {
            _uiSettings = new UISettings();
            _uiSettings.ColorValuesChanged += (_, _) =>
            {
                App.MainWin?.DispatcherQueue.TryEnqueue(Apply);
            };
        }
        catch (Exception ex) { App.Log($"初始化 UISettings 失败: {ex.Message}"); }

        Apply();
    }

    private static void OnSettingsChanged(object? sender, EventArgs e)
    {
        // 设置可能来自 API 线程（POST /api/settings）：资源字典/窗口刷新必须在 UI 线程执行，
        // 否则跨线程访问 XAML 会抛异常 → API 返回 500。UI 线程上保持同步刷新（点击即时生效）。
        var dq = _uiQueue ?? App.MainWin?.DispatcherQueue;
        if (dq != null && !dq.HasThreadAccess)
            dq.TryEnqueue(Apply);
        else
            Apply();
    }

    /// <summary>切换主题配色（写设置 → 持久化 → Changed → 全窗口刷新）。</summary>
    public static void SetThemeColor(string id) =>
        App.Host?.Settings.Update(s =>
        {
            if (string.Equals(s.ThemeColorId, id, StringComparison.OrdinalIgnoreCase)) return false;
            s.ThemeColorId = id;
            return true;
        });

    /// <summary>切换窗口透明度模式（写设置 → 持久化 → Changed → 全窗口刷新）。</summary>
    public static void SetTransparencyMode(TransparencyMode mode) =>
        App.Host?.Settings.Update(s =>
        {
            if (s.Transparency == mode) return false;
            s.Transparency = mode;
            return true;
        });

    /// <summary>当前透明度模式。</summary>
    public static TransparencyMode CurrentTransparency =>
        App.Host?.Settings.Current.Transparency ?? TransparencyMode.Opaque;

    public static void Register(Window window)
    {
        PruneWindows();
        Windows.Add(new WeakReference<Window>(window));
        ApplyCaption(window);
        ApplyTransparency(window);
    }

    public static void Unregister(Window window)
    {
        for (var i = Windows.Count - 1; i >= 0; i--)
            if (!Windows[i].TryGetTarget(out var w) || ReferenceEquals(w, window))
                Windows.RemoveAt(i);
    }

    private static void PruneWindows()
    {
        for (var i = Windows.Count - 1; i >= 0; i--)
            if (!Windows[i].TryGetTarget(out _)) Windows.RemoveAt(i);
    }

    private static IEnumerable<Window> AllWindows()
    {
        PruneWindows();
        foreach (var w in Windows)
            if (w.TryGetTarget(out var win)) yield return win;
    }

    public static void Apply()
    {
        var id = App.Host?.Settings.Current.ThemeColorId;
        Current = ThemeCatalog.Resolve(id, GetOsAccent());
        RebuildOverrides();
        foreach (var w in AllWindows())
        {
            ApplyCaption(w);
            ApplyTransparency(w);
            ForceThemeResourceRefresh(w);
        }
        ThemeChanged?.Invoke();
    }

    /// <summary>
    /// 按当前透明度模式设置窗口背景与原生 Acrylic 背景：
    ///  Opaque           → 无背景层 + 实心主题背景（完全不透明）
    ///  Frosted          → 半透明主题色 + Acrylic 磨砂（能透出后面的桌面/窗口，带模糊）
    ///  BlackTransparent → 黑色薄磨砂（最低不透明度，几乎全透明）
    ///
    /// 实现说明：XAML 的 MicaBackdrop/DesktopAcrylicBackdrop 在本机实测"设了但不透桌面"
    /// （采样点亮度恒为常数、与窗口后面的内容无关），因此改用本项目进度窗已验证生效的
    /// 原生 DesktopAcrylicController，并在切换模式时释放上一个控制器。
    /// </summary>
    public static void ApplyTransparency(Window window)
    {
        try
        {
            var mode = CurrentTransparency;
            var t = Current;

            // 先释放上一个原生背景，避免多个控制器叠加
            ReleaseBackdrop(window);

            var root = window.Content as Grid;

            switch (mode)
            {
                case TransparencyMode.Opaque:
                    window.SystemBackdrop = null;
                    // 强制 A=0xFF：主题 WindowFill 默认 alpha 0xF2（95%），"不透明"必须绝对实心
                    if (root != null) root.Background = Solid(Opaque(t.WindowFill));
                    break;

                case TransparencyMode.Frosted:
                    // 主题色作为染色层，磨砂明显：既保留主题色又透出背景（实测随背景亮度变化）
                    var controller = WindowEffects.TryApplyAcrylicTinted(
                        window, ToColor(t.WindowFill), tintOpacity: 0.70, luminosityOpacity: 0.55, thin: false);
                    if (controller != null) _backdrops[window] = controller;
                    // 根元素透明：染色交给 Acrylic 控制器，避免二次叠加变实
                    if (root != null) root.Background = new SolidColorBrush(Colors.Transparent);
                    break;

                case TransparencyMode.BlackTransparent:
                    // 黑色薄磨砂 + 极低染色：接近"纯透明"的黑色，桌面几乎原样透出
                    var blackController = WindowEffects.TryApplyAcrylicTinted(
                        window, Colors.Black, tintOpacity: 0.18, luminosityOpacity: 0.05, thin: true);
                    if (blackController != null) _backdrops[window] = blackController;
                    if (root != null) root.Background = new SolidColorBrush(Colors.Transparent);
                    break;
            }

            // 诊断：确认实际生效的 backdrop 类型与平台支持情况（排查透明不生效时看这里）
            var rootBg = (root?.Background as SolidColorBrush)?.Color;
            App.Log($"透明度模式={mode} 原生控制器={(window.SystemBackdrop == null && _backdrops.ContainsKey(window) ? "已挂载" : "无")} " +
                    $"mica支持={MicaController.IsSupported()} acrylic支持={DesktopAcrylicController.IsSupported()} " +
                    $"根背景={rootBg?.ToString() ?? "n/a"}");
        }
        catch (Exception ex) { App.Log($"应用透明度模式失败: {ex.Message}"); }
    }

    /// <summary>释放窗口上已挂载的原生背景控制器（切模式 / 关窗时调用）。</summary>
    private static void ReleaseBackdrop(Window window)
    {
        if (_backdrops.TryGetValue(window, out var controller))
        {
            _backdrops.Remove(window);
            try { controller.Dispose(); }
            catch (Exception ex) { App.Log($"释放背景控制器失败: {ex.Message}"); }
        }
        window.SystemBackdrop = null;
    }

    /// <summary>
    /// WinUI 平台限制：修改已合并字典的键值不会让已渲染元素重新求值 ThemeResource，
    /// 只有"元素生效主题变化"才触发全量重取。这里把根元素主题同步切换
    /// Dark→Light→Dark（同一调度周期内完成、不渲染中间帧，无可见闪烁），
    /// 强制整棵可视树用新调色板重绘。
    /// </summary>
    private static void ForceThemeResourceRefresh(Window window)
    {
        try
        {
            if (window.Content is FrameworkElement root)
            {
                root.RequestedTheme = ElementTheme.Light;
                root.RequestedTheme = ElementTheme.Dark;
            }
            // 已打开的弹层（ContentDialog/MenuFlyout 等）挂在独立 Popup 分支，单独刷新
            foreach (var popup in VisualTreeHelper.GetOpenPopups(window))
                if (popup.Child is FrameworkElement pe)
                {
                    pe.RequestedTheme = ElementTheme.Light;
                    pe.RequestedTheme = ElementTheme.Dark;
                }
        }
        catch (Exception ex) { App.Log($"刷新主题资源失败: {ex.Message}"); }
    }

    public static void ApplyCaption(Window window)
    {
        // 原生标题栏：底色用 LayerFill（与顶部工具栏同层），文字用主题标题色
        WindowEffects.SetCaptionColor(window, ToColor(Opaque(Current.LayerFill)));
        WindowEffects.SetCaptionTextColor(window, ToColor(Current.TitleText));
    }

    public static PaletteColor? GetOsAccent()
    {
        try
        {
            _uiSettings ??= new UISettings();
            var c = _uiSettings.GetColorValue(UIColorType.Accent);
            return new PaletteColor(c.A, c.R, c.G, c.B);
        }
        catch { return null; }
    }

    private static PaletteColor Opaque(PaletteColor c) => c with { A = 0xFF };

    public static Color ToColor(PaletteColor c) => Color.FromArgb(c.A, c.R, c.G, c.B);

    private static Brush Solid(PaletteColor c) => new SolidColorBrush(ToColor(c));

    /// <summary>重建覆盖字典的键值。注意：仅改字典不会让已渲染元素重取 ThemeResource，
    /// 需配合 ForceThemeResourceRefresh 的主题切换触发全量重求值。</summary>
    private static void RebuildOverrides()
    {
        if (_overrides == null) return;
        var t = Current;
        var card2 = ThemeColorMath.Mix(t.CardFill, PaletteColor.ParseHexOrThrow("#FFFFFFFF"), 0.04);
        var card3 = ThemeColorMath.Mix(t.CardFill, PaletteColor.ParseHexOrThrow("#FFFFFFFF"), 0.08);
        var accentHover = t.AccentFillSecondary;
        var accentPressed = t.AccentFillTertiary;
        var accentDisabled = t.AccentFillDisabled;
        var white = PaletteColor.ParseHexOrThrow("#FFFFFFFF");

        _overrides.Clear();

        // ---- SystemAccentColor 系列（列表选中/复选框/单选框/进度条等内部引用）----
        _overrides["SystemAccentColor"] = ToColor(t.Accent);
        _overrides["SystemAccentColorLight1"] = ToColor(t.AccentLight1);
        _overrides["SystemAccentColorLight2"] = ToColor(t.AccentLight2);
        _overrides["SystemAccentColorLight3"] = ToColor(t.AccentLight3);
        _overrides["SystemAccentColorDark1"] = ToColor(t.AccentDark1);
        _overrides["SystemAccentColorDark2"] = ToColor(t.AccentDark2);
        _overrides["SystemAccentColorDark3"] = ToColor(t.AccentDark3);

        // ---- 强调色填充/文字/边框 ----
        _overrides["AccentFillColorDefaultBrush"] = Solid(t.Accent);
        _overrides["AccentFillColorSecondaryBrush"] = Solid(accentHover);
        _overrides["AccentFillColorTertiaryBrush"] = Solid(accentPressed);
        _overrides["AccentFillColorDisabledBrush"] = Solid(accentDisabled);
        _overrides["AccentTextFillColorPrimaryBrush"] = Solid(t.AccentLight2);
        _overrides["AccentTextFillColorSecondaryBrush"] = Solid(t.AccentLight1);
        _overrides["AccentTextFillColorTertiaryBrush"] = Solid(t.AccentLight1);
        _overrides["AccentTextFillColorDisabledBrush"] = Solid(t.TitleText with { A = 0x5D });
        // Tab 栏动画用的 Color 类型键（Brush 无法直接用于 ColorAnimation）
        _overrides["TabAccentColor"] = ToColor(t.AccentLight2);
        _overrides["TabInactiveColor"] = ToColor(t.TitleText with { A = 0x9E });
        // Tab/BT 按钮未激活填充与 hover/press 反馈色：基于卡片底色按白混入梯度，深浅主题均有区分度
        _overrides["TabSurfaceColor"] = ToColor(ThemeColorMath.Mix(t.CardFill, white, 0.06));
        _overrides["TabSurfaceHoverColor"] = ToColor(ThemeColorMath.Mix(t.CardFill, white, 0.11));
        _overrides["TabSurfacePressedColor"] = ToColor(ThemeColorMath.Mix(t.CardFill, white, 0.16));
        _overrides["AccentAAFillColorDefaultBrush"] = Solid(t.Accent);
        _overrides["AccentAAFillColorDisabledBrush"] = Solid(accentDisabled);
        _overrides["AccentControlElevationBorderBrush"] = Solid(accentHover);
        _overrides["AccentControlElevationBorderBrushPointerOver"] = Solid(accentPressed);

        // ---- 强调色按钮四态 ----
        _overrides["AccentButtonBackground"] = Solid(t.Accent);
        _overrides["AccentButtonBackgroundPointerOver"] = Solid(accentHover);
        _overrides["AccentButtonBackgroundPressed"] = Solid(accentPressed);
        _overrides["AccentButtonBackgroundDisabled"] = Solid(white with { A = 0x0B });
        _overrides["AccentButtonForeground"] = Solid(t.OnAccent);
        _overrides["AccentButtonForegroundPointerOver"] = Solid(t.OnAccent);
        _overrides["AccentButtonForegroundPressed"] = Solid(t.OnAccent);
        _overrides["AccentButtonForegroundDisabled"] = Solid(white with { A = 0x5D });
        _overrides["AccentButtonBorderBrush"] = Solid(t.Accent);
        _overrides["AccentButtonBorderBrushPointerOver"] = Solid(accentHover);
        _overrides["AccentButtonBorderBrushPressed"] = Solid(accentPressed);
        _overrides["AccentButtonBorderBrushDisabled"] = Solid(white with { A = 0x0F });

        // ---- 进度条 / 超链接 ----
        // WinUI 3 ProgressBar 模板键名无 Brush 后缀：轨道 Fill=TemplateBinding Background（默认资源键
        // ProgressBarBackground），填充块为 TemplateBinding Foreground（ProgressBarForeground）
        _overrides["ProgressBarForeground"] = Solid(t.Accent);
        _overrides["ProgressBarBackground"] = Solid(PaletteColor.ParseHexOrThrow("#80000000"));
        _overrides["HyperlinkButtonForegroundBrush"] = Solid(t.AccentLight1);
        _overrides["HyperlinkButtonForegroundPointerOverBrush"] = Solid(t.AccentLight2);
        _overrides["HyperlinkButtonForegroundPressedBrush"] = Solid(t.Accent);

        // ---- 列表选中态（WinUI 3 ListViewItem 模板引用的是无 Brush 后缀的键，
        //      默认别名指向 SubtleFillColor 系列、透明度仅 3%~5% 近乎不可见）----
        _overrides["ListViewItemBackgroundSelected"] = Solid(accentHover with { A = 0x4D });
        _overrides["ListViewItemBackgroundSelectedPointerOver"] = Solid(accentHover with { A = 0x66 });
        _overrides["ListViewItemBackgroundSelectedPressed"] = Solid(accentHover with { A = 0x80 });
        _overrides["ListViewItemBackgroundPointerOver"] = Solid(white with { A = 0x14 });

        // ---- 表面 / 分层 ----
        _overrides["SolidBackgroundFillColorBaseBrush"] = Solid(t.WindowFill);
        _overrides["SolidBackgroundFillColorBaseAltBrush"] = Solid(t.LayerFill);
        _overrides["SolidBackgroundFillColorSecondaryBrush"] = Solid(t.CardFill);
        _overrides["SolidBackgroundFillColorTertiaryBrush"] = Solid(card2);
        _overrides["SolidBackgroundFillColorQuarternaryBrush"] = Solid(card3);
        _overrides["SolidBackgroundFillColorMiddleBrush"] = Solid(card2);
        _overrides["LayerFillColorDefaultBrush"] = Solid(t.LayerFill);
        _overrides["LayerFillColorAltBrush"] = Solid(t.WindowFill);
        _overrides["LayerOnMicaBaseAltFillColorDefaultBrush"] = Solid(t.CardFill);
        _overrides["LayerOnMicaBaseAltFillColorSecondaryBrush"] = Solid(card2);
        _overrides["LayerOnMicaBaseAltFillColorTertiaryBrush"] = Solid(card3);
        _overrides["LayerOnMicaBaseAltFillColorDisabledBrush"] = Solid(white with { A = 0x0B });

        // ---- 卡片 / 描边 ----
        _overrides["CardBackgroundFillColorDefaultBrush"] = Solid(t.CardFill);
        _overrides["CardBackgroundFillColorSecondaryBrush"] = Solid(card2);
        _overrides["CardStrokeColorDefaultBrush"] = Solid(t.CardStroke);
        _overrides["CardStrokeColorSecondaryBrush"] = Solid(t.CardStroke);
        _overrides["SurfaceStrokeColorDefaultBrush"] = Solid(t.CardStroke);
        _overrides["SurfaceStrokeColorFlyoutBrush"] = Solid(t.CardStroke);
        _overrides["DividerStrokeColorDefaultBrush"] = Solid(white with { A = 0x0F });

        // ---- 控件填充 / 描边 / 悬停 ----
        _overrides["ControlFillColorDefaultBrush"] = Solid(white with { A = 0x0D });
        _overrides["ControlFillColorSecondaryBrush"] = Solid(white with { A = 0x0F });
        _overrides["ControlFillColorTertiaryBrush"] = Solid(white with { A = 0x16 });
        _overrides["ControlFillColorDisabledBrush"] = Solid(white with { A = 0x0B });
        _overrides["ControlStrokeColorDefaultBrush"] = Solid(white with { A = 0x12 });
        _overrides["ControlStrokeColorSecondaryBrush"] = Solid(white with { A = 0x1A });
        _overrides["ControlStrokeColorDisabledBrush"] = Solid(white with { A = 0x0F });
        _overrides["ControlStrokeColorOnAccentDefaultBrush"] = Solid(white with { A = 0x14 });
        _overrides["ControlStrokeColorOnAccentSecondaryBrush"] = Solid(white with { A = 0x23 });
        _overrides["SubtleFillColorTransparentBrush"] = Solid(default(PaletteColor));
        _overrides["SubtleFillColorSecondaryBrush"] = Solid(white with { A = 0x09 });
        _overrides["SubtleFillColorTertiaryBrush"] = Solid(white with { A = 0x05 });
        _overrides["SubtleFillColorDisabledBrush"] = Solid(default(PaletteColor));

        // ---- 文字层级 ----
        _overrides["TextFillColorPrimaryBrush"] = Solid(t.TitleText);
        _overrides["TextFillColorSecondaryBrush"] = Solid(t.TitleText with { A = 0xC9 });
        _overrides["TextFillColorTertiaryBrush"] = Solid(t.TitleText with { A = 0x8A });
        _overrides["TextFillColorDisabledBrush"] = Solid(t.TitleText with { A = 0x5D });

        // ---- 状态色（深色基底上保证可读的固定错误红）----
        _overrides["SystemFillColorCriticalBrush"] = Solid(PaletteColor.ParseHexOrThrow("#FFFF6B6B"));
        _overrides["AppDangerFillHoverBrush"] = Solid(PaletteColor.ParseHexOrThrow("#FFFF8585"));
        _overrides["AppDangerFillPressedBrush"] = Solid(PaletteColor.ParseHexOrThrow("#FF4F4F"));

        // ---- 文本框 / 选择高亮 ----
        _overrides["TextControlBorderBrush"] = Solid(white with { A = 0x1A });
        _overrides["TextControlBorderBrushPointerOver"] = Solid(white with { A = 0x1A });
        _overrides["TextControlBorderBrushFocused"] = Solid(accentHover);
        _overrides["TextControlBorderBrushDisabled"] = Solid(white with { A = 0x0B });
        _overrides["TextControlSelectionHighlightColor"] = Solid(t.Accent with { A = 0x66 });
        _overrides["TextControlButtonForegroundBrush"] = Solid(t.TitleText with { A = 0x8A });

        // ---- 复选框 / 单选框 / 开关选中态 ----
        _overrides["CheckBoxCheckBackgroundFillCheckedBrush"] = Solid(t.Accent);
        _overrides["CheckBoxCheckBackgroundFillCheckedPointerOverBrush"] = Solid(accentHover);
        _overrides["CheckBoxCheckBackgroundFillCheckedPressedBrush"] = Solid(accentPressed);
        _overrides["CheckBoxCheckGlyphForegroundCheckedBrush"] = Solid(t.OnAccent);
        _overrides["RadioButtonOuterEllipseCheckedStrokeBrush"] = Solid(t.Accent);
        _overrides["RadioButtonOuterEllipseCheckedFillBrush"] = Solid(t.Accent);
        _overrides["RadioButtonCheckGlyphFillBrush"] = Solid(t.OnAccent);
        _overrides["RadioButtonCheckGlyphStrokeBrush"] = Solid(t.OnAccent);
        _overrides["ToggleSwitchFillOnBrush"] = Solid(t.Accent);
        _overrides["ToggleSwitchKnobFillOnBrush"] = Solid(t.OnAccent);

        // ---- 弹层 / 对话框 / 工具提示 ----
        _overrides["FlyoutBackgroundBrush"] = Solid(card2);
        _overrides["FlyoutBorderBrush"] = Solid(t.CardStroke);
        _overrides["ComboBoxDropDownBackgroundBrush"] = Solid(card2);
        _overrides["ComboBoxDropDownBorderBrush"] = Solid(t.CardStroke);
        _overrides["ContentDialogBackgroundBrush"] = Solid(t.WindowFill);
        _overrides["ContentDialogSmokeLayerBrush"] = Solid(white with { A = 0x00 });
        _overrides["ContentDialogSeparatorBorderBrush"] = Solid(t.CardStroke);
        _overrides["ToolTipBackgroundBrush"] = Solid(card3);

        // 镜像写入 Application.Resources 直属键：运行时 Add 进 MergedDictionaries 的覆盖字典
        // 会被 XamlControlsResources 延迟加载默认样式时重建丢弃（表现为启动数秒后
        // ThemeResource 查找失败崩溃），直属键不进合并集合、查找优先级最高且稳定存在；
        // 主题切换经 ForceThemeResourceRefresh 全量重取后仍能取到新值。
        var appResources = Application.Current.Resources;
        foreach (var key in _overrides.Keys.ToList())
            appResources[key] = _overrides[key];
    }
}
