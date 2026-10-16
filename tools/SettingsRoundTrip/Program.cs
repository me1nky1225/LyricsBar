// Settings-window load/save round-trip harness for the v1.4.1 dropdowns.
// Builds the real LyricsBar.Views.SettingsWindow, drives its combo boxes exactly like a user click,
// and checks both in-memory settings and the JSON written to %LOCALAPPDATA%\LyricsBar\settings.json.
// Restores the user's settings file at the end.
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LyricsBar.Classes.Services.Lyrics.Presentation;
using LyricsBar.Classes.Settings;
using LyricsBar.Views;

internal static class Program
{
    private static readonly string SettingsPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LyricsBar", "settings.json");

    private static int _fail;

    [STAThread]
    private static int Main()
    {
        var backup = Path.Combine(Path.GetTempPath(), "lyricsbar-settings-backup-" + Guid.NewGuid().ToString("N") + ".json");
        var hadFile = File.Exists(SettingsPath);
        if (hadFile) File.Copy(SettingsPath, backup, true);
        Console.WriteLine($"settings: {SettingsPath} (backup {(hadFile ? backup : "<none>")})");

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        try
        {
            var w = new SettingsWindow(() => { });
            var match = (ComboBox)w.FindName("MatchStrictnessCombo");
            var second = (ComboBox)w.FindName("SecondaryLineCombo");

            Check("MatchStrictnessCombo item count", match.Items.Count, 3);
            Check("SecondaryLineCombo item count", second.Items.Count, 3);
            Console.WriteLine($"items: match=[{string.Join(" | ", match.Items.Cast<ComboBoxItem>().Select(i => i.Content))}]");
            Console.WriteLine($"items: second=[{string.Join(" | ", second.Items.Cast<ComboBoxItem>().Select(i => i.Content))}]");

            // ---- match strictness: drive the combo, then read memory + disk ----
            SetAndVerify(match, 0, "LyricsMatchStrictness", "Lenient");
            SetAndVerify(match, 2, "LyricsMatchStrictness", "Strict");
            SetAndVerify(match, 1, "LyricsMatchStrictness", "Balanced");

            // ---- second-line priority: order arrays land in both memory and JSON ----
            SetAndVerifyOrder(second, 2, "NextLine,Translation,Romanization");
            SetAndVerifyOrder(second, 1, "Romanization,Translation,NextLine");
            SetAndVerifyOrder(second, 0, "Translation,Romanization,NextLine");

            // ---- load path: a freshly built window must show what was just saved ----
            w.Close();
            var w2 = new SettingsWindow(() => { });
            var match2 = (ComboBox)w2.FindName("MatchStrictnessCombo");
            var second2 = (ComboBox)w2.FindName("SecondaryLineCombo");
            Check("reload MatchStrictnessCombo.SelectedIndex", match2.SelectedIndex, 1);
            Check("reload SecondaryLineCombo.SelectedIndex", second2.SelectedIndex, 0);
            w2.Close();

            // ---- a non-preset order must fall back to index 0 instead of throwing ----
            var saved = JsonNode.Parse(File.ReadAllText(SettingsPath))!;
            saved["LyricsSecondaryLine"]!["Order"] = new JsonArray("Translation", "NextLine", "Romanization");
            File.WriteAllText(SettingsPath, saved.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            var w3 = new SettingsWindow(() => { });
            Check("seeded non-preset order falls back to 0", ((ComboBox)w3.FindName("SecondaryLineCombo")).SelectedIndex, 0);
            Check("seeded non-preset order keeps strictness", ((ComboBox)w3.FindName("MatchStrictnessCombo")).SelectedIndex, 1);
            w3.Close();

            // ---- v1.5.0: 加粗 / 斜体 / 曲目信息宽度 / 预设字体与颜色 ----
            // 这四件事全部是"设置窗口写、宿主读"，所以只验证窗口这一侧能不能真的落盘。
            var w4 = new SettingsWindow(() => { });
            var fontCombo = (ComboBox)w4.FindName("FontFamilyCombo");
            var bold = (CheckBox)w4.FindName("FontBoldCheck");
            var italic = (CheckBox)w4.FindName("FontItalicCheck");
            var width = (Slider)w4.FindName("TrackInfoWidthSlider");
            var swatches = (WrapPanel)w4.FindName("ColorPresetPanel");

            Check("FontFamilyCombo item count", fontCombo.Items.Count, LyricsFontCatalog.Candidates.Count + 1);
            Check("FontFamilyCombo starts with the system label",
                (fontCombo.Items[0] as string) == LyricsFontCatalog.SystemDefaultLabel ? 1 : 0, 1);
            Console.WriteLine($"items: font=[{string.Join(" | ", fontCombo.Items.Cast<string>().Take(4))} …]");
            Check("ColorPresetPanel child count", swatches.Children.Count, LyricsTextColorPalette.Presets.Count);

            Check("bold off by default", bold.IsChecked == false ? 1 : 0, 1);
            bold.IsChecked = true;
            CheckDiskBool("LyricsFontBold", true);
            italic.IsChecked = true;
            CheckDiskBool("LyricsFontItalic", true);

            width.Value = 220;
            CheckDiskNumber("LyricsTrackInfoWidthDip", 220);
            // 滑块下限是 0，而规则只认 0 或 >= 40：停在 10 时必须被夹到 40，而且滑块自己也要被推回 40，
            // 否则标签显示 10、实际生效 40。
            width.Value = 10;
            CheckDiskNumber("LyricsTrackInfoWidthDip", LyricsTrackInfoWidthPolicy.MinimumDip);
            Check("slider snaps back to the floor", (int)Math.Round(width.Value), (int)LyricsTrackInfoWidthPolicy.MinimumDip);
            width.Value = 400;
            CheckDiskNumber("LyricsTrackInfoWidthDip", 400);
            width.Value = 0;
            CheckDiskNumber("LyricsTrackInfoWidthDip", 0);
            Check("zero label reads 自适应", ((TextBlock)w4.FindName("TrackInfoWidthText")).Text, "自适应");

            // 点一下色块：色块自己只做一件事——把 #RRGGBB 写进颜色框，预览与存盘都交给已有的 TextChanged。
            var swatch = (Border)swatches.Children[3];
            var preset = LyricsTextColorPalette.Presets[3];
            swatch.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            {
                RoutedEvent = UIElement.MouseLeftButtonDownEvent,
                Source = swatch
            });
            Check("swatch click fills the colour box", ((TextBox)w4.FindName("ColorBox")).Text, preset.Hex);
            CheckDiskString("LyricsTextColor", preset.Hex);

            // 落盘的东西必须能被新窗口读回来。
            w4.Close();
            var w5 = new SettingsWindow(() => { });
            Check("reload FontBoldCheck", ((CheckBox)w5.FindName("FontBoldCheck")).IsChecked == true ? 1 : 0, 1);
            Check("reload FontItalicCheck", ((CheckBox)w5.FindName("FontItalicCheck")).IsChecked == true ? 1 : 0, 1);
            Check("reload TrackInfoWidthSlider", (int)Math.Round(((Slider)w5.FindName("TrackInfoWidthSlider")).Value), 0);
            w5.Close();
        }
        catch (Exception ex)
        {
            Console.WriteLine("EXCEPTION: " + ex);
            _fail++;
        }
        finally
        {
            if (hadFile) File.Copy(backup, SettingsPath, true);
            else File.Delete(SettingsPath);
            Console.WriteLine($"restored settings file (hadFile={hadFile})");
        }

        Console.WriteLine(_fail == 0 ? "RESULT: PASS" : $"RESULT: FAIL ({_fail})");
        return _fail == 0 ? 0 : 1;
    }

