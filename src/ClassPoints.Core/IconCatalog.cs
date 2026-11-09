namespace ClassPoints.Core;

/// <summary>
/// 图标一律取自 Windows 自带的 Segoe MDL2 Assets 字体，不用 emoji。
///
/// 三条理由，都是踩过的：
/// 1) emoji 的最终字形由系统字体回退链决定，教室一体机（Win10 + 厂商镜像）和
///    办公室电脑（Win11）上同一个码点长得不一样，截图对不上版；
/// 2) 彩色字形和这套单色界面撞色，浅色主题下尤其脏；
/// 3) 字号一变，emoji 的基线会漂，和旁边的中文对不齐。
///
/// 数据库 rules.icon / counters.icon 里存的就是这个字形本身（一个私用区字符），
/// 所以换图标集只要改这里和 Normalize 的映射，界面一处都不用动。
/// </summary>
public static class IconCatalog
{
    public const string FontFamily = "Segoe MDL2 Assets";

    // 私用区码点，只在 MDL2 里取。名字是给代码读的，不是给字体读的。
    public const string Star = "\uE735";
    public const string Message = "\uE8BD";
    public const string Reading = "\uE82D";
    public const string Edit = "\uE70F";
    public const string Check = "\uE73E";
    public const string Flag = "\uE7C1";
    public const string Bolt = "\uE945";
    public const string Bulb = "\uE82F";
    public const string Document = "\uE8A5";
    public const string Calculator = "\uE8EF";
    public const string Warning = "\uE7BA";
    public const string Clock = "\uE823";

    public const string Default = Star;

    /// <summary>规则图标候选。顺序就是设置页里出现的顺序。</summary>
    public static readonly IReadOnlyList<string> RuleIcons =
    [
        Star, Message, Reading, Edit, Check, Flag, Bolt, Bulb, Document, Calculator,
    ];

    /// <summary>事件图标候选。都是"负向但不好看"的类型，所以偏标记类。</summary>
    public static readonly IReadOnlyList<string> CounterIcons =
    [
        Flag, Clock, Document, Warning, Bolt, Check, Star,
    ];

    /// <summary>旧版本存下来的 emoji。只用于一次性迁移，之后不会再写进库。</summary>
    private static readonly Dictionary<string, string> Legacy = new()
    {
        ["⭐"] = Star,
        ["🌟"] = Star,
        ["💬"] = Message,
        ["📖"] = Reading,
        ["🖊"] = Edit,
        ["🖊️"] = Edit,
        ["✏"] = Edit,
        ["✏️"] = Edit,
        ["✅"] = Check,
        ["✔"] = Check,
        ["🚩"] = Flag,
        ["⚡"] = Bolt,
        ["💡"] = Bulb,
        ["📄"] = Document,
        ["📝"] = Document,
        ["🧮"] = Calculator,
        ["⚠"] = Warning,
        ["⚠️"] = Warning,
        ["⏰"] = Clock,
        ["🕒"] = Clock,
    };

    /// <summary>把一个可能来自旧版本的值收敛成合法字形。空值/认不出的都给默认星标。</summary>
    public static string Normalize(string? icon)
    {
        if (string.IsNullOrEmpty(icon)) return Default;
        if (Legacy.TryGetValue(icon, out var mapped)) return mapped;
        return IsGlyph(icon) ? icon : Default;
    }

    /// <summary>是不是私用区里的单个字形（也就是这套图标认识的东西）。</summary>
    public static bool IsGlyph(string? icon) =>
        icon is { Length: 1 } && icon[0] is >= '\uE000' and <= '\uF8FF';

    /// <summary>给提示文字用的中文名，认不出就返回空串。</summary>
    public static string? Describe(string? icon) => Normalize(icon) switch
    {
        Star => "星标",
        Message => "发言",
        Reading => "朗读",
        Edit => "书写",
        Check => "完成",
        Flag => "标记",
        Bolt => "闪电",
        Bulb => "点子",
        Document => "文档",
        Calculator => "计算",
        Warning => "提醒",
        Clock => "时间",
        _ => null,
    };
}
