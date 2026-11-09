using ClassPoints.App.Services;
using ClassPoints.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ClassPoints.App.Theme;

namespace ClassPoints.App.Desk;

/// <summary>
/// 总览：老师坐下先看这一页 —— 今天发生了什么、有哪几件事没做、下一步该点哪里。
/// 四个数字都是现算的，不做缓存：这一页的目的就是"让人相信数据是真的"。
/// </summary>
public sealed partial class DeskHomePage : Page
{
    public DeskHomePage()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    private static AppServices Services => App.Services;

    private DeskShell? Shell => App.Desk;

    private void Refresh()
    {
        try
        {
            var period = Services.CurrentPeriod;
            var students = Services.Students.ListActive();
            var recent = Services.Entries.Entries(period.Id, 2000);
            var today = recent.Where(e => Clock.ToLocal(e.OccurredAt).Date == DateTime.Today).ToList();

            TitleText.Text = "总览";
            SubText.Text = $"{Services.ClassName} · {period.Name} · {period.RangeText}";

            // 今日：被撤销的记录不计入（它们是冲销行，delta 已经取反，直接加就是净额）
            var todaySum = today.Sum(e => e.Delta);
            TodayPoints.Text = (todaySum >= 0 ? "+" : "") + todaySum;
            TodayPoints.Foreground = todaySum < 0 ? Pal.Negative : Pal.Positive;
            TodayCount.Text = today.Count == 0 ? "今天还没有记录" : $"{today.Count} 条记录";

            var term = Services.Entries.TermTotals(period.TermKey);
            TermPoints.Text = term.Values.Sum().ToString();
            PeriodPoints.Text = $"本月积分合计 {Services.Entries.PeriodPointsAwarded(period.Id)}";

            StudentCount.Text = students.Count.ToString();
            SeatText.Text = $"{Services.Settings.SeatRows} 排 × {Services.Settings.SeatCols} 座";

            var exams = Services.Exams.ListSummaries(period.Id, students.Count);
            ExamCount.Text = exams.Count.ToString();
            var scheme = Services.Exams.ActiveScheme();
            var weightSum = Services.Exams.Weights(scheme.Id).Values.Sum();
            WeightText.Text = exams.Count == 0 ? "还没导入成绩" : $"权重合计 {weightSum * 100:0.#}%";

            RenderRecent(today.Count > 0 ? today : recent);
            RenderTodo(students.Count, exams, scheme, weightSum);
        }
        catch (Exception ex)
        {
            Services.LogError("desk-home", ex);
            Shell?.Toast("总览读取失败：" + ex.Message, 6);
        }
    }