    private static void SetAndVerify(ComboBox combo, int index, string jsonName, string expected)    {
        combo.SelectedIndex = index;
        var memory = SettingsManager.Current.LyricsMatchStrictness.ToString();
        var disk = ReadDiskField(jsonName);
        Console.WriteLine($"match={index} -> memory={memory} disk={disk}");
        if (memory != expected) Fail($"memory after match={index}: {memory} != {expected}");
        if (disk != expected) Fail($"disk after match={index}: {disk} != {expected}");
    }

    private static void SetAndVerifyOrder(ComboBox combo, int index, string expected)
    {
        combo.SelectedIndex = index;
        var memory = string.Join(",", (SettingsManager.Current.LyricsSecondaryLine.Order ?? []).Select(m => m.ToString()));
        var disk = ReadDiskOrder();
        Console.WriteLine($"second={index} -> memory=[{memory}] disk=[{disk}]");
        if (memory != expected) Fail($"memory order after second={index}: [{memory}] != [{expected}]");
        if (disk != expected) Fail($"disk order after second={index}: [{disk}] != [{expected}]");
    }

    private static string ReadDiskField(string name)
    {
        var node = JsonNode.Parse(File.ReadAllText(SettingsPath))!;
        return node[name]?.GetValue<string>() ?? "<missing>";
    }

