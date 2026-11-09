using Windows.UI;
using ClassPoints.App.Services;
using ClassPoints.Core;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using ClassPoints.App.Theme;

namespace ClassPoints.App.Board;

/// <summary>
/// 看板：无人操作时的停留页。深色底、前 15 名 + 最近 8 条动态、20 秒个人榜/小组榜切换。
/// 常驻界面不显示任何按钮，轻触才浮出两个出口。
/// </summary>
public sealed partial class BoardWallPage : Page
{
    private static readonly SolidColorBrush Primary = Pal.Primary;
    private static readonly SolidColorBrush Secondary = Pal.Secondary;
    private static readonly SolidColorBrush Positive = Pal.Positive;
    private static readonly SolidColorBrush Negative = Pal.Negative;
    private static readonly SolidColorBrush Accent = Pal.Accent;

    private readonly DispatcherTimer _rotate = new();
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(15) };
    private bool _showingGroups;

    public BoardWallPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        PointerPressed += OnPagePointerPressed;
    }

    private AppServices Services => AppServices.Instance;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ClassText.Text = Services.ClassName;
        PeriodText.Text = Services.PeriodName;

        _rotate.Interval = TimeSpan.FromSeconds(Services.Settings.BoardRotateSec);
        _rotate.Tick += OnRotateTick;
        _rotate.Start();

        _refresh.Tick += (_, _) => Render();
        _refresh.Start();

        Render();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _rotate.Stop();
        _refresh.Stop();
    }

    private void OnRotateTick(object? sender, object e)
    {
        try
        {
            if (ActionLayer.Visibility == Visibility.Visible) return;
            _showingGroups = !_showingGroups;
            FadeSwap();
        }
        catch (Exception ex)
        {
            App.Services.LogError("wall-rotate", ex);
        }
    }

    private void FadeSwap()
    {
        // 同一个 Storyboard 不能对同一元素的同一属性挂两个动画，所以拆成两段接力
        var fadeOut = new Storyboard();
        var outAnim = new DoubleAnimation
        {
            From = 1, To = 0, Duration = new Duration(TimeSpan.FromMilliseconds(180)),
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(outAnim, RankPanel);
        Storyboard.SetTargetProperty(outAnim, "Opacity");
        fadeOut.Children.Add(outAnim);

        fadeOut.Completed += (_, _) =>
        {
            try
            {
                Render();
                var fadeIn = new Storyboard();
                var inAnim = new DoubleAnimation
                {
                    From = 0, To = 1, Duration = new Duration(TimeSpan.FromMilliseconds(220)),
                    EnableDependentAnimation = true,
                };
                Storyboard.SetTarget(inAnim, RankPanel);
                Storyboard.SetTargetProperty(inAnim, "Opacity");
                fadeIn.Children.Add(inAnim);
                fadeIn.Begin();
            }
            catch (Exception ex)
            {
                App.Services.LogError("wall-fade", ex);
            }
        };

        fadeOut.Begin();
    }

    public void Render()
    {
        var period = Services.CurrentPeriod;
        var students = Services.Students.ListActive();
        if (students.Count == 0)
        {
            EmptyText.Visibility = Visibility.Visible;
            RankPanel.Children.Clear();
            FeedPanel.Children.Clear();
            return;
        }
        EmptyText.Visibility = Visibility.Collapsed;

        var totals = Services.Entries.PeriodTotals(period.Id);
        BoardModeText.Text = _showingGroups ? "小组榜" : "个人榜";
        ListHeader.Text = _showingGroups ? "小组积分" : "积分榜";
        ListSub.Text = _showingGroups
            ? "按小组累计"
            : $"共 {students.Count} 人";

        if (_showingGroups) RenderGroups(period.Id);
        else RenderIndividuals(students, totals);

        RenderFeed(period.Id);
    }

    private void RenderIndividuals(IReadOnlyList<Student> students, Dictionary<string, int> totals)
    {
        RankPanel.Children.Clear();
        var ordered = students
            .Select(s => (Student: s, Points: totals.TryGetValue(s.Id, out var p) ? p : 0))
            .OrderByDescending(x => x.Points)
            .ThenBy(x => x.Student.SeatNo ?? int.MaxValue)
            .Take(15)
            .ToList();

        var rank = 0;
        var lastPoints = int.MinValue;
        for (var i = 0; i < ordered.Count; i++)
        {
            var (student, points) = ordered[i];
            if (points != lastPoints)
            {
                rank = i + 1;
                lastPoints = points;
            }
            RankPanel.Children.Add(BuildRow(rank, student.Name, points.ToString(), points >= 0 ? Primary : Negative,
                student.SeatNo is int n ? $"{n} 号" : ""));
        }
    }

    private void RenderGroups(string periodId)
    {
        RankPanel.Children.Clear();
        var groups = Services.Entries.GroupTotals(periodId);
        if (groups.Count == 0)
        {
            RankPanel.Children.Add(new TextBlock
            {
                Text = "还没有分组。到电脑端「名单」填小组名。",
                Foreground = Secondary,
                FontSize = 18,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(6, 12, 6, 0),
            });
            return;
        }

        var rank = 0;
        var last = int.MinValue;
        for (var i = 0; i < groups.Count && i < 15; i++)
        {
            var (group, points, members) = groups[i];
            if (points != last)
            {
                rank = i + 1;
                last = points;
            }
            RankPanel.Children.Add(BuildRow(rank, group, points.ToString(), points >= 0 ? Primary : Negative,
                $"{members} 人"));
        }
    }

    private FrameworkElement BuildRow(int rank, string name, string points, Brush pointsBrush, string sub)
    {
        var grid = new Grid { Height = 46, Padding = new Thickness(4, 0, 4, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var rankText = new TextBlock
        {
            Text = rank.ToString(),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 22,
            FontWeight = rank <= 3 ? FontWeights.Bold : FontWeights.Normal,
            Foreground = rank <= 3 ? Accent : Secondary,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(rankText, 0);

        var namePanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Orientation = Orientation.Horizontal, Spacing = 8 };
        namePanel.Children.Add(new TextBlock
        {
            Text = name,
            FontSize = 22,
            FontWeight = rank <= 3 ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = Primary,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        if (!string.IsNullOrEmpty(sub))
            namePanel.Children.Add(new TextBlock
            {
                Text = sub,
                FontSize = 13,
                Foreground = Secondary,
                VerticalAlignment = VerticalAlignment.Center,
            });
        Grid.SetColumn(namePanel, 1);

        var pointsText = new TextBlock
        {
            Text = points,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 24,
            FontWeight = FontWeights.SemiBold,
            Foreground = pointsBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(pointsText, 2);

        grid.Children.Add(rankText);
        grid.Children.Add(namePanel);
        grid.Children.Add(pointsText);
        return grid;
    }

    private void RenderFeed(string periodId)
    {
        FeedPanel.Children.Clear();
        var entries = Services.Entries.Entries(periodId, limit: 8);
        if (entries.Count == 0)
        {
            FeedPanel.Children.Add(new TextBlock
            {
                Text = "这个周期还没有记录。\n加的第一条分会出现在这里。",
                Foreground = Secondary,
                FontSize = 17,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(4, 10, 4, 0),
                LineHeight = 26,
            });
            return;
        }

        foreach (var entry in entries)
        {
            var panel = new StackPanel { Spacing = 2, Margin = new Thickness(4, 4, 4, 6) };
            var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            top.Children.Add(new TextBlock
            {
                Text = Clock.ToLocal(entry.OccurredAt).ToString("HH:mm"),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 14,
                Foreground = Secondary,
                VerticalAlignment = VerticalAlignment.Center,
            });
            top.Children.Add(new TextBlock
            {
                Text = entry.StudentName,
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Foreground = Primary,
            });
            panel.Children.Add(top);

            var bottom = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(46, 0, 0, 0) };
            bottom.Children.Add(new TextBlock
            {
                Text = entry.RuleLabel ?? "",
                FontSize = 15,
                Foreground = Secondary,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 220,
            });
            bottom.Children.Add(new TextBlock
            {
                Text = entry.DeltaText,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 17,
                FontWeight = FontWeights.SemiBold,
                Foreground = entry.Delta >= 0 ? Positive : Negative,
            });
            panel.Children.Add(bottom);

            FeedPanel.Children.Add(panel);
        }
    }

    // ---------------- 轻触 ----------------

    private void OnScrimTapped(object sender, TappedRoutedEventArgs e) => HideActions();

    private void HideActions() => ActionLayer.Visibility = Visibility.Collapsed;

    private void OnLookupClick(object sender, RoutedEventArgs e)
    {
        HideActions();
        App.Board?.Navigate(BoardNav.Lookup);
    }

    private async void OnStartClick(object sender, RoutedEventArgs e)
    {
        HideActions();
        var shell = App.Board;
        if (shell is null) return;
        if (await shell.EnsureIdentifiedAsync()) shell.Navigate(BoardNav.Score);
    }

    private void OnPagePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (ActionLayer.Visibility == Visibility.Visible) return;
        ActionLayer.Visibility = Visibility.Visible;
        Services.Session.Touch();
    }
}
