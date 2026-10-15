using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LyricsBar.Classes.Services;
using LyricsBar.Classes.Services.Lyrics;
using LyricsBar.Classes.Settings;
using LyricsBar.Classes.Utils;

namespace LyricsBar.Views;

/// <summary>
/// 精简版的设置窗口。原项目有 12 个设置页、一个可搜索的设置索引和一套分组滚动策略；
/// 精简版把真正会影响"任务栏歌词"的选项收在一页里，改动即时生效并落盘。
/// The trimmed build's settings window. The original had twelve settings pages, a searchable settings index, and a grouped
/// scroll policy; the trimmed build gathers the options that genuinely affect "taskbar lyrics" onto one page, applied and
/// persisted immediately.
/// </summary>
public partial class SettingsWindow : Window
{
    /// <summary>字体下拉里代表"跟随系统"的条目。它不对应任何真实字体名，落盘时写回 null。</summary>
    private const string SystemDefaultFontLabel = "系统默认";

    /// <summary>
    /// "常用颜色"里的一键色板：名称 + `#RRGGBB`。原项目只有一个自由输入框，用户每次都要自己记得色值；
    /// 这里给几个深色壁纸下真正吃得开的常用色，点一下即填进输入框（仍然是普通文本框，可以继续手改任意色值）。
    /// The one-click swatches under "common colours": label plus `#RRGGBB`. The original build had only a free-text field, so
    /// every change meant recalling a hex value; these are the colours that actually read well over a dark wallpaper. A click
    /// fills the text box, which stays an ordinary text box and still accepts any value typed by hand.
    /// </summary>
    private static readonly (string Name, string Value)[] ColorPresets =
    [
        ("纯白", "#FFFFFF"),
        ("米白", "#FFF6E5"),
        ("浅金", "#FFE082"),
        ("天蓝", "#8FD3FF"),
        ("青绿", "#8CE8C8"),
        ("樱粉", "#FFB3C7"),
        ("浅灰", "#C9CDD4"),
        ("深灰", "#6B7280"),
        ("纯黑", "#000000"),
    ];

    private readonly Action _redockRequested;
    private readonly Dictionary<string, System.Windows.Controls.CheckBox> _sourceChecks = new(StringComparer.Ordinal);
    private bool _loading;

    /// <summary>创建设置窗口。/ Creates the settings window.</summary>
    /// <param name="redockRequested">用户点击"重新停靠"时回调。/ Invoked when the user asks to re-dock.</param>
    public SettingsWindow(Action redockRequested)
    {
        _redockRequested = redockRequested ?? throw new ArgumentNullException(nameof(redockRequested));
        InitializeComponent();

        // 标题栏与页头图标共用同一份嵌入的 LyricsBar.ico：取不到时标题栏由系统给默认图标，页头图片直接隐藏，
        // 两条路都不会因为图标缺失而打不开窗口。位图源由 AppIcon 从缓存的 Icon 转出，窗口与托盘互不影响。
        // The title bar and the header share one embedded LyricsBar.ico: when it is missing the title bar falls back to the
        // system default and the header image is simply hidden, so neither path can block the window from opening. The
        // bitmap source is converted by AppIcon from its cached Icon, so the window and the tray never disturb each other.
        var iconSource = AppIcon.LoadImageSource();
        Icon = iconSource;
        if (iconSource is not null)
            HeaderIcon.Source = iconSource;
        else
            HeaderIcon.Visibility = Visibility.Collapsed;

        Loaded += (_, _) => SyncFontComboEditState();
        BuildSourceList();
        BuildFontFamilyList();
        BuildColorPresets();
        WireEvents();
        LoadFromSettings();
    }

