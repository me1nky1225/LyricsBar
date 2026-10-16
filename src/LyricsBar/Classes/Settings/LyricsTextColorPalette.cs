namespace LyricsBar.Classes.Settings;

/// <summary>一个文字颜色预设：显示名 + <c>#RRGGBB</c> 值。/ One text-colour preset: a display name plus a <c>#RRGGBB</c> value.</summary>
/// <param name="Name">下拉框里的名字。/ Name shown in the list.</param>
/// <param name="Hex">写回 <c>LyricsTextColor</c> 的颜色值。/ Colour value written back into <c>LyricsTextColor</c>.</param>
public sealed record LyricsColorPreset(string Name, string Hex);

/// <summary>
/// 文字颜色预设。
///
/// 挑的都是"压在任何壁纸上都还能读"的浅色：任务栏歌词栏本身半透明，颜色太饱和或者太暗会跟壁纸糊在一起。
/// 预设只是快捷方式，颜色输入框仍然可以手输 <c>#RRGGBB</c> 或颜色名，所以预设不涵盖的颜色一样能用。
/// Text-colour presets.
///
/// They are all light values that stay legible over any wallpaper: the bar itself is translucent, so a saturated or dark colour
/// muddles into the background. A preset is only a shortcut — the colour box still accepts a typed <c>#RRGGBB</c> or a colour
/// name, so anything outside this list remains available.
/// </summary>
public static class LyricsTextColorPalette
{
    /// <summary>预设颜色，顺序即界面上的排列顺序。/ Preset colours, in the order they appear in the UI.</summary>
    public static IReadOnlyList<LyricsColorPreset> Presets { get; } =
    [
        new("纯白", "#FFFFFF"),
        new("暖白", "#FFF3E0"),
        new("淡金", "#FFE082"),
        new("天蓝", "#90CAF9"),
        new("湖绿", "#80CBC4"),
        new("樱粉", "#F8BBD0"),
        new("浅紫", "#CE93D8"),
        new("浅灰", "#E0E0E0"),
        new("深灰", "#9E9E9E")
    ];
}