    private static string ReadDiskOrder()
    {
        var node = JsonNode.Parse(File.ReadAllText(SettingsPath))!;
        var order = node["LyricsSecondaryLine"]?["Order"];
        return order is JsonArray arr
            ? string.Join(",", arr.Select(v => v?.GetValue<string>() ?? "<null>"))
            : (order is null ? "<missing>" : "null");
    }

    private static void Check(string what, int actual, int expected)
    {
        Console.WriteLine($"{what}: {actual} (expect {expected})");
        if (actual != expected) Fail($"{what}: {actual} != {expected}");
    }

    private static void Check(string what, string actual, string expected)
    {
        Console.WriteLine($"{what}: {actual} (expect {expected})");
        if (actual != expected) Fail($"{what}: {actual} != {expected}");
    }

    /// <summary>控件改一下之后，磁盘上的布尔字段必须已经跟着变。/ A bool must already be on disk after the control changed.</summary>
    private static void CheckDiskBool(string jsonName, bool expected)
    {
        var actual = ReadDiskNode(jsonName);
        var value = actual is JsonValue v && v.TryGetValue<bool>(out var b) ? b : (bool?)null;
        Console.WriteLine($"{jsonName} -> disk={actual?.ToJsonString() ?? "<missing>"} (expect {expected})");
        if (value != expected) Fail($"{jsonName}: {actual?.ToJsonString() ?? "<missing>"} != {expected}");
    }

    /// <summary>磁盘上的数值字段必须等于期望值（允许 1e-6 误差，JSON 里可能是整数或小数）。/ A numeric setting on disk must match, within 1e-6.</summary>
    private static void CheckDiskNumber(string jsonName, double expected)
    {
        var actual = ReadDiskNode(jsonName);
        var value = actual is JsonValue v && v.TryGetValue<double>(out var d) ? d : (double?)null;
        Console.WriteLine($"{jsonName} -> disk={actual?.ToJsonString() ?? "<missing>"} (expect {expected})");
        if (value is null || Math.Abs(value.Value - expected) > 1e-6)
            Fail($"{jsonName}: {actual?.ToJsonString() ?? "<missing>"} != {expected}");
    }

    /// <summary>磁盘上的字符串字段必须等于期望值。/ A string setting on disk must match exactly.</summary>
    private static void CheckDiskString(string jsonName, string expected)
    {
        var actual = ReadDiskNode(jsonName);
        var value = actual is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        Console.WriteLine($"{jsonName} -> disk={actual?.ToJsonString() ?? "<missing>"} (expect {expected})");
        if (!string.Equals(value, expected, StringComparison.Ordinal))
            Fail($"{jsonName}: {actual?.ToJsonString() ?? "<missing>"} != {expected}");
    }

    private static JsonNode? ReadDiskNode(string name) => JsonNode.Parse(File.ReadAllText(SettingsPath))?[name];

    private static void Fail(string message)
    {
        Console.WriteLine("  FAIL: " + message);
        _fail++;
    }
}
