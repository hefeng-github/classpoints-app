using ClassPoints.App.Services;
using ClassPoints.App.Theme;
using ClassPoints.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ClassPoints.App.Viewer;

/// <summary>
/// 只读事件统计。班主任和家长都能看，但右下角不会出现"改"。
/// </summary>
public sealed partial class ViewerCountersPage : Page
{
    private const double NameWidth = 160;
    private const double SeatWidth = 64;
    private const double CounterWidth = 132;
    private const double TotalWidth = 90;

    private List<Counter> _counters = [];

    public ViewerCountersPage()
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
            _counters = Services.Counters.ListAll(includeArchived: true);
            var counts = Services.Counters.Counts(period.Id);

            var students = Services.Students.ListActive();
            var filter = SearchBox.Text.Trim();
            if (filter.Length > 0)
                students = students.Where(s => s.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

            var total = counts.Values.Sum();
            SubText.Text = $"{period.Name} · {period.RangeText} · 全班共 {total} 次 · 只统计本机记录";

            BuildHeader();
            BuildRows(students, counts);
            EmptyText.Visibility = students.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            Services.LogError("viewer-counters", ex);
            Shell?.Toast("事件统计读取失败：" + ex.Message, 6);
        }
    }

    private void BuildHeader()
    {
        HeaderHost.Children.Clear();
        HeaderHost.ColumnDefinitions.Clear();

        HeaderHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(NameWidth) });
        HeaderHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(SeatWidth) });
        foreach (var _ in _counters)
            HeaderHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(CounterWidth) });
        HeaderHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(TotalWidth) });
        HeaderHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        HeaderHost.Children.Add(Header("姓名", 0, HorizontalAlignment.Left));
        HeaderHost.Children.Add(Header("座位", 1, HorizontalAlignment.Left));

        for (var i = 0; i < _counters.Count; i++)
        {
            var cell = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            cell.Children.Add(new FontIcon
            {
                Glyph = _counters[i].Glyph,
                FontFamily = new FontFamily(IconCatalog.FontFamily),
                FontSize = 12,
                Foreground = Pal.Secondary,
                VerticalAlignment = VerticalAlignment.Center,
            });
            cell.Children.Add(new TextBlock
            {
                Text = _counters[i].Label,
                Style = (Style)Application.Current.Resources["DeskColumnHeader"],
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = CounterWidth - 26,
            });
            Grid.SetColumn(cell, 2 + i);
            HeaderHost.Children.Add(cell);
        }

        HeaderHost.Children.Add(Header("合计", 2 + _counters.Count, HorizontalAlignment.Right));
    }

    private static TextBlock Header(string text, int column, HorizontalAlignment align)
    {
        var block = new TextBlock
        {
            Text = text,
            Style = (Style)Application.Current.Resources["DeskColumnHeader"],
            HorizontalAlignment = align,
        };
        Grid.SetColumn(block, column);
        return block;
    }

    private void BuildRows(List<Student> students, Dictionary<(string CounterId, string StudentId), int> counts)
    {
        RowList.Children.Clear();

        foreach (var student in students)
        {
            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(NameWidth) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(SeatWidth) });
            foreach (var _ in _counters)
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(CounterWidth) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(TotalWidth) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var name = new TextBlock
            {
                Text = student.Name,
                Style = (Style)Application.Current.Resources["DeskBodyText"],
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            row.Children.Add(name);

            var seat = new TextBlock
            {
                Text = student.SeatNo?.ToString() ?? "—",
                Style = (Style)Application.Current.Resources["DeskSecondaryText"],
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(seat, 1);
            row.Children.Add(seat);

            var total = 0;
            for (var i = 0; i < _counters.Count; i++)
            {
                var value = counts.GetValueOrDefault((_counters[i].Id, student.Id));
                total += value;

                var cell = Number(value, 2 + i, HorizontalAlignment.Right);
                cell.Foreground = value == 0 ? Pal.Secondary : Pal.Primary;
                row.Children.Add(cell);
            }

            var sum = Number(total, 2 + _counters.Count, HorizontalAlignment.Right);
            sum.FontWeight = FontWeights.SemiBold;
            row.Children.Add(sum);

            RowList.Children.Add(new Border
            {
                BorderBrush = Pal.Stroke,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(14, 9, 14, 9),
                Child = row,
            });
        }
    }

    private static TextBlock Number(int value, int column, HorizontalAlignment align)
    {
        var text = new TextBlock
        {
            Text = value.ToString(),
            FontFamily = (FontFamily)Application.Current.Resources["NumberFont"],
            FontSize = 15,
            HorizontalAlignment = align,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(text, column);
        return text;
    }
}
