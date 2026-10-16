using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LyricsBar.Classes.Services;
using LyricsBar.Classes.Services.Lyrics;
using LyricsBar.Classes.Services.Lyrics.Presentation;
using LyricsBar.Classes.Settings;

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
        BuildColorPresetList();
        BuildFontFamilyList();
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
    /// 用 <see cref="LyricsFontCatalog.Candidates"/> 这份精选清单填充字体下拉，而不是枚举系统里所有字体族。
    /// 枚举出来的清单是按字体名排序的，开头几十上百项全是图标字体（Marlett、Segoe MDL2 Assets 之类）和各种语言
    /// 补充字形，真正能拿来显示中文歌词的那几款要翻很久才看得到，用户往往以为下拉里没有可用字体。
    /// 下拉本身仍然是可编辑的，清单里没有的字体族直接在框里输入名字即可，所以这是一份"常用项靠前"的清单，不是白名单。
    /// Fills the font drop-down from <see cref="LyricsFontCatalog.Candidates"/> instead of enumerating every installed family.
    /// The enumerated list is sorted by family name, so it opens with dozens of icon faces (Marlett, Segoe MDL2 Assets and
    /// friends) and assorted language-pack faces, while the few families that actually render Chinese lyrics take a long scroll
    /// to reach — users concluded the drop-down held nothing usable. The drop-down stays editable, so a family outside the list
    /// is still typed in by name: this puts the common ones first, it is not a whitelist.
    /// </summary>
    private void BuildFontFamilyList()
    {
        // "跟随系统"那一项仍然排在最前：它是默认值，也是用户想回到系统字体时唯一要找的条目。
        var names = new List<string> { LyricsFontCatalog.SystemDefaultLabel };
        names.AddRange(LyricsFontCatalog.Candidates);

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

    /// <summary>
    /// 颜色预设色板：每个预设一个小方块，点一下就把它的 <c>#RRGGBB</c> 写进颜色输入框。
    /// 点击只做"填空"这一件事：颜色框自己的 TextChanged 已经负责校验、刷新预览并落盘，
    /// 在这里再走一遍同样的逻辑只会多出一条容易和那边跑偏的路径。
    /// 方块用 Border 而不是 Button：本文件的按钮模板会把背景跟着悬停 / 按下状态改色（对普通按钮是对的），
    /// 而色块的全部意义就是准确显示那个颜色，任何状态变色都会让人读错颜色。
    /// The colour-preset palette: one small square per preset, and clicking it writes that <c>#RRGGBB</c> into the colour box.
    /// The click does exactly one thing — fill the box: the box's own TextChanged already validates, refreshes the preview and
    /// persists, and repeating that here would add a second path free to drift away from it.
    /// The squares are Borders rather than Buttons because this file's button template recolours its background on hover and
    /// press (right for a real button), while a swatch exists precisely to show one colour accurately — any state tint makes
    /// the colour read wrong.
    /// </summary>
    private void BuildColorPresetList()
    {
        foreach (var preset in LyricsTextColorPalette.Presets)
        {
            var swatch = new Border
            {
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(4),
                // 描边是中性灰而不是预设色本身：纯白这类浅色在白色卡片上没有描边就完全看不见边界。
                // 下拉框 / 按钮用的也是同一个灰，卡片里不会多出一种孤立的中性色。
                // The hairline is neutral grey rather than the preset's own colour: a light preset such as pure white would be
                // invisible on the white card without it, and the same grey is what the comboboxes and buttons already use, so
                // the card gains no stray neutral tone.
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x8C, 0x8C, 0x8C)),
                BorderThickness = new Thickness(1),
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(preset.Hex)),
                Margin = new Thickness(0, 0, 6, 6),
                Cursor = Cursors.Hand,
                ToolTip = preset.Name
            };
            swatch.MouseLeftButtonDown += (_, _) => ColorBox.Text = preset.Hex;
            ColorPresetPanel.Children.Add(swatch);
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
        FontBoldCheck.Checked += (_, _) => ApplyFromUi();
        FontBoldCheck.Unchecked += (_, _) => ApplyFromUi();
        FontItalicCheck.Checked += (_, _) => ApplyFromUi();
        FontItalicCheck.Unchecked += (_, _) => ApplyFromUi();
        SoftwareRenderCheck.Checked += (_, _) => ApplyFromUi();
        SoftwareRenderCheck.Unchecked += (_, _) => ApplyFromUi();
        CompatibilityModeCheck.Checked += (_, _) => ApplyFromUi();
        CompatibilityModeCheck.Unchecked += (_, _) => ApplyFromUi();

        FontSizeSlider.ValueChanged += (_, _) => { UpdateValueLabels(); ApplyFromUi(); };
        CharSpacingSlider.ValueChanged += (_, _) => { UpdateValueLabels(); ApplyFromUi(); };
        LineGapSlider.ValueChanged += (_, _) => { UpdateValueLabels(); ApplyFromUi(); };
        TrackInfoWidthSlider.ValueChanged += (_, _) => { UpdateValueLabels(); ApplyFromUi(); };
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
            FontBoldCheck.IsChecked = s.LyricsFontBold;
            FontItalicCheck.IsChecked = s.LyricsFontItalic;
            SoftwareRenderCheck.IsChecked = s.DisableWebView2Gpu;
            CompatibilityModeCheck.IsChecked = s.WebView2CompatibilityMode;

            FontSizeSlider.Value = s.LyricsFontSize;
            CharSpacingSlider.Value = s.LyricsCharacterSpacingPercent;
            LineGapSlider.Value = s.LyricsLineGapPercent;
            TrackInfoWidthSlider.Value = s.LyricsTrackInfoWidthDip;
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

            FontFamilyCombo.Text = string.IsNullOrWhiteSpace(s.FontFamily) ? LyricsFontCatalog.SystemDefaultLabel : s.FontFamily;

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
        // 曲目信息宽度是唯一一个"0 有意义"的滑块：0 表示自适应，滑到最左边时显示"自适应"而不是光秃秃的 0，
        // 否则用户会以为这一档是"宽度为零、看不见了"。
        // The track-info width is the only slider where zero carries meaning: it means automatic, so the far-left position reads
        // "automatic" instead of a bare 0, which would look like "zero width, nothing visible".
        TrackInfoWidthText.Text = TrackInfoWidthSlider.Value <= 0
            ? "自适应"
            : ((int)TrackInfoWidthSlider.Value).ToString(CultureInfo.InvariantCulture);
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
        s.LyricsFontBold = FontBoldCheck.IsChecked == true;
        s.LyricsFontItalic = FontItalicCheck.IsChecked == true;
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
        // 滑块下面是连续的 0..400，而规则只认"0 = 自适应"或 40..400：滑块停在 10/20/30 时按规则夹成 40，
        // 并把滑块自己推回 40，否则标签会一直显示用户选的 10 而实际生效的是 40。
        // The slider is continuous 0..400 while the rule only accepts "0 = automatic" or 40..400: a thumb resting on 10/20/30 is
        // clamped to 40 by the rule, and the slider is pushed back to 40 as well, otherwise the label would keep showing the 10
        // the user picked while 40 is what actually applies.
        var trackInfoWidthDip = LyricsTrackInfoWidthPolicy.NormalizeManual(TrackInfoWidthSlider.Value);
        s.LyricsTrackInfoWidthDip = trackInfoWidthDip;
        if (Math.Abs(trackInfoWidthDip - TrackInfoWidthSlider.Value) > 0.01)
            TrackInfoWidthSlider.Value = trackInfoWidthDip;
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
                       string.Equals(typedFont, LyricsFontCatalog.SystemDefaultLabel, StringComparison.Ordinal)
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
