using ClassPoints.Core;
using Windows.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using ClassPoints.App.Theme;

namespace ClassPoints.App.Controls;

public sealed partial class StudentCard : UserControl
{
    private static readonly SolidColorBrush StrokeNormal = Pal.Stroke;
    private static readonly SolidColorBrush StrokeSelected = Pal.Accent;
    private static readonly SolidColorBrush StrokeNegative = Pal.Negative;
    private static readonly SolidColorBrush TextPrimary = Pal.Primary;
    private static readonly SolidColorBrush TextSecondary = Pal.Secondary;
    private static readonly SolidColorBrush Positive = Pal.Positive;
    private static readonly SolidColorBrush Negative = Pal.Negative;
    private static readonly SolidColorBrush CardBg = Pal.Card;
    private static readonly SolidColorBrush CardBgPressed = Pal.TileBg;

    private DispatcherTimer? _rollTimer;
    private DispatcherTimer? _flashTimer;
    private int _shownPoints;

    public StudentCard()
    {
        InitializeComponent();
    }

    public Student? Student { get; private set; }

    public int Index { get; set; }

    public bool IsSelected { get; private set; }

    /// <summary>查分页用：不显示分数，只显示姓名与座位（否则它就成了第二块公开榜单）。</summary>
    public bool ShowSeatOnly { get; set; }

    public void Bind(Student student, int points, int todayDelta)
    {
        Student = student;
        _shownPoints = points;
        NameText.Text = student.Name;
        ScoreText.Text = ShowSeatOnly ? SeatShort(student) : points.ToString();
        TodayText.Text = ShowSeatOnly
            ? ""
            : todayDelta == 0 ? "" : $"今日 {(todayDelta > 0 ? "+" : "")}{todayDelta}";
        TodayText.Foreground = todayDelta >= 0 ? Positive : Negative;
        ApplyTypography();
        UpdateStroke();
    }

    private static string SeatShort(Student s) => s.SeatNo is int n ? $"{n} 号" : "";

    /// <summary>只更新「今日变化」那一行，不动分数（分数由 RollTo 负责动画）。</summary>
    public void UpdateToday(int todayDelta)
    {
        if (ShowSeatOnly) return;
        TodayText.Text = todayDelta == 0 ? "" : $"今日 {(todayDelta > 0 ? "+" : "")}{todayDelta}";
        TodayText.Foreground = todayDelta >= 0 ? Positive : Negative;
    }

    /// <summary>卡片尺寸不足时按顺序让位：先压「今日变化」，再压姓名，最后才动分数。分数永不低于 28。</summary>
    public void ApplyTypography()
    {
        var h = Root.ActualHeight > 0 ? Root.ActualHeight : 125;
        var w = Root.ActualWidth > 0 ? Root.ActualWidth : 222;

        var scoreSize = System.Math.Clamp(h * 0.29, 28, 44);
        ScoreText.FontSize = scoreSize;

        var nameSize = w < 200 || h < 112 ? 18.0 : 20.0;
        NameText.FontSize = nameSize;

        TodayText.Visibility = h < 118 || ShowSeatOnly ? Visibility.Collapsed : Visibility.Visible;
    }

    public void SetSelected(bool selected)
    {
        IsSelected = selected;
        CheckBadge.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        UpdateStroke();
    }

    private void UpdateStroke()
    {
        Root.BorderBrush = IsSelected ? StrokeSelected : StrokeNormal;
        Root.BorderThickness = new Thickness(IsSelected ? 3 : 1);
    }

    public void SetPressed(bool pressed)
    {
        Root.Background = pressed ? CardBgPressed : CardBg;
        Root.Opacity = pressed ? 0.96 : 1.0;
    }

    /// <summary>分数滚动：从旧值数到新值，300 ms。数字本身跳动比任何动画都更能说明"记上了"。</summary>
    public void RollTo(int target, bool animate = true)
    {
        _rollTimer?.Stop();
        if (ShowSeatOnly)
        {
            _shownPoints = target;
            return;
        }

        if (!animate || System.Math.Abs(target - _shownPoints) == 0)
        {
            _shownPoints = target;
            ScoreText.Text = target.ToString();
            return;
        }

        var from = _shownPoints;
        var steps = 10;
        var step = 0;
        ScoreText.Foreground = target >= from ? Positive : Negative;

        _rollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        _rollTimer.Tick += (_, _) =>
        {
            step++;
            var t = (double)step / steps;
            var eased = 1 - System.Math.Pow(1 - t, 3);
            var value = (int)System.Math.Round(from + (target - from) * eased);
            ScoreText.Text = value.ToString();
            if (step >= steps)
            {
                _rollTimer?.Stop();
                _shownPoints = target;
                ScoreText.Text = target.ToString();
                ScoreText.Foreground = TextPrimary;
            }
        };
        _rollTimer.Start();
    }

    /// <summary>被扣分的学生卡片短暂显示红边 2 秒，不弹窗、不发问 —— 避免公开羞辱。</summary>
    public void FlashNegative()
    {
        _flashTimer?.Stop();
        Root.BorderBrush = StrokeNegative;
        Root.BorderThickness = new Thickness(2);

        _flashTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _flashTimer.Tick += (_, _) =>
        {
            _flashTimer?.Stop();
            UpdateStroke();
        };
        _flashTimer.Start();
    }

    /// <summary>入账后浮起一个 +10，600 ms 淡出。反馈放在手指上方，不放在指尖处。</summary>
    public void FloatDelta(int delta)
    {
        var text = new TextBlock
        {
            Text = (delta >= 0 ? "+" : "") + delta,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 24,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = delta >= 0 ? Positive : Negative,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
            Margin = new Thickness(0, 2, 6, 0),
        };

        if (Content is not Grid host)
        {
            // UserControl 的 Content 是 Border(Root)，浮字挂在 Grid 上
            if (Root.Child is not Grid g) return;
            host = g;
        }

        host.Children.Add(text);

        var transform = new TranslateTransform();
        text.RenderTransform = transform;
        var story = new Storyboard();
        var fade = new DoubleAnimation
        {
            From = 1, To = 0, Duration = new Duration(TimeSpan.FromMilliseconds(600)),
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(fade, text);
        Storyboard.SetTargetProperty(fade, "Opacity");

        var move = new DoubleAnimation
        {
            From = 0, To = -34, Duration = new Duration(TimeSpan.FromMilliseconds(600)),
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(move, transform);
        Storyboard.SetTargetProperty(move, "Y");

        story.Children.Add(fade);
        story.Children.Add(move);
        story.Completed += (_, _) => host.Children.Remove(text);
        story.Begin();
    }
}
