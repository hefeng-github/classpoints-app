using ClassPoints.App.Controls;
using ClassPoints.App.Services;
using ClassPoints.App.Theme;
using ClassPoints.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ClassPoints.App.Board;

/// <summary>
/// 事件计数（大屏）。教室门口最常做的动作：看见谁迟到，点他一下。
///
/// 三件事决定了这一页的形态：
/// 1) 一张卡上只有「−」「＋」两个落点，都是 64 高 —— 手指不用瞄准；
/// 2) 事件之间切换用顶部大胶囊，切完立刻重排，不做下拉框；
/// 3) 每次都留一条流水，「撤销上一步」是本页唯一需要记住的历史。
/// </summary>
public sealed partial class BoardCountersPage : Page
{
    private const double Gap = 10;
    private const double MinCardW = 196;
    private const double MinCardH = 158;

    private readonly List<Student> _students = [];
    private readonly List<Counter> _counters = [];
    private readonly Dictionary<string, int> _counts = [];
    private readonly Dictionary<string, TextBlock> _countLabels = [];
    private readonly List<(string CounterId, string StudentId)> _history = [];

    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly DispatcherTimer _resizeDebounce = new() { Interval = TimeSpan.FromMilliseconds(150) };

    private Counter? _current;
    private int _columns = 6;