    private void RenderRecent(List<Entry> rows)
    {
        RecentList.Children.Clear();
        RecentEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (rows.Count == 0)
        {
            RecentHint.Text = "";
            return;
        }

        RecentHint.Text = $"共 {rows.Count} 条 · 这里只取最近 12 条，完整流水在大屏";

        foreach (var e in rows.Take(12))
        {
            var row = new Grid { Padding = new Thickness(18, 8, 18, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });

            var time = new TextBlock
            {
                Text = Clock.ToLocal(e.OccurredAt).ToString("MM-dd HH:mm"),
                Style = (Style)Application.Current.Resources["DeskSecondaryText"],
            };
            Grid.SetColumn(time, 0);
            row.Children.Add(time);

            var who = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            who.Children.Add(new TextBlock
            {
                Text = e.StudentName,
                Style = (Style)Application.Current.Resources["DeskBodyText"],
            });
            who.Children.Add(new TextBlock
            {
                Text = e.RuleLabel ?? (e.IsReversal ? "撤销" : "手动"),
                Style = (Style)Application.Current.Resources["DeskSecondaryText"],
                VerticalAlignment = VerticalAlignment.Center,
            });
            Grid.SetColumn(who, 1);
            row.Children.Add(who);

            var note = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(e.Note) ? (e.DetailSuffix.Length > 0 ? e.DetailSuffix : "") : e.Note!,
                Style = (Style)Application.Current.Resources["DeskSecondaryText"],
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Right,
                MaxWidth = 320,
            };
            Grid.SetColumn(note, 2);
            row.Children.Add(note);

            var delta = new TextBlock
            {
                Text = e.DeltaText,
                FontFamily = (FontFamily)Application.Current.Resources["NumberFont"],
                FontSize = 15,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = e.Delta >= 0 ? Pal.Positive : Pal.Negative,
                Opacity = e.IsReversed ? 0.45 : 1,
            };
            Grid.SetColumn(delta, 3);
            row.Children.Add(delta);

            RecentList.Children.Add(row);
            RecentList.Children.Add(new Border
            {
                Height = 1,
                Background = Pal.Stroke,
                Margin = new Thickness(18, 0, 18, 0),
            });
        }
    }

    private void RenderTodo(int studentCount, List<ClassPoints.Data.ExamSummary> exams,
        Scheme scheme, double weightSum)
    {
        TodoList.Children.Clear();

        if (studentCount == 0)
            AddTodo("还没导入名单", "没名单就没记分台。去「名单」粘一列姓名。", true);

        var lastBackup = Services.Settings.LastBackupAt;
        var days = lastBackup <= 0 ? 999 : (DateTime.Today - Clock.LocalDate(lastBackup).Date).Days;
        if (days >= 7)
            AddTodo("备份很久没做了", lastBackup <= 0 ? "这台机器上还没有任何备份。" : $"上次备份是 {days} 天前。", days >= 999);
        else if (days >= 3)
            AddTodo("该备份了", $"上次备份 {days} 天前，建议每周至少一次。", false);

        if (exams.Count > 0 && System.Math.Abs(weightSum - 1.0) > 0.0001)
            AddTodo("考试权重没配平", $"合计 {weightSum * 100:0.#}%，不等于 100% 导不出总评。去「成绩」页点「自动配平」。", true);

        var missing = exams.Sum(e => e.MissingCount);
        if (missing > 0)
            AddTodo($"{missing} 人缺考未处理", "缺考按「" + AbsencePolicyText(Services.Settings.AbsencePolicy) + "」计入，可在「成绩」页改口径。", false);

        if (exams.Count == 0 && studentCount > 0)
            AddTodo("还没有成绩", "期末总评要至少一场考试。去「成绩」导入。", false);

        if (!Services.Settings.HasRecovery)
            AddTodo("还没有恢复码", "忘了密码只能靠恢复码。去「设置」生成一张。", true);

        if (TodoList.Children.Count == 0)
            AddTodo("没有待办", "名单、成绩、备份都齐了。", false);
    }

    private static string AbsencePolicyText(AbsencePolicy policy) => policy switch
    {
        AbsencePolicy.Zero => "缺考记 0 分",
        AbsencePolicy.ClassAverage => "按班均分计",
        _ => "按其余考试权重重新分配",
    };

    private void AddTodo(string title, string detail, bool urgent)
    {
        var border = new Border
        {
            Padding = new Thickness(12, 10, 12, 10),
            CornerRadius = new CornerRadius(8),
            Background = urgent ? Pal.NegativeBg : Pal.Card,
            BorderThickness = new Thickness(1),
            BorderBrush = urgent ? Pal.Negative : Pal.Stroke,
        };

        var panel = new StackPanel { Spacing = 3 };
        panel.Children.Add(new TextBlock
        {
            Text = title,
            Style = (Style)Application.Current.Resources["DeskBodyText"],
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        panel.Children.Add(new TextBlock
        {
            Text = detail,
            Style = (Style)Application.Current.Resources["DeskSecondaryText"],
        });

        border.Child = panel;
        TodoList.Children.Add(border);
    }

    private void OnGoStudents(object sender, RoutedEventArgs e) => Shell?.Navigate(DeskNav.Students);

    private void OnGoExams(object sender, RoutedEventArgs e) => Shell?.Navigate(DeskNav.Exams);

    private void OnGoPrint(object sender, RoutedEventArgs e) => Shell?.Navigate(DeskNav.Print);

    private void OnGoCounters(object sender, RoutedEventArgs e) => Shell?.Navigate(DeskNav.Counters);

    private void OnRefresh(object sender, RoutedEventArgs e) => Refresh();

    private void OnBackup(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = Services.Backups.BackupNow("manual", null);
            Services.Settings.LastBackupAt = Clock.NowMs();
            Shell?.Toast("已备份到 " + Path.GetFileName(path), 4);
            Refresh();
        }
        catch (Exception ex)
        {
            Services.LogError("manual-backup", ex);
            Shell?.Toast("备份失败：" + ex.Message, 6);
        }
    }
}
