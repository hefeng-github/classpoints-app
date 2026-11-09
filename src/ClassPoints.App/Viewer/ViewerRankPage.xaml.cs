using ClassPoints.App.Services;
using ClassPoints.App.Theme;
using ClassPoints.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ClassPoints.App.Viewer;

/// <summary>
/// 只读排名页。两种口径：本周期（最近一次重置以来）和学期累计（总评用这个）。
/// 不存在"改分"的入口，连行都不可点 —— 查询机不该有可点的东西。
/// </summary>
public sealed partial class ViewerRankPage : Page
{
    private bool _byTerm;

    public ViewerRankPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Reload();
    }

    private static AppServices Services => App.Services;

    private ViewerShell? Shell => App.Viewer;

    private void OnSortPeriod(object sender, RoutedEventArgs e)
    {
        _byTerm = false;
        Reload();
    }

    private void OnSortTerm(object sender, RoutedEventArgs e)
    {
        _byTerm = true;
        Reload();
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => Reload();

    private void Reload()
    {
        try
        {
            var period = Services.CurrentPeriod;
            var students = Services.Students.ListActive();
            var periodTotals = Services.Entries.PeriodTotals(period.Id);
            var termTotals = Services.Entries.TermTotals(period.TermKey);
            var events = Services.Counters.ClassTotalsByStudent(period.Id);

            var filter = SearchBox.Text.Trim();
            if (filter.Length > 0)
                students = students.Where(s => s.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

            var points = _byTerm ? termTotals : periodTotals;
            var ordered = students
                .OrderByDescending(s => points.GetValueOrDefault(s.Id))
                .ThenBy(s => s.SeatNo ?? int.MaxValue)
                .ThenBy(s => s.Name, StringComparer.CurrentCulture)
                .ToList();

            PeriodButton.Style = (Style)Application.Current.Resources[_byTerm ? "DeskButton" : "DeskPrimaryButton"];
            TermButton.Style = (Style)Application.Current.Resources[_byTerm ? "DeskPrimaryButton" : "DeskButton"];

            SubText.Text = _byTerm
                ? $"{period.Name} · 学期累计口径，总评用这个 · 共 {ordered.Count} 人"
                : $"{period.Name} · {period.RangeText} · 共 {ordered.Count} 人";

            RowList.Children.Clear();

            var rank = 0;
            var previous = int.MinValue;
            for (var i = 0; i < ordered.Count; i++)
            {
                var student = ordered[i];
                var value = points.GetValueOrDefault(student.Id);
                if (value != previous)
                {
                    rank = i + 1;
                    previous = value;
                }

                RowList.Children.Add(BuildRow(rank, student, value, termTotals.GetValueOrDefault(student.Id),
                    events.GetValueOrDefault(student.Id)));
            }

            if (ordered.Count == 0)
                RowList.Children.Add(new TextBlock
                {
                    Text = "没有匹配的学生。",
                    Style = (Style)Application.Current.Resources["DeskSecondaryText"],
                    Margin = new Thickness(16, 20, 16, 20),
                });
        }
        catch (Exception ex)
        {
            Services.LogError("viewer-rank", ex);
            Shell?.Toast("排名读取失败：" + ex.Message, 6);
        }
    }

    private FrameworkElement BuildRow(int rank, Student student, int periodPoints, int termPoints, int eventCount)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });

        var rankText = new TextBlock
        {
            Text = rank.ToString(),
            FontFamily = (FontFamily)Application.Current.Resources["NumberFont"],
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = rank <= 3 ? Pal.Accent : Pal.Secondary,
            VerticalAlignment = VerticalAlignment.Center,
        };
        grid.Children.Add(rankText);

        var name = new TextBlock
        {
            Text = student.Name,
            Style = (Style)Application.Current.Resources["DeskBodyText"],
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);

        var seat = new TextBlock
        {
            Text = student.SeatNo?.ToString() ?? "—",
            Style = (Style)Application.Current.Resources["DeskSecondaryText"],
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(seat, 2);
        grid.Children.Add(seat);

        grid.Children.Add(Number(periodPoints, 3));
        grid.Children.Add(Number(termPoints, 4));
        grid.Children.Add(Number(eventCount, 5));

        return new Border
        {
            BorderBrush = Pal.Stroke,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(14, 10, 14, 10),
            Child = grid,
        };
    }

    private static TextBlock Number(int value, int column)
    {
        var text = new TextBlock
        {
            Text = value.ToString(),
            FontFamily = (FontFamily)Application.Current.Resources["NumberFont"],
            FontSize = 15,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = value >= 0 ? Pal.Primary : Pal.Negative,
        };
        Grid.SetColumn(text, column);
        return text;
    }
}
