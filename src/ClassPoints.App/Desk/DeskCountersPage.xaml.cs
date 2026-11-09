using ClassPoints.App.Controls;
using ClassPoints.App.Services;
using ClassPoints.App.Theme;
using ClassPoints.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ClassPoints.App.Desk;

/// <summary>
/// 事件计数（电脑端）。一行一个学生，一列一个事件，格子里直接加减。
///
/// 这里刻意不做"编辑一条记录"：每次加减都是一条流水，改错了就再加减一次，
/// 历史永远对得上。真想改准确值，点数字输入总数，系统自己补一条差额。
/// </summary>
public sealed partial class DeskCountersPage : Page
{
    private const double NameWidth = 160;
    private const double SeatWidth = 56;
    private const double CounterWidth = 132;
    private const double TotalWidth = 84;

    private List<Counter> _counters = [];

    public DeskCountersPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    private static AppServices Services => App.Services;

    private DeskShell? Shell => App.Desk;

    private void OnRefresh(object sender, RoutedEventArgs e) => Refresh();

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => Refresh();

    // ---------------- 渲染 ----------------

    private void Refresh()
    {
        try
        {
            var period = Services.CurrentPeriod;
            _counters = Services.Counters.ListAll();
            var students = Services.Students.ListActive();
            var counts = Services.Counters.Counts(period.Id);

            var filter = SearchBox.Text.Trim();
            if (filter.Length > 0)
                students = students
                    .Where(s => s.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                                (s.SeatNo?.ToString() ?? "") == filter)
                    .ToList();

            // 事件流水还没接同步（worker 侧只放行固定几张表）。这句必须写出来：
            // 不写的话，老师在办公室这台机器上看到一片 0，会以为教室里记的事件丢了。
            SubText.Text = $"{period.Name} · {period.RangeText} · 只数次数不加分 · 事件只记在本机，暂不参与云端同步";

            var total = counts.Values.Sum();
            SummaryText.Text = _counters.Count == 0
                ? "还没有事件类型"
                : total == 0
                    ? "本周期还没有记录"
                    : $"本周期共 {total} 次";

            BuildHeader();
            BuildRows(students, counts);
            BuildRecent(period.Id, students.Count == 0);

            EmptyPanel.Visibility = _counters.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            Services.LogError("desk-counters", ex);
            Shell?.Toast("事件计数读取失败：" + ex.Message, 6);
        }
    }

    private void BuildHeader()
    {
        HeaderHost.Children.Clear();
        HeaderHost.ColumnDefinitions.Clear();

        AddColumn(NameWidth, GridUnitType.Pixel);
        AddColumn(SeatWidth, GridUnitType.Pixel);
        foreach (var _ in _counters) AddColumn(CounterWidth, GridUnitType.Pixel);
        AddColumn(TotalWidth, GridUnitType.Pixel);

        HeaderHost.Children.Add(Header("姓名", 0, HorizontalAlignment.Left));
        HeaderHost.Children.Add(Header("座位", 1, HorizontalAlignment.Left));

        for (var i = 0; i < _counters.Count; i++)
        {
            var cell = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            cell.Children.Add(new FontIcon
            {
                Glyph = _counters[i].Glyph,
                FontFamily = new FontFamily(IconCatalog.FontFamily),
                FontSize = 13,
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

        var totalHeader = Header("合计", 2 + _counters.Count, HorizontalAlignment.Right);
        HeaderHost.Children.Add(totalHeader);
    }

    private void AddColumn(double width, GridUnitType type) =>
        HeaderHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width, type) });

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

        if (students.Count == 0)
        {
            RowList.Children.Add(new TextBlock
            {
                Text = "没有匹配的学生。清空搜索再试。",
                Style = (Style)Application.Current.Resources["DeskSecondaryText"],
                Margin = new Thickness(16, 20, 16, 20),
            });
            return;
        }

        var columns = 3 + _counters.Count;