    public BoardCountersPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        CardGrid.SizeChanged += (_, _) =>
        {
            _resizeDebounce.Stop();
            _resizeDebounce.Start();
        };
        _resizeDebounce.Tick += (_, _) =>
        {
            _resizeDebounce.Stop();
            LayoutCards();
        };
        _refreshTimer.Tick += (_, _) => Reload(keepCounter: true);
    }

    private AppServices Services => AppServices.Instance;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Reload(keepCounter: false);
        _refreshTimer.Start();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => _refreshTimer.Stop();

    // ---------------- 数据 ----------------

    private void Reload(bool keepCounter)
    {
        try
        {
            var period = Services.CurrentPeriod;
            var previousId = _current?.Id;

            _students.Clear();
            _students.AddRange(Services.Students.ListActive());

            _counters.Clear();
            _counters.AddRange(Services.Counters.ListAll());

            _current = keepCounter
                ? _counters.FirstOrDefault(c => c.Id == previousId) ?? _counters.FirstOrDefault()
                : _counters.FirstOrDefault();

            _counts.Clear();
            if (_current is not null)
            {
                var all = Services.Counters.Counts(period.Id);
                foreach (var ((counterId, studentId), value) in all)
                    if (counterId == _current.Id) _counts[studentId] = value;
            }

            SubText.Text = $"{period.Name} · {period.RangeText} · 只数次数，不加分也不扣分";
            BuildCounterChips(period.Id);
            BuildCards();
            LayoutCards();

            var total = Services.Counters.ClassTotal(period.Id, _current?.Id ?? "");
            TotalText.Text = _current is null ? "" : $"全班 {total} 次";

            Empty.Show(_students.Count == 0 || _counters.Count == 0);
            if (_students.Count == 0)
                Empty.Configure("\uE716", "还没有学生。到电脑端导入名单，或在记分台上粘贴一列姓名。",
                    null, "刷新");
            else if (_counters.Count == 0)
                Empty.Configure("\uE7C1", "还没有事件类型。到电脑端的「事件计数」页新增一个。", null, "刷新");

            Empty.PrimaryInvoked -= OnEmptyRefresh;
            Empty.SecondaryInvoked -= OnEmptyRefresh;
            Empty.SecondaryInvoked += OnEmptyRefresh;
        }
        catch (Exception ex)
        {
            Services.LogError("board-counters", ex);
            Toast("事件计数读取失败：" + ex.Message, 8);
        }
    }

    private void OnEmptyRefresh() => Reload(keepCounter: true);

    private void BuildCounterChips(string periodId)
    {
        CounterPanel.Children.Clear();

        foreach (var counter in _counters)
        {
            var active = counter.Id == _current?.Id;
            var total = Services.Counters.ClassTotal(periodId, counter.Id);

            var button = new Button
            {
                Height = 60,
                MinWidth = 150,
                Padding = new Thickness(18, 0, 18, 0),
                CornerRadius = new CornerRadius(14),
                BorderThickness = new Thickness(active ? 2 : 1),
                BorderBrush = active ? Pal.Accent : Pal.Stroke,
                Background = active ? Pal.AccentBg : Pal.Card,
                Tag = counter,
                HorizontalContentAlignment = HorizontalAlignment.Left,
            };

            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            content.Children.Add(new FontIcon
            {
                Glyph = counter.Glyph,
                FontFamily = new FontFamily(IconCatalog.FontFamily),
                FontSize = 18,
                Foreground = active ? Pal.Accent : Pal.Secondary,
                VerticalAlignment = VerticalAlignment.Center,
            });

            var text = new StackPanel { Spacing = 0, VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock
            {
                Text = counter.Label,
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Foreground = Pal.Primary,
            });
            text.Children.Add(new TextBlock
            {
                Text = total == 0 ? "还没记过" : $"{total} 次",
                FontSize = 12,
                Foreground = Pal.Secondary,
            });
            content.Children.Add(text);

            button.Content = content;
            button.Click += (_, _) =>
            {
                _current = counter;
                Reload(keepCounter: true);
            };
            CounterPanel.Children.Add(button);
        }
    }

    private void BuildCards()
    {
        CardGrid.Children.Clear();
        _countLabels.Clear();

        foreach (var student in _students)
        {
            var count = _counts.GetValueOrDefault(student.Id);

            var root = new Border
            {
                Background = Pal.Card,
                BorderBrush = Pal.Stroke,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(16),
                Padding = new Thickness(12, 10, 12, 10),
            };

            var stack = new StackPanel { Spacing = 4 };
            stack.Children.Add(new TextBlock
            {
                Text = student.Name,
                FontSize = 20,
                FontWeight = FontWeights.SemiBold,
                Foreground = Pal.Primary,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });

            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            var number = new TextBlock
            {
                Text = count.ToString(),
                FontFamily = (FontFamily)Application.Current.Resources["NumberFont"],
                FontSize = 34,
                FontWeight = FontWeights.Bold,
                Foreground = count == 0 ? Pal.Secondary : Pal.Primary,
            };
            line.Children.Add(number);
            line.Children.Add(new TextBlock
            {
                Text = "次",
                FontSize = 14,
                Foreground = Pal.Secondary,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 6),
            });
            stack.Children.Add(line);
            _countLabels[student.Id] = number;

            var buttons = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 4, 0, 0) };
            buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var minus = StepButton("\uE738", "减一次", count > 0, () => Step(student, -1));
            Grid.SetColumn(minus, 0);
            buttons.Children.Add(minus);

            var plus = StepButton("\uE710", "加一次", true, () => Step(student, 1), primary: true);
            Grid.SetColumn(plus, 1);
            buttons.Children.Add(plus);

            stack.Children.Add(buttons);
            root.Child = stack;
            CardGrid.Children.Add(root);

            // +/- 按钮的可点区域跟着卡片一起放大：整张卡点右边加、左边减
            root.Tag = student;
        }
    }

    /// <summary>
    /// 加减按钮。教室后排看过来，"加"必须比"减"显眼：加号用强调色底 + 强调色字形，
    /// 减号留在瓷砖色上。两个按钮一样灰的话，站在屏幕前的学生要犹豫一秒才敢按。
    /// </summary>
    private Button StepButton(string glyph, string tip, bool enabled, Action onClick, bool primary = false)
    {
        var button = new Button
        {
            Height = 52,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            // 减号原来是瓷白色，跟卡片底几乎一个色，三米外看不出那是个按钮。
            // 改成强调色的浅底：和实心的「加」是一对，主次还在，但边界清楚。
            BorderBrush = primary ? Pal.Accent : Pal.AccentBg,
            Background = primary ? Pal.Accent : Pal.AccentBg,
            IsEnabled = enabled,
            Content = new FontIcon
            {
                Glyph = glyph,
                FontFamily = new FontFamily(IconCatalog.FontFamily),
                FontSize = primary ? 22 : 24,
                Foreground = primary ? Pal.OnAccent : Pal.Primary,
            },
        };
        ToolTipService.SetToolTip(button, tip);
        button.Click += (_, _) => onClick();
        return button;
    }

    private void Step(Student student, int value)
    {
        if (_current is not { } counter) return;

        try
        {
            var period = Services.CurrentPeriod;
            var count = Services.Counters.AddTick(counter.Id, student.Id, period.Id, value, Services.Operator);

            _counts[student.Id] = count;
            if (_countLabels.TryGetValue(student.Id, out var label))
            {
                label.Text = count.ToString();
                label.Foreground = count == 0 ? Pal.Secondary : Pal.Primary;
            }

            _history.Add((counter.Id, student.Id));
            UndoButton.IsEnabled = true;

            TotalText.Text = $"全班 {Services.Counters.ClassTotal(period.Id, counter.Id)} 次";
            if (value > 0) Sounds.Play(Cue.Tap);
            Toast($"{student.Name} · {counter.Label} {count} 次", 1.5);
        }
        catch (Exception ex)
        {
            Services.LogError("board-counter-step", ex);
            Toast("记不上：" + ex.Message, 6);
        }
    }

    private void OnUndoLast(object sender, RoutedEventArgs e)
    {
        if (_history.Count == 0) return;

        var (counterId, studentId) = _history[^1];
        _history.RemoveAt(_history.Count - 1);
        UndoButton.IsEnabled = _history.Count > 0;

        try
        {
            var period = Services.CurrentPeriod;
            var count = Services.Counters.UndoLast(counterId, studentId, period.Id, Services.Operator);
            Reload(keepCounter: true);
            Toast($"已撤销一步，现在 {count} 次", 2.5);
        }
        catch (Exception ex)
        {
            Services.LogError("board-counter-undo", ex);
            Toast("撤销失败：" + ex.Message, 6);
        }
    }

    // ---------------- 布局 ----------------

    private void LayoutCards()
    {
        if (CardGrid.Children.Count == 0) return;

        var viewportW = Scroll.ViewportWidth > 0 ? Scroll.ViewportWidth : CardGrid.ActualWidth;
        var viewportH = Scroll.ViewportHeight > 0 ? Scroll.ViewportHeight : 600;

        _columns = System.Math.Clamp((int)((viewportW + Gap) / (MinCardW + Gap)), 3, 10);
        var rows = System.Math.Clamp((int)((viewportH + Gap) / (MinCardH + Gap)), 2, 6);

        var cellW = System.Math.Max(150, (viewportW - Gap * (_columns - 1)) / _columns);
        var cellH = System.Math.Clamp((viewportH - Gap * (rows - 1)) / rows, MinCardH, 210);
        var neededRows = (int)System.Math.Ceiling(CardGrid.Children.Count / (double)_columns);

        CardGrid.ColumnDefinitions.Clear();
        CardGrid.RowDefinitions.Clear();
        for (var c = 0; c < _columns; c++)
            CardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(cellW) });
        for (var r = 0; r < neededRows; r++)
            CardGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(cellH) });

        CardGrid.Width = _columns * cellW + Gap * (_columns - 1);
        CardGrid.Height = neededRows * cellH + Gap * (neededRows - 1);

        for (var i = 0; i < CardGrid.Children.Count; i++)
        {
            var card = (FrameworkElement)CardGrid.Children[i];
            var row = i / _columns;
            var col = i % _columns;
            Grid.SetRow(card, row);
            Grid.SetColumn(card, col);
            card.Margin = new Thickness(0, 0, col == _columns - 1 ? 0 : Gap, row == neededRows - 1 ? 0 : Gap);
            card.HorizontalAlignment = HorizontalAlignment.Stretch;
            card.VerticalAlignment = VerticalAlignment.Stretch;
        }
    }

    public void Toast(string message, double seconds = 3) => ToastLayer.Show(message, seconds);
}
