namespace LyricsBar.Classes.Settings;

/// <summary>
/// 字体下拉里给出的候选字体。
///
/// 这里只列"几乎每台 Windows 都装、且中文歌词能正常显示"的几款：之前是把系统里所有字体族一股脑塞进下拉，
/// 打开先看到几百个图标字体与语言包字体，真正要用的那几个反而要翻很久。可编辑下拉仍然保留，想用清单以外的字体
/// 直接输名字即可，所以这个清单只是"常用项靠前"，不是白名单。
/// The candidates offered in the font drop-down.
///
/// Only the handful that ship with essentially every Windows install and render Chinese lyrics properly are listed: the list
/// used to be every installed family, so opening it showed hundreds of icon and language-pack faces while the few usable ones
/// took scrolling to find. The drop-down stays editable, so a family outside this list can still be typed in — the list puts
/// the common ones first, it is not a whitelist.
/// </summary>
public static class LyricsFontCatalog
{
    /// <summary>下拉里"跟随系统"那一项的显示名（保存时仍然写回 null）。字符串与旧设置窗口里的私有常量一字不差，老设置文件与老截图里的显示名都不变。/ Label of the "follow the system" entry; it still saves back as null. The text is character-for-character the private constant the settings window used to hold, so old settings files and old screenshots keep the same wording.</summary>
    public const string SystemDefaultLabel = "系统默认";

    /// <summary>候选字体族名，顺序即下拉顺序。/ Candidate family names, in drop-down order.</summary>
    public static IReadOnlyList<string> Candidates { get; } =
    [
        "Microsoft YaHei UI",
        "Microsoft YaHei",
        "微软雅黑",
        "DengXian",
        "等线",
        "SimSun",
        "宋体",
        "KaiTi",
        "楷体",
        "SimHei",
        "黑体",
        "Segoe UI",
        "Arial",
        "Times New Roman",
        "Consolas"
    ];
}
