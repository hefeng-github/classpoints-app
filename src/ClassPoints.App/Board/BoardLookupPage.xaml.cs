using ClassPoints.App.Controls;
using ClassPoints.App.Services;
using ClassPoints.Core;
using Windows.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using ClassPoints.App.Theme;

namespace ClassPoints.App.Board;

/// <summary>
/// 查分：学生自助，一个人看。
/// 网格不显示分数（否则它就成了第二块公开榜单），个人页不显示操作人与备注（教室是公开屏幕）。
/// 30 秒无操作回看板并清屏。
/// </summary>
public sealed partial class BoardLookupPage : Page
{
    private static readonly SolidColorBrush Primary = Pal.Primary;
    private static readonly SolidColorBrush Secondary = Pal.Secondary;
    private static readonly SolidColorBrush Positive = Pal.Positive;
    private static readonly SolidColorBrush Negative = Pal.Negative;
    private static readonly SolidColorBrush Accent = Pal.Accent;

    private readonly DispatcherTimer _timeout = new();
    private readonly List<Student> _students = new();
    private readonly Dictionary<string, int> _totals = new();
    private int _columns = 8;
    private const double Gap = 10;
    private const double CellH = 104;
    private double _cellW = 180;
    private Student? _person;
    private bool _weekView;

    public BoardLookupPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        PointerPressed += (_, _) => ResetTimeout();
        Scroll.SizeChanged += (_, _) => Layout();
    }

    private AppServices Services => AppServices.Instance;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PeriodText.Text = Services.PeriodName;   // 班级名在顶上那条常驻状态里，这里只补周期
        Load();
        ResetTimeout();

        _timeout.Tick += OnTimeoutTick;
        _timeout.Start();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => _timeout.Stop();

    private void Load()
    {
        var period = Services.CurrentPeriod;
        _students.Clear();
        _students.AddRange(Services.Students.ListActive());
        _totals.Clear();
        foreach (var (k, v) in Services.Entries.PeriodTotals(period.Id)) _totals[k] = v;

        BuildGrid();
        Layout();

        var empty = _students.Count == 0;
        EmptyText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        Scroll.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }

    private void BuildGrid()
    {
        SeatGrid.Children.Clear();
        for (var i = 0; i < _students.Count; i++)
        {
            var student = _students[i];
            var card = new StudentCard { Index = i, ShowSeatOnly = true };
            card.Bind(student, _totals.GetValueOrDefault(student.Id), 0);
            card.Tapped += (_, _) => ShowPersonal(student);
            SeatGrid.Children.Add(card);
        }
    }

    private void Layout()
    {
        if (_students.Count == 0) return;

        var viewportW = Scroll.ViewportWidth > 0 ? Scroll.ViewportWidth : ActualWidth - 24;
        _columns = System.Math.Clamp((int)(viewportW / 160), 4, 12);
        _cellW = (viewportW - Gap * (_columns - 1)) / _columns;
        var rows = (int)System.Math.Ceiling(_students.Count / (double)_columns);

        SeatGrid.ColumnDefinitions.Clear();
        SeatGrid.RowDefinitions.Clear();
        for (var c = 0; c < _columns; c++)
            SeatGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(_cellW) });
        for (var r = 0; r < rows; r++)
            SeatGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(CellH) });

        SeatGrid.Width = _columns * _cellW + Gap * (_columns - 1);
        SeatGrid.Height = rows * CellH + Gap * (rows - 1);

        for (var i = 0; i < SeatGrid.Children.Count; i++)
        {
            var card = (StudentCard)SeatGrid.Children[i];
            var row = i / _columns;
            var col = i % _columns;
            Grid.SetRow(card, row);
            Grid.SetColumn(card, col);
            card.Margin = new Thickness(0, 0, col == _columns - 1 ? 0 : Gap, row == rows - 1 ? 0 : Gap);
            card.HorizontalAlignment = HorizontalAlignment.Stretch;
            card.VerticalAlignment = VerticalAlignment.Stretch;
        }
    }

    // ---------------- 超时 ----------------

    private void ResetTimeout()
    {
        _timeout.Stop();
        _timeout.Interval = TimeSpan.FromSeconds(Services.Settings.LookupTimeoutSec);
        _timeout.Start();
    }

    private void OnTimeoutTick(object? sender, object e)
    {
        _timeout.Stop();
        // 退出时清屏：上一个人的分数留在屏幕上，下一个凑过来就看到了
        ClearPersonal();
        if (App.Board is { } shell && shell.CurrentIndex == BoardNav.Lookup) shell.Navigate(BoardNav.Wall);
    }

    // ---------------- 个人页 ----------------

    private void ShowPersonal(Student student)
    {
        _person = student;
        ResetTimeout();

        PersonName.Text = student.Name;
        PersonSeat.Text = student.SeatText;

        var period = Services.CurrentPeriod;
        var points = _weekView ? WeekPoints(student.Id) : _totals.GetValueOrDefault(student.Id);

        BigPoints.Text = points.ToString();
        BigPoints.Foreground = points >= 0 ? Primary : Negative;
        BigPointsLabel.Text = _weekView ? "本周积分" : $"{period.Name}积分";

        // 名次：措辞给追赶目标，不做位置判决（不写"倒数第几"、不写"共 48 人"）
        var ordered = _students
            .OrderByDescending(s => _totals.GetValueOrDefault(s.Id))
            .ToList();
        var rank = ordered.FindIndex(s => s.Id == student.Id) + 1;
        var gap = 0;
        if (rank > 1) gap = _totals.GetValueOrDefault(ordered[rank - 2].Id) - _totals.GetValueOrDefault(student.Id);

        RankLine.Text = rank <= 0 ? "" : $"班级第 {rank} 名";
        GapLine.Text = rank <= 1
            ? "目前是班里积分最高的，继续保持"
            : gap > 0 ? $"距前一名 {gap} 分" : "和前一名的分一样高";

        WeekToggle.IsChecked = _weekView;
        PeriodToggle.IsChecked = !_weekView;

        BuildDistribution(student, period.Id);
        BuildRecent(student);

        PersonalLayer.Visibility = Visibility.Visible;
    }

    private int WeekPoints(string studentId)
    {
        var today = DateTime.Today;
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var from = Clock.StartOfDay(monday);
        var to = Clock.StartOfDay(today.AddDays(1));

        return Services.Entries
            .EntriesForStudent(studentId, 500)
            .Where(e => e.OccurredAt >= from && e.OccurredAt < to)
            .Sum(e => e.Delta);
    }

    private void BuildDistribution(Student student, string periodId)
    {
        DistPanel.Children.Clear();
        var dist = Services.Entries.RuleDistribution(periodId, student.Id);
        if (dist.Count == 0)
        {
            DistPanel.Children.Add(new TextBlock
            {
                Text = "这个周期还没有加分记录。",
                Foreground = Secondary,
                FontSize = 16,
                Margin = new Thickness(2, 6, 0, 0),
            });
            return;
        }

        // 条形长度按「次数」画：按分值画会让一次黑板演题把其余项压成一条看不见的线
        var maxCount = System.Math.Max(1, dist.Max(d => d.Count));
        foreach (var (label, count, points) in dist.Take(6))
        {
            var row = new Grid { Height = 34, ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });

            var name = new TextBlock
            {
                Text = label,
                FontSize = 16,
                Foreground = Primary,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(name, 0);

            var barHost = new Grid { VerticalAlignment = VerticalAlignment.Center, Height = 16 };
            var track = new Border
            {
                Background = Pal.TileBg,
                CornerRadius = new CornerRadius(8),
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            var bar = new Border
            {
                // 用比例而不是固定像素，窗口变宽条形跟着长
                Background = points >= 0 ? Positive : Negative,
                CornerRadius = new CornerRadius(8),
                HorizontalAlignment = HorizontalAlignment.Left,
                Width = System.Math.Max(6, 260.0 * count / maxCount),
            };
            barHost.Children.Add(track);
            barHost.Children.Add(bar);
            Grid.SetColumn(barHost, 1);

            var value = new TextBlock
            {
                Text = $"{count} 次  {(points >= 0 ? "+" : "")}{points}",
                FontFamily = new FontFamily("Consolas"),
                FontSize = 15,
                Foreground = Secondary,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            Grid.SetColumn(value, 2);

            row.Children.Add(name);
            row.Children.Add(barHost);
            row.Children.Add(value);
            DistPanel.Children.Add(row);
        }
    }

    private void BuildRecent(Student student)
    {
        RecentPanel.Children.Clear();
        var entries = Services.Entries.EntriesForStudent(student.Id, 6);
        if (entries.Count == 0)
        {
            RecentPanel.Children.Add(new TextBlock
            {
                Text = "还没有记录。",
                Foreground = Secondary,
                FontSize = 16,
            });
            return;
        }

        foreach (var e in entries)
        {
            var row = new Grid { ColumnSpacing = 10, Padding = new Thickness(0, 4, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var when = Clock.ToLocal(e.OccurredAt);
            var timeText = when.Date == DateTime.Today ? $"今天 {when:HH:mm}"
                : when.Date == DateTime.Today.AddDays(-1) ? $"昨天 {when:HH:mm}"
                : when.ToString("MM-dd HH:mm");

            var time = new TextBlock
            {
                Text = timeText,
                FontSize = 14,
                Foreground = Secondary,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(time, 0);

            var label = new TextBlock
            {
                Text = e.RuleLabel ?? "",
                FontSize = 16,
                Foreground = Primary,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(label, 1);

            var delta = new TextBlock
            {
                Text = e.IsReversed ? "已撤销" : e.DeltaText,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 17,
                FontWeight = FontWeights.SemiBold,
                Foreground = e.IsReversed ? Secondary : e.Delta >= 0 ? Positive : Negative,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(delta, 2);

            row.Children.Add(time);
            row.Children.Add(label);
            row.Children.Add(delta);
            RecentPanel.Children.Add(row);
        }
    }

    private void OnRangeToggle(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton b || b.Tag is not string tag) return;

        _weekView = tag == "week";
        WeekToggle.IsChecked = _weekView;
        PeriodToggle.IsChecked = !_weekView;

        if (_person is not null) ShowPersonal(_person);
        ResetTimeout();
    }

    private void OnPersonalCloseClick(object sender, RoutedEventArgs e)
    {
        ClearPersonal();
        ResetTimeout();
    }

    private void ClearPersonal()
    {
        PersonalLayer.Visibility = Visibility.Collapsed;
        _person = null;
        DistPanel.Children.Clear();
        RecentPanel.Children.Clear();
        BigPoints.Text = "";
        PersonName.Text = "";
    }
}
