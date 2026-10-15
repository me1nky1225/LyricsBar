// Settings-window load/save round-trip harness for the v1.4.1 dropdowns.
// Builds the real LyricsBar.Views.SettingsWindow, drives its combo boxes exactly like a user click,
// and checks both in-memory settings and the JSON written to %LOCALAPPDATA%\LyricsBar\settings.json.
// Restores the user's settings file at the end.
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
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

    private static void SetAndVerify(ComboBox combo, int index, string jsonName, string expected)
    {
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

    private static void Fail(string message)
    {
        Console.WriteLine("  FAIL: " + message);
        _fail++;
    }
}