    /// <summary>
    /// 可编辑下拉框的显隐切换。模板里 ContentSite 与 PART_EditableTextBox 同时存在：
    /// 可编辑时文本该由文本框承载（可打字、有光标），不可编辑时回到 ContentSite 显示选中项。
    /// ComboBox 类只承诺显隐 PART_EditableTextBox，ContentSite 的让位归模板作者负责；
    /// 而 XAML 的 Style Setter 不允许带 TargetName，触发器写不进模板里的内层 Grid，
    /// 所以放在这里、模板应用后统一做一次（这个下拉的可编辑状态是静态的，之后不会变）。
    /// Switches visibility for the editable combobox. The template holds ContentSite and PART_EditableTextBox side by side:
    /// while editable the text belongs to the box (typing, caret), otherwise ContentSite shows the selection. ComboBox only
    /// promises to toggle PART_EditableTextBox; vacating ContentSite is the template author's job. A Style setter cannot
    /// carry TargetName and triggers cannot reach the template's inner Grid, so this runs once after the template is applied —
    /// the editable state here is static and never changes afterwards.
    /// </summary>
    private void SyncFontComboEditState()
    {
        if (!FontFamilyCombo.IsEditable)
            return;

        if (FontFamilyCombo.Template.FindName("PART_EditableTextBox", FontFamilyCombo) is FrameworkElement editBox)
            editBox.Visibility = Visibility.Visible;
        if (FontFamilyCombo.Template.FindName("ContentSite", FontFamilyCombo) is FrameworkElement contentSite)
            contentSite.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// 用系统里真实安装的字体填充字体下拉，而不是写死几个名字。
    /// 写死的清单在不同机器上会有一半不存在，用户选了没反应还以为是程序坏了；直接枚举系统字体则永远是可选中的。
    /// 字体名由系统提供，不需要自己维护。
    /// Fills the font list with the families actually installed on this machine rather than a hard-coded handful. A hard-coded
    /// list is half missing on any given machine, and the user picks one that does nothing and assumes the app is broken;
    /// enumerating the system list always yields selectable entries and needs no maintenance.
    /// </summary>
    private void BuildFontFamilyList()
    {
        var names = new List<string> { SystemDefaultFontLabel };

        try
        {
            names.AddRange(Fonts.SystemFontFamilies
                .Select(family => family.Source)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.CurrentCulture));
        }
        catch (Exception ex)
        {
            // 枚举字体失败不该让设置窗口打不开：至少还能手输字体名。
            Log.Warn("Settings", $"枚举系统字体失败，字体列表只保留默认项: {ex.Message}");
        }

        FontFamilyCombo.ItemsSource = names;
    }

    private void BuildSourceList()
    {
        foreach (var sourceId in LyricsSourceCatalog.DefaultOrder)
        {
            var check = new System.Windows.Controls.CheckBox
            {
                Content = LyricsSourceCatalog.GetDisplayName(sourceId),
                Tag = sourceId,
                Margin = new Thickness(0, 3, 0, 3)
            };
            check.Checked += (_, _) => ApplyFromUi(nameof(AppSettings.LyricsSource));
            check.Unchecked += (_, _) => ApplyFromUi(nameof(AppSettings.LyricsSource));
            _sourceChecks[sourceId] = check;
            SourcePanel.Children.Add(check);
        }
    }

