using ClassPoints.Core;
using ClassPoints.App.Theme;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace ClassPoints.App.Controls;

/// <summary>
/// 图标选择：一排 ToggleButton，每个里面一个 Segoe MDL2 字形。
///
/// 不用 ComboBox 是因为字形要在下拉里以图标字体渲染，得挂 ItemTemplate，
/// 而这几处都是代码里临时拼的对话框 —— 一排按钮更短，也更适合手指点。
/// </summary>
public static class IconChooser
{
    public static StackPanel Build(IReadOnlyList<string> icons, string? current, Action<string> onPick)
    {
        var selected = IconCatalog.Normalize(current);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var buttons = new List<ToggleButton>();

        foreach (var glyph in icons)
        {
            var button = new ToggleButton
            {
                Width = 44,
                Height = 44,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(10),
                IsChecked = glyph == selected,
                Content = new FontIcon
                {
                    Glyph = glyph,
                    FontFamily = new FontFamily(IconCatalog.FontFamily),
                    FontSize = 20,
                },
                Tag = glyph,
            };

            button.Click += (_, _) =>
            {
                // 手写单选：ToggleButton 自己不会互斥
                foreach (var other in buttons)
                    if (!ReferenceEquals(other, button)) other.IsChecked = false;

                button.IsChecked = true;
                onPick(glyph);
            };

            ToolTipService.SetToolTip(button, IconCatalog.Describe(glyph) ?? "图标");
            buttons.Add(button);
            row.Children.Add(button);
        }

        foreach (var b in buttons) b.Foreground = Pal.Primary;
        return row;
    }
}
