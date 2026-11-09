using ClassPoints.App.Services;
using ClassPoints.App.Theme;
using ClassPoints.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ClassPoints.App.Viewer;

/// <summary>
/// 只读查询页。学生自己来查的时候，屏幕上只有"我这次多少分、为什么"，没有按钮。
/// 分数不排名次（名次在「排名」页），因为站在旁边的人会看到。
/// </summary>
public sealed partial class ViewerLookupPage : Page
{
    private List<Student> _students = [];
    private Student? _selected;

    public ViewerLookupPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Reload();
    }

    private static AppServices Services => App.Services;

    private ViewerShell? Shell => App.Viewer;

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => Reload();

    private void Reload()
    {
        try
        {
            var period = Services.CurrentPeriod;
            var totals = Services.Entries.PeriodTotals(period.Id);

            var filter = SearchBox.Text.Trim();
            _students = Services.Students.ListActive()
                .Where(s => filter.Length == 0 ||
                            s.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                            (s.SeatNo?.ToString() ?? "") == filter)
                .ToList();

            CountText.Text = filter.Length == 0
                ? $"共 {_students.Count} 人，按座位排"
                : $"找到 {_students.Count} 人";

            StudentList.Children.Clear();
            foreach (var student in _students)
            {
                var active = _selected?.Id == student.Id;
                var button = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Padding = new Thickness(14, 9, 14, 9),
                    CornerRadius = new CornerRadius(0),
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    BorderBrush = Pal.Stroke,
                    Background = active ? Pal.AccentBg : new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                    Tag = student,
                };

                var grid = new Grid { ColumnSpacing = 8 };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var left = new StackPanel { Spacing = 1 };
                left.Children.Add(new TextBlock
                {
                    Text = student.Name,
                    Style = (Style)Application.Current.Resources["DeskBodyText"],
                    Foreground = active ? Pal.Accent : Pal.Primary,
                });
                left.Children.Add(new TextBlock
                {
                    Text = student.SeatNo is int n ? $"{n} 号" : "未排座",
                    Style = (Style)Application.Current.Resources["DeskSecondaryText"],
                });
                grid.Children.Add(left);

                var points = new TextBlock
                {
                    Text = WithSign(totals.GetValueOrDefault(student.Id)),
                    FontFamily = (FontFamily)Application.Current.Resources["NumberFont"],
                    FontSize = 16,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = active ? Pal.Accent : Pal.Secondary,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(points, 1);
                grid.Children.Add(points);

                button.Content = grid;
                button.Click += (_, _) =>
                {
                    _selected = student;
                    Reload();
                };
                StudentList.Children.Add(button);
            }

            if (_selected is not null) RenderDetail(_selected, period, totals);
            else if (_students.Count > 0 && filter.Length > 0)
            {
                _selected = _students[0];
                RenderDetail(_selected, period, totals);
            }
        }
        catch (Exception ex)
        {
            Services.LogError("viewer-lookup", ex);
            Shell?.Toast("查询失败：" + ex.Message, 6);
        }
    }

    private void RenderDetail(Student student, Period period, Dictionary<string, int> totals)
    {
        Hint.Visibility = Visibility.Collapsed;
        DetailScroll.Visibility = Visibility.Visible;

        NameText.Text = student.Name;
        SubText.Text = $"{student.SeatText} · {period.Name} · {period.RangeText}";

        PeriodPoints.Text = WithSign(Services.Entries.PeriodTotals(period.Id).GetValueOrDefault(student.Id));
        TermPoints.Text = WithSign(Services.Entries.TermTotals(period.TermKey).GetValueOrDefault(student.Id));

        var counts = Services.Counters.CountsForStudent(period.Id, student.Id);
        var eventTotal = counts.Values.Sum();
        EventText.Text = eventTotal.ToString();

        // ---- 事件 ----
        EventList.Children.Clear();
        var counters = Services.Counters.ListAll(includeArchived: true)
            .Where(c => counts.GetValueOrDefault(c.Id) != 0)
            .ToList();

        if (counters.Count == 0)
            EventList.Children.Add(new TextBlock
            {
                Text = "本周期没有登记事件。",
                Style = (Style)Application.Current.Resources["DeskSecondaryText"],
            });

        foreach (var counter in counters)
        {
            var chip = new Border
            {
                Background = Pal.TileBg,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 6, 12, 6),
            };

            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            row.Children.Add(new FontIcon
            {
                Glyph = counter.Glyph,
                FontFamily = new FontFamily(IconCatalog.FontFamily),
                FontSize = 13,
                Foreground = Pal.Secondary,
                VerticalAlignment = VerticalAlignment.Center,
            });
            row.Children.Add(new TextBlock
            {
                Text = $"{counter.Label} {counts[counter.Id]} 次",
                Style = (Style)Application.Current.Resources["DeskBodyText"],
            });
            chip.Child = row;
            EventList.Children.Add(chip);
        }

        // ---- 积分明细 ----
        EntryList.Children.Clear();
        var entries = Services.Entries.EntriesForStudent(student.Id, 12);
        if (entries.Count == 0)
        {
            EntryList.Children.Add(new TextBlock
            {
                Text = "还没有积分记录。",
                Style = (Style)Application.Current.Resources["DeskSecondaryText"],
            });
        }

        foreach (var entry in entries)
        {
            var row = new Grid { ColumnSpacing = 10, Padding = new Thickness(0, 7, 0, 7) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });

            var time = new TextBlock
            {
                Text = entry.TimeText,
                Style = (Style)Application.Current.Resources["DeskSecondaryText"],
            };
            row.Children.Add(time);

            var middle = new StackPanel { Spacing = 1 };
            middle.Children.Add(new TextBlock
            {
                Text = entry.RuleLabel ?? (entry.IsReversal ? "撤销" : "手动调整"),
                Style = (Style)Application.Current.Resources["DeskBodyText"],
            });
            var suffix = string.IsNullOrWhiteSpace(entry.Note) ? entry.DetailSuffix : entry.Note!;
            if (!string.IsNullOrWhiteSpace(suffix))
                middle.Children.Add(new TextBlock
                {
                    Text = suffix,
                    Style = (Style)Application.Current.Resources["DeskSecondaryText"],
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
            Grid.SetColumn(middle, 1);
            row.Children.Add(middle);

            var delta = new TextBlock
            {
                Text = entry.DeltaText,
                FontFamily = (FontFamily)Application.Current.Resources["NumberFont"],
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Right,
                Foreground = entry.Delta >= 0 ? Pal.Positive : Pal.Negative,
                Opacity = entry.IsReversed ? 0.45 : 1,
            };
            Grid.SetColumn(delta, 2);
            row.Children.Add(delta);

            EntryList.Children.Add(row);
            EntryList.Children.Add(new Border { Height = 1, Background = Pal.Stroke });
        }
    }

    private static string WithSign(int value) => (value > 0 ? "+" : "") + value;
}