        foreach (var student in students)
        {
            var row = new Grid { ColumnSpacing = 10 };
            for (var i = 0; i < columns; i++)
                row.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = new GridLength(i switch
                    {
                        0 => NameWidth,
                        1 => SeatWidth,
                        _ when i == 2 + _counters.Count => TotalWidth,
                        _ => CounterWidth,
                    }, GridUnitType.Pixel),
                });

            var name = new TextBlock
            {
                Text = student.Name,
                Style = (Style)Application.Current.Resources["DeskBodyText"],
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(name, 0);
            row.Children.Add(name);

            var seat = new TextBlock
            {
                Text = student.SeatNo?.ToString() ?? "—",
                Style = (Style)Application.Current.Resources["DeskSecondaryText"],
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(seat, 1);
            row.Children.Add(seat);

            var rowTotal = 0;
            for (var i = 0; i < _counters.Count; i++)
            {
                var counter = _counters[i];
                var value = counts.GetValueOrDefault((counter.Id, student.Id));
                rowTotal += value;

                var cell = BuildCell(student, counter, value);
                Grid.SetColumn(cell, 2 + i);
                row.Children.Add(cell);
            }

            var totalText = new TextBlock
            {
                Text = rowTotal == 0 ? "0" : rowTotal.ToString(),
                FontFamily = (FontFamily)Application.Current.Resources["NumberFont"],
                FontSize = 15,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = rowTotal == 0 ? Pal.Secondary : Pal.Primary,
            };
            Grid.SetColumn(totalText, 2 + _counters.Count);
            row.Children.Add(totalText);

            var host = new Border
            {
                Style = (Style)Application.Current.Resources["DeskRowBorder"],
                Padding = new Thickness(12, 6, 12, 6),
                Child = row,
            };
            RowList.Children.Add(host);
        }
    }

    private FrameworkElement BuildCell(Student student, Counter counter, int value)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        panel.Children.Add(StepButton("\uE738", "减一次", value <= 0, () => Step(student, counter, -1)));

        var number = new Button
        {
            Content = new TextBlock
            {
                Text = value.ToString(),
                FontFamily = (FontFamily)Application.Current.Resources["NumberFont"],
                FontSize = 16,
                Foreground = value == 0 ? Pal.Secondary : Pal.Primary,
            },
            Width = 46,
            Height = 30,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        };
        ToolTipService.SetToolTip(number, "点数字直接改成指定次数");
        number.Click += async (_, _) => await EditCountAsync(student, counter);
        panel.Children.Add(number);

        panel.Children.Add(StepButton("\uE710", "加一次", false, () => Step(student, counter, 1)));
        return panel;
    }

    private static Button StepButton(string glyph, string tip, bool disabled, Action onClick)
    {
        var button = new Button
        {
            Width = 30,
            Height = 30,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            BorderBrush = Pal.Stroke,
            Background = Pal.Card,
            IsEnabled = !disabled,
            Content = new FontIcon
            {
                Glyph = glyph,
                FontFamily = new FontFamily(IconCatalog.FontFamily),
                FontSize = 14,
                Foreground = Pal.Secondary,
            },
        };
        ToolTipService.SetToolTip(button, tip);
        button.Click += (_, _) => onClick();
        return button;
    }

    private void Step(Student student, Counter counter, int value)
    {
        try
        {
            var period = Services.CurrentPeriod;
            var count = Services.Counters.AddTick(counter.Id, student.Id, period.Id, value,
                Services.DeskOperator);

            Refresh();
            Shell?.Toast($"{student.Name} · {counter.Label} {(value > 0 ? "+1" : "−1")} → {count} 次", 2);
        }
        catch (Exception ex)
        {
            Services.LogError("desk-counter-step", ex);
            Shell?.Toast("记不上：" + ex.Message, 6);
        }
    }

    private async Task EditCountAsync(Student student, Counter counter)
    {
        var period = Services.CurrentPeriod;
        var current = Services.Counters.Total(period.Id, student.Id, counter.Id);

        var box = new NumberBox
        {
            Value = current,
            Minimum = 0,
            Maximum = 999,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            Width = 180,
        };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"{student.Name} · {counter.Label}",
            Content = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"本周期现在是 {current} 次。改成多少就补一条差额流水，历史记录不会被删。",
                        TextWrapping = TextWrapping.Wrap,
                        Style = (Style)Application.Current.Resources["DeskSecondaryText"],
                    },
                    box,
                },
            },
            PrimaryButtonText = "改成这个数",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        if (double.IsNaN(box.Value)) return;

        Services.Counters.SetCount(counter.Id, student.Id, period.Id, (int)box.Value, Services.DeskOperator);
        Refresh();
        Shell?.Toast($"{student.Name} · {counter.Label} 已改为 {(int)box.Value} 次", 3);
    }

    private void BuildRecent(string periodId, bool filtered)
    {
        RecentList.Children.Clear();
        var rows = Services.Counters.Recent(periodId, 30);
        if (filtered) rows = rows.Take(12).ToList();

        if (rows.Count == 0)
        {
            RecentList.Children.Add(new TextBlock
            {
                Text = "还没有记录。",
                Style = (Style)Application.Current.Resources["DeskSecondaryText"],
                Margin = new Thickness(16, 0, 16, 0),
            });
            return;
        }

        foreach (var tick in rows)
        {
            var row = new Border
            {
                Padding = new Thickness(16, 7, 16, 7),
                BorderBrush = Pal.Stroke,
                BorderThickness = new Thickness(0, 0, 0, 1),
            };

            var grid = new Grid { ColumnSpacing = 8 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var text = new StackPanel { Spacing = 1 };
            text.Children.Add(new TextBlock
            {
                Text = $"{tick.StudentName} · {tick.CounterLabel}",
                Style = (Style)Application.Current.Resources["DeskBodyText"],
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            text.Children.Add(new TextBlock
            {
                Text = tick.TimeText + (string.IsNullOrWhiteSpace(tick.Note) ? "" : " · " + tick.Note),
                Style = (Style)Application.Current.Resources["DeskSecondaryText"],
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            grid.Children.Add(text);

            var value = new TextBlock
            {
                Text = tick.Value > 0 ? "+" + tick.Value : tick.Value.ToString(),
                FontFamily = (FontFamily)Application.Current.Resources["NumberFont"],
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = tick.Value > 0 ? Pal.Primary : Pal.Secondary,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(value, 1);
            grid.Children.Add(value);

            row.Child = grid;
            RecentList.Children.Add(row);
        }
    }

    // ---------------- 事件类型 ----------------

    private async void OnAddCounter(object sender, RoutedEventArgs e)
    {
        var name = new TextBox
        {
            Header = "事件名称",
            PlaceholderText = "例如：迟到、作业未交、仪容不合规",
            Width = 360,
        };

        var icon = IconCatalog.Flag;
        var picker = IconChooser.Build(IconCatalog.CounterIcons, icon, g => icon = g);

        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(name);
        content.Children.Add(new TextBlock
        {
            Text = "图标",
            Style = (Style)Application.Current.Resources["DeskColumnHeader"],
        });
        content.Children.Add(picker);
        content.Children.Add(new TextBlock
        {
            Text = "新增以后，大屏的「事件」页会立刻多一个按钮。",
            Style = (Style)Application.Current.Resources["DeskSecondaryText"],
            TextWrapping = TextWrapping.Wrap,
        });

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "新增事件",
            Content = content,
            PrimaryButtonText = "添加",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var label = NameNormalizer.Clean(name.Text);
        if (label.Length == 0)
        {
            Shell?.Toast("名称不能空", 4);
            return;
        }

        Services.Counters.Save(new Counter { Label = label, Icon = icon });
        Refresh();
        Shell?.Toast($"已新增「{label}」", 3);
    }

    private async void OnManageCounters(object sender, RoutedEventArgs e)
    {
        var counters = Services.Counters.ListAll(includeArchived: true);
        if (counters.Count == 0)
        {
            Shell?.Toast("还没有事件类型，先新增一个", 4);
            return;
        }

        var panel = new StackPanel { Spacing = 10 };
        var edits = new List<(Counter Counter, TextBox Box, CheckBox Stop)>();

        foreach (var counter in counters)
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var box = new TextBox { Text = counter.Label, Height = 34 };
            row.Children.Add(box);

            var stop = new CheckBox
            {
                Content = "停用",
                IsChecked = counter.IsArchived,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(stop, 1);
            row.Children.Add(stop);

            panel.Children.Add(row);
            edits.Add((counter, box, stop));
        }

        panel.Children.Add(new TextBlock
        {
            Text = "停用后新记录不再显示它，已有的次数仍然保留、仍然计入统计。",
            Style = (Style)Application.Current.Resources["DeskSecondaryText"],
            TextWrapping = TextWrapping.Wrap,
        });

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "管理事件",
            Content = new ScrollViewer { Content = panel, MaxHeight = 420 },
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        foreach (var (counter, box, stop) in edits)
        {
            var label = NameNormalizer.Clean(box.Text);
            var changed = label.Length > 0 && label != counter.Label;
            var archived = stop.IsChecked == true;
            var wasArchived = counter.IsArchived;

            if (!changed && archived == wasArchived) continue;

            if (changed)
            {
                counter.Label = label;
                Services.Counters.Save(counter);
            }
            if (archived != wasArchived)
                Services.Counters.Archive(counter.Id, archived);
        }

        Refresh();
        Shell?.Toast("已保存", 3);
    }
}
