using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace ClassPoints.App.Theme;

/// <summary>外观偏好。System 表示跟随 Windows。</summary>
public enum AppTheme
{
    System = 0,
    Light = 1,
    Dark = 2,
}

/// <summary>
/// C# 里构建界面时用的调色板。
///
/// 关键点：这些 Brush 实例是**全局共享**的，切换主题只改它们的 Color。
/// SolidColorBrush 的 Color 是依赖属性，改一次所有引用它的元素立刻重画，
/// 所以不需要为了换主题把页面重建一遍（重建会打断正在进行的动画和输入焦点）。
///
/// XAML 那边走的是另一条路：Palette.xaml 里的 ThemeDictionaries 配合根元素
/// 的 RequestedTheme，ThemeResource 会自动重解析。两条路最终指向同一组色值，
/// 改色时务必两边一起改。
/// </summary>
public static class Pal
{
    public static SolidColorBrush Bg { get; } = new();
    public static SolidColorBrush Card { get; } = new();
    public static SolidColorBrush Stroke { get; } = new();
    public static SolidColorBrush StrokeStrong { get; } = new();
    public static SolidColorBrush Accent { get; } = new();
    public static SolidColorBrush AccentBg { get; } = new();
    public static SolidColorBrush TileBg { get; } = new();
    public static SolidColorBrush Primary { get; } = new();
    public static SolidColorBrush Secondary { get; } = new();
    public static SolidColorBrush Positive { get; } = new();
    public static SolidColorBrush Negative { get; } = new();
    public static SolidColorBrush PositiveBg { get; } = new();
    public static SolidColorBrush NegativeBg { get; } = new();
    public static SolidColorBrush Warn { get; } = new();
    public static SolidColorBrush Danger { get; } = new();
    public static SolidColorBrush RowAlt { get; } = new();
    public static SolidColorBrush OnAccent { get; } = new();
    public static SolidColorBrush FloatingBg { get; } = new();
    public static SolidColorBrush AccentTint { get; } = new();
    public static SolidColorBrush Scrim { get; } = new();

    /// <summary>当前是否为深色。界面里少数地方要按明暗做不同处理（比如标题栏按钮颜色）。</summary>
    public static bool IsDark { get; private set; }

    public static void Apply(AppTheme theme)
    {
        IsDark = theme switch
        {
            AppTheme.Dark => true,
            AppTheme.Light => false,
            _ => Application.Current.RequestedTheme == ApplicationTheme.Dark,
        };

        if (IsDark)
        {
            Set(Bg, 0x12, 0x16, 0x1C);
            Set(Card, 0x1C, 0x22, 0x2B);
            Set(Stroke, 0x2A, 0x33, 0x41);
            Set(StrokeStrong, 0x3A, 0x43, 0x50);
            Set(Accent, 0x4C, 0xC2, 0xFF);
            Set(AccentBg, 0x1D, 0x3B, 0x52);
            Set(TileBg, 0x24, 0x2C, 0x37);
            Set(Primary, 0xF2, 0xF5, 0xF9);
            Set(Secondary, 0x9A, 0xA7, 0xB8);
            Set(Positive, 0x34, 0xD3, 0x99);
            Set(Negative, 0xF8, 0x71, 0x71);
            Set(PositiveBg, 0x16, 0x35, 0x2B);
            Set(NegativeBg, 0x3A, 0x1D, 0x14);
            Set(Warn, 0xFB, 0xBF, 0x24);
            Set(Danger, 0xEF, 0x44, 0x44);
            Set(RowAlt, 0x17, 0x1C, 0x24);
            Set(OnAccent, 0x0B, 0x10, 0x16);
            Set(FloatingBg, 0x1C, 0x22, 0x2B, 0xF2);
            Set(AccentTint, 0x4C, 0xC2, 0xFF, 0x33);
            Set(Scrim, 0x0B, 0x0E, 0x13, 0xCC);
        }
        else
        {
            Set(Bg, 0xF4, 0xF6, 0xF9);
            Set(Card, 0xFF, 0xFF, 0xFF);
            Set(Stroke, 0xD9, 0xE0, 0xE9);
            Set(StrokeStrong, 0xC3, 0xCD, 0xDA);
            Set(Accent, 0x0F, 0x6F, 0xC4);
            Set(AccentBg, 0xE3, 0xF0, 0xFC);
            Set(TileBg, 0xF1, 0xF4, 0xF8);
            Set(Primary, 0x12, 0x16, 0x1D);
            Set(Secondary, 0x5B, 0x66, 0x75);
            Set(Positive, 0x0B, 0x8A, 0x5F);
            Set(Negative, 0xC6, 0x28, 0x28);
            Set(PositiveBg, 0xE4, 0xF5, 0xEC);
            Set(NegativeBg, 0xFB, 0xEA, 0xEA);
            Set(Warn, 0xA5, 0x60, 0x0A);
            Set(Danger, 0xC6, 0x28, 0x28);
            Set(RowAlt, 0xF7, 0xF9, 0xFC);
            Set(OnAccent, 0xFF, 0xFF, 0xFF);
            Set(FloatingBg, 0xFF, 0xFF, 0xFF, 0xFB);
            Set(AccentTint, 0x0F, 0x6F, 0xC4, 0x2E);
            Set(Scrim, 0x14, 0x1A, 0x22, 0xA6);
        }
    }

    private static void Set(SolidColorBrush brush, byte r, byte g, byte b, byte a = 0xFF)
        => brush.Color = Color.FromArgb(a, r, g, b);
}