    private void WireEvents()
    {
        EnabledCheck.Checked += (_, _) => ApplyFromUi(nameof(AppSettings.LyricsEnabled));
        EnabledCheck.Unchecked += (_, _) => ApplyFromUi(nameof(AppSettings.LyricsEnabled));
        TwoLineCheck.Checked += (_, _) => ApplyFromUi();
        TwoLineCheck.Unchecked += (_, _) => ApplyFromUi();
        TrackInfoBlockCheck.Checked += (_, _) => ApplyFromUi();
        TrackInfoBlockCheck.Unchecked += (_, _) => ApplyFromUi();
        SyllableCheck.Checked += (_, _) => ApplyFromUi();
        SyllableCheck.Unchecked += (_, _) => ApplyFromUi();
        ProgressCheck.Checked += (_, _) => ApplyFromUi();
        ProgressCheck.Unchecked += (_, _) => ApplyFromUi();
        CoverCheck.Checked += (_, _) => ApplyFromUi();
        CoverCheck.Unchecked += (_, _) => ApplyFromUi();
        MouseControlCheck.Checked += (_, _) => ApplyFromUi();
        MouseControlCheck.Unchecked += (_, _) => ApplyFromUi();
        NoLyricsInfoCheck.Checked += (_, _) => ApplyFromUi();
        NoLyricsInfoCheck.Unchecked += (_, _) => ApplyFromUi();
        FixedWidthCheck.Checked += (_, _) => ApplyFromUi();
        FixedWidthCheck.Unchecked += (_, _) => ApplyFromUi();
        ShadowCheck.Checked += (_, _) => ApplyFromUi();
        ShadowCheck.Unchecked += (_, _) => ApplyFromUi();
        SoftwareRenderCheck.Checked += (_, _) => ApplyFromUi();
        SoftwareRenderCheck.Unchecked += (_, _) => ApplyFromUi();
        CompatibilityModeCheck.Checked += (_, _) => ApplyFromUi();
        CompatibilityModeCheck.Unchecked += (_, _) => ApplyFromUi();

        FontSizeSlider.ValueChanged += (_, _) => { UpdateValueLabels(); ApplyFromUi(); };
        CharSpacingSlider.ValueChanged += (_, _) => { UpdateValueLabels(); ApplyFromUi(); };
        LineGapSlider.ValueChanged += (_, _) => { UpdateValueLabels(); ApplyFromUi(); };
        UnsungSlider.ValueChanged += (_, _) => { UpdateValueLabels(); ApplyFromUi(); };
        FixedWidthSlider.ValueChanged += (_, _) => { UpdateValueLabels(); ApplyFromUi(); };
        MaxWidthSlider.ValueChanged += (_, _) => { UpdateValueLabels(); ApplyFromUi(); };
        OffsetSlider.ValueChanged += (_, _) => { UpdateValueLabels(); ApplyFromUi(); };

        AlignCombo.SelectionChanged += (_, _) => ApplyFromUi();
        ConversionCombo.SelectionChanged += (_, _) => ApplyFromUi();
        BarAlignmentCombo.SelectionChanged += (_, _) => ApplyFromUi();
        SecondaryLineCombo.SelectionChanged += (_, _) => ApplyFromUi();
        MatchStrictnessCombo.SelectionChanged += (_, _) => ApplyFromUi();

        // 换渲染引擎要重建渲染器才生效，而重建是宿主的事：这里只落盘并提示用户点一次"重新停靠"。
        // Switching engines needs the renderer rebuilt, which is the host's business: this only persists the value and points
        // the user at the re-dock button.
        RendererCombo.SelectionChanged += (_, _) =>
        {
            ApplyFromUi(nameof(AppSettings.LyricsRendererEngine));
            StatusText.Text = "已切换歌词渲染引擎，点「重新停靠到任务栏」后生效。";
        };

        // 字体下拉可编辑：选中列表项立刻生效；手动输入则等焦点离开再落盘，
        // 免得每敲一个字符都写一次设置文件。
        // The font list is editable: picking an entry applies at once, while typed input waits for focus to leave so that every
        // keystroke does not rewrite the settings file.
        FontFamilyCombo.SelectionChanged += (_, _) => ApplyFromUi();
        FontFamilyCombo.LostFocus += (_, _) => ApplyFromUi();

        ColorBox.TextChanged += (_, _) =>
        {
            UpdateColorPreview();
            ApplyFromUi();
        };

        RedockButton.Click += (_, _) =>
        {
            _redockRequested();
            StatusText.Text = "已重新查找任务栏并停靠。";
        };

        OpenFolderButton.Click += (_, _) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo(SettingsManager.Directory) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                StatusText.Text = $"打开设置文件夹失败：{ex.Message}";
            }
        };

        ResetButton.Click += (_, _) =>
        {
            SettingsManager.Apply(new AppSettings(), resetScope: SettingsResetScope.All);
            LoadFromSettings();
            StatusText.Text = "已恢复默认设置。";
        };

        CloseButton.Click += (_, _) => Close();
    }

    private void LoadFromSettings()
    {
        _loading = true;
        try
        {
            var s = SettingsManager.Current;

            EnabledCheck.IsChecked = s.LyricsEnabled;
            TwoLineCheck.IsChecked = s.TwoLineLyricsEnabled;
            TrackInfoBlockCheck.IsChecked = s.LyricsTrackInfoBlockEnabled;
            SyllableCheck.IsChecked = s.LyricsSyllableHighlightEnabled;
            ProgressCheck.IsChecked = s.LyricsProgressBarEnabled;
            CoverCheck.IsChecked = s.LyricsCoverArtEnabled;
            NoLyricsInfoCheck.IsChecked = s.LyricsShowTrackInfoWhenNoLyrics;
            MouseControlCheck.IsChecked = s.LyricsMouseControlEnabled;
            FixedWidthCheck.IsChecked = s.LyricsFixedWidthEnabled;
            ShadowCheck.IsChecked = s.LyricsTextShadowEnabled;
            SoftwareRenderCheck.IsChecked = s.DisableWebView2Gpu;
            CompatibilityModeCheck.IsChecked = s.WebView2CompatibilityMode;

            FontSizeSlider.Value = s.LyricsFontSize;
            CharSpacingSlider.Value = s.LyricsCharacterSpacingPercent;
            LineGapSlider.Value = s.LyricsLineGapPercent;
            UnsungSlider.Value = s.LyricsUnsungOpacityPercent;
            FixedWidthSlider.Value = s.LyricsFixedWidthDip;
            MaxWidthSlider.Value = s.LyricsMaxWidthDip;
            OffsetSlider.Value = s.TaskbarOffsetDip;

            AlignCombo.SelectedIndex = s.LyricsTextAlignment switch
            {
                LyricsTextAlignment.Left => 1,
                LyricsTextAlignment.Right => 2,
                LyricsTextAlignment.Split => 3,
                _ => 0
            };

            ConversionCombo.SelectedIndex = s.LyricsChineseConversion switch
            {
                LyricsChineseConversionMode.SimplifiedToTraditional => 1,
                LyricsChineseConversionMode.TraditionalToSimplified => 2,
                _ => 0
            };

            BarAlignmentCombo.SelectedIndex = s.LyricsBarAlignment switch
            {
                LyricsBarAlignment.Left => 0,
                LyricsBarAlignment.Right => 2,
                _ => 1
            };

            RendererCombo.SelectedIndex = s.LyricsRendererEngine == LyricsRendererEngine.WebView2 ? 1 : 0;

            SecondaryLineCombo.SelectedIndex = ResolveSecondaryLineIndex(s.LyricsSecondaryLine.Order);
            MatchStrictnessCombo.SelectedIndex = s.LyricsMatchStrictness switch
            {
                LyricsMatchStrictness.Strict => 2,
                LyricsMatchStrictness.Balanced => 1,
                _ => 0
            };

            FontFamilyCombo.Text = string.IsNullOrWhiteSpace(s.FontFamily) ? SystemDefaultFontLabel : s.FontFamily;

            ColorBox.Text = s.LyricsTextColor;

            // EnabledSourceIds 为 null 表示"从未配置"，也就是全部启用。
            var enabled = s.LyricsSource.EnabledSourceIds;
            foreach (var (sourceId, check) in _sourceChecks)
            {
                check.IsChecked = enabled is null || enabled.Contains(sourceId, StringComparer.Ordinal);
            }
        }
        finally
        {
            _loading = false;
        }

        UpdateValueLabels();
        UpdateColorPreview();
    }

    private void UpdateValueLabels()
    {
        FontSizeText.Text = ((int)FontSizeSlider.Value).ToString(CultureInfo.InvariantCulture);
        CharSpacingText.Text = ((int)CharSpacingSlider.Value).ToString(CultureInfo.InvariantCulture);
        LineGapText.Text = ((int)LineGapSlider.Value).ToString(CultureInfo.InvariantCulture);
        UnsungText.Text = ((int)UnsungSlider.Value).ToString(CultureInfo.InvariantCulture);
        FixedWidthText.Text = ((int)FixedWidthSlider.Value).ToString(CultureInfo.InvariantCulture);
        MaxWidthText.Text = ((int)MaxWidthSlider.Value).ToString(CultureInfo.InvariantCulture);
        OffsetText.Text = ((int)OffsetSlider.Value).ToString(CultureInfo.InvariantCulture);
    }

    private void UpdateColorPreview()
    {
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(ColorBox.Text.Trim());
            ColorPreview.Background = new SolidColorBrush(color);
        }
        catch (Exception)
        {
            ColorPreview.Background = Brushes.Transparent;
        }
    }

    /// <summary>
    /// 铺开"常用颜色"色板。每个色块是一个带模板的最小按钮：模板里只有一块圆角颜色块，
    /// 鼠标悬停只加一圈蓝边，不参与任何"按下/禁用"状态，所以点下去看到的就是它填进去的颜色。
    /// Lays out the common-colour swatches. Each one is a minimal templated button whose template holds a single rounded
    /// colour block; hovering only adds a blue ring and no pressed/disabled state is involved, so what is clicked is exactly
    /// the colour that lands in the text box.
    /// </summary>
    private void BuildColorPresets()
    {
        ColorPresetPanel.Children.Clear();

        foreach (var (name, value) in ColorPresets)
        {
            var swatch = new Border
            {
                Width = 22,
                Height = 22,
                Margin = new Thickness(0, 0, 6, 6),
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x8C, 0x8C, 0x8C)),
                Background = new SolidColorBrush(ParsePresetColor(value))
            };

            var button = new Button
            {
                Content = swatch,
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = $"{name}  {value}"
            };

            // 模板里不留任何内边距：默认按钮的内容区会把 22×22 的色块挤小，颜色块就不再是正方形的色卡。
            // The template keeps no padding: the default button content area would shrink the 22x22 block, and the swatch
            // would stop being a square colour chip.
            var template = new ControlTemplate(typeof(Button));
            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
            content.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            template.VisualTree = content;
            button.Template = template;

            button.Click += (_, _) => ColorBox.Text = value;
            ColorPresetPanel.Children.Add(button);
        }
    }

    /// <summary>把 `#RRGGBB` 解析成颜色；色板是自己的常量，解析失败时退回白色而不是抛异常。</summary>
    private static Color ParsePresetColor(string value)
    {
        try
        {
            return (Color)ColorConverter.ConvertFromString(value);
        }
        catch (Exception)
        {
            return Colors.White;
        }
    }

    /// <summary>
    /// 把界面上的值写回设置并落盘。声明 propertyName 是为了让取词层分辨"该重新联网取词"与"只需重绘"。
    /// Writes the UI values back into the settings and persists them. Declaring <paramref name="propertyName"/> lets the
    /// retrieval layer tell "refetch over the network" apart from "just repaint".
    /// </summary>
    private void ApplyFromUi(string? propertyName = null)
    {
        if (_loading)
            return;

        var s = SettingsManager.Current.Clone();

        s.LyricsEnabled = EnabledCheck.IsChecked == true;
        s.TwoLineLyricsEnabled = TwoLineCheck.IsChecked == true;
        s.LyricsTrackInfoBlockEnabled = TrackInfoBlockCheck.IsChecked == true;
        s.LyricsSyllableHighlightEnabled = SyllableCheck.IsChecked == true;
        s.LyricsProgressBarEnabled = ProgressCheck.IsChecked == true;
        s.LyricsCoverArtEnabled = CoverCheck.IsChecked == true;
        s.LyricsShowTrackInfoWhenNoLyrics = NoLyricsInfoCheck.IsChecked == true;
        s.LyricsMouseControlEnabled = MouseControlCheck.IsChecked == true;
        s.LyricsFixedWidthEnabled = FixedWidthCheck.IsChecked == true;
        s.LyricsTextShadowEnabled = ShadowCheck.IsChecked == true;
        s.DisableWebView2Gpu = SoftwareRenderCheck.IsChecked == true;
        s.WebView2CompatibilityMode = CompatibilityModeCheck.IsChecked == true;

        // 两个降级开关都关掉，等于"我要回到默认渲染路径"，所以把程序自己记住的降级级别一并清零。
        // 否则它会继续沿用上次记住的重度降级，用户会觉得这些开关关不掉。
        // Turning both fallback switches off means "go back to the default rendering path", so the tier the app remembered is
        // reset too. Otherwise it keeps reusing the heavy fallback and the switches look like they cannot be turned off.
        if (!s.WebView2CompatibilityMode && !s.DisableWebView2Gpu)
            s.WebView2RendererTier = 0;

        s.LyricsFontSize = FontSizeSlider.Value;
        s.LyricsCharacterSpacingPercent = (int)CharSpacingSlider.Value;
        s.LyricsLineGapPercent = (int)LineGapSlider.Value;
        s.LyricsUnsungOpacityPercent = (int)UnsungSlider.Value;
        s.LyricsFixedWidthDip = (int)FixedWidthSlider.Value;
        s.LyricsMaxWidthDip = MaxWidthSlider.Value;
        s.TaskbarOffsetDip = OffsetSlider.Value;

        s.LyricsTextAlignment = AlignCombo.SelectedIndex switch
        {
            1 => LyricsTextAlignment.Left,
            2 => LyricsTextAlignment.Right,
            3 => LyricsTextAlignment.Split,
            _ => LyricsTextAlignment.Center
        };

        s.LyricsChineseConversion = ConversionCombo.SelectedIndex switch
        {
            1 => LyricsChineseConversionMode.SimplifiedToTraditional,
            2 => LyricsChineseConversionMode.TraditionalToSimplified,
            _ => LyricsChineseConversionMode.None
        };

        s.LyricsBarAlignment = BarAlignmentCombo.SelectedIndex switch
        {
            0 => LyricsBarAlignment.Left,
            2 => LyricsBarAlignment.Right,
            _ => LyricsBarAlignment.Center
        };

        s.LyricsRendererEngine = RendererCombo.SelectedIndex == 1
            ? LyricsRendererEngine.WebView2
            : LyricsRendererEngine.Native;

        s.LyricsSecondaryLine = SecondaryLineCombo.SelectedIndex switch
        {
            1 => new LyricsSecondaryLineSettings([LyricsSecondaryLineMode.Romanization, LyricsSecondaryLineMode.Translation, LyricsSecondaryLineMode.NextLine]),
            2 => new LyricsSecondaryLineSettings([LyricsSecondaryLineMode.NextLine, LyricsSecondaryLineMode.Translation, LyricsSecondaryLineMode.Romanization]),
            _ => new LyricsSecondaryLineSettings([LyricsSecondaryLineMode.Translation, LyricsSecondaryLineMode.Romanization, LyricsSecondaryLineMode.NextLine])
        };

        s.LyricsMatchStrictness = MatchStrictnessCombo.SelectedIndex switch
        {
            2 => LyricsMatchStrictness.Strict,
            1 => LyricsMatchStrictness.Balanced,
            _ => LyricsMatchStrictness.Lenient
        };

        // "系统默认"是界面上的占位标签，不是字体名，落盘时必须写回 null，
        // 否则下次启动会去找一个叫"系统默认"的字体，找不到就退回默认字体，看起来像设置丢了。
        // "System default" is a UI label rather than a font name, so it must be written back as null; otherwise the next launch
        // looks for a font literally called "system default", falls back to the default, and the setting looks lost.
        var typedFont = FontFamilyCombo.Text?.Trim();
        s.FontFamily = string.IsNullOrEmpty(typedFont) ||
                       string.Equals(typedFont, SystemDefaultFontLabel, StringComparison.Ordinal)
            ? null
            : typedFont;

        var color = ColorBox.Text.Trim();
        if (IsValidColor(color))
            s.LyricsTextColor = color;

        var checkedIds = _sourceChecks
            .Where(pair => pair.Value.IsChecked == true)
            .Select(pair => pair.Key)
            .ToArray();

        // 全部勾选时写回 null，而不是列出六个 id：这样以后新增的歌词来源会自动启用。
        s.LyricsSource = new LyricsSourceSettings(
            checkedIds.Length == _sourceChecks.Count ? null : checkedIds);

        SettingsManager.Apply(s, propertyName);
    }

    private static bool IsValidColor(string value)
    {
        try
        {
            _ = (Color)ColorConverter.ConvertFromString(value);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 把存储的 Order 数组映射回下拉框索引。只认三种预设顺序，自定义顺序回退到 0（翻译优先）。
    /// Maps the stored Order array back to a combo-box index. Only the three preset orders are recognised; a custom order falls
    /// back to 0 (translation first).
    /// </summary>
    private static int ResolveSecondaryLineIndex(IReadOnlyList<LyricsSecondaryLineMode>? order)
    {
        if (order is null || order.Count == 0)
            return 0;

        if (order.Count >= 3 &&
            order[0] == LyricsSecondaryLineMode.Romanization &&
            order[1] == LyricsSecondaryLineMode.Translation &&
            order[2] == LyricsSecondaryLineMode.NextLine)
            return 1;

        if (order.Count >= 3 &&
            order[0] == LyricsSecondaryLineMode.NextLine &&
            order[1] == LyricsSecondaryLineMode.Translation &&
            order[2] == LyricsSecondaryLineMode.Romanization)
            return 2;

        return 0;
    }
}
