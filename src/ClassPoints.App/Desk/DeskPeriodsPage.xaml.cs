using ClassPoints.App.Services;
using ClassPoints.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ClassPoints.App.Theme;

namespace ClassPoints.App.Desk;

/// <summary>
/// 周期与重置。这里的每一个按钮都可能让"数据看起来消失了"，
/// 所以每一步都先归档、再切换，并且在界面上把"数据还在哪"写清楚。
/// </summary>
public sealed partial class DeskPeriodsPage : Page
{
    public DeskPeriodsPage()
    {
        InitializeComponent();
        HardResetButton.Confirmed += OnHardResetHold;
        Loaded += (_, _) => Reload();
    }

    private static AppServices Services => App.Services;

    private DeskShell? Shell => App.Desk;

    private void Reload()
    {
        try
        {
            var period = Services.CurrentPeriod;
            var entryCount = Services.Periods.EntryCount(period.Id);
            var periodPoints = Services.Entries.PeriodPointsAwarded(period.Id);
            var termPoints = Services.Entries.TermTotals(period.TermKey).Values.Sum();

            PeriodNameText.Text = period.Name;
            PeriodRangeText.Text = "起止 " + period.RangeText;
            EntryCountText.Text = entryCount.ToString();
            PointsText.Text = periodPoints.ToString();
            TermPointsText.Text = termPoints.ToString();
            TermKeyText.Text = period.TermKey;

            ResetHint.Text = $"重置前会自动生成归档包（Excel + 数据库副本）到备份目录：{ClassPoints.Data.Database.BackupRoot}";

            RenderArchives();
        }
        catch (Exception ex)
        {
            Services.LogError("desk-periods", ex);
            Shell?.Toast("周期页读取失败：" + ex.Message, 6);
        }
    }

    private void RenderArchives()
    {
        var archived = Services.Periods.Archived();
        ArchiveList.Children.Clear();
        ArchiveEmpty.Visibility = archived.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ArchiveHint.Text = archived.Count == 0 ? "" : $"共 {archived.Count} 个已归档周期";

        foreach (var p in archived)
        {
            var count = Services.Periods.EntryCount(p.Id);
            var total = Services.Entries.PeriodTotals(p.Id).Values.Sum();

            var grid = new Grid { ColumnSpacing = 12, Padding = new Thickness(18, 8, 18, 8), MinHeight = 42 };
            foreach (var w in new[] { 0d, 180d, 90d, 90d, 150d })
                grid.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = w <= 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(w),
                });

            var title = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            title.Children.Add(new TextBlock { Text = p.Name, Style = Styled("DeskBodyText") });
            title.Children.Add(new TextBlock
            {
                Text = "学期编号 " + p.TermKey,
                Style = Styled("DeskSecondaryText"),
            });
            grid.Children.Add(title);

            Put(grid, 1, new TextBlock
            {
                Text = p.RangeText,
                Style = Styled("DeskSecondaryText"),
                VerticalAlignment = VerticalAlignment.Center,
            });
            Put(grid, 2, Number(count.ToString()));
            Put(grid, 3, Number(total.ToString()));

            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var view = new Button { Content = "看榜单", Style = Styled("DeskButton"), Height = 28, MinWidth = 62 };
            view.Click += async (_, _) => await ShowRankingAsync(p);
            actions.Children.Add(view);

            var export = new Button { Content = "导出", Style = Styled("DeskButton"), Height = 28, MinWidth = 54 };
            export.Click += async (_, _) => await ExportAsync(p);
            actions.Children.Add(export);

            Put(grid, 4, actions);

            ArchiveList.Children.Add(new Border
            {
                BorderBrush = Pal.Stroke,
                BorderThickness = new Thickness(0, 1, 0, 0),
                Child = grid,
            });
        }
    }

    private async Task ShowRankingAsync(Period period)
    {
        var totals = Services.Entries.PeriodTotals(period.Id);
        var students = Services.Students.ListActive()
            .OrderByDescending(s => totals.GetValueOrDefault(s.Id))
            .ThenBy(s => s.Name, StringComparer.CurrentCulture)
            .ToList();

        var panel = new StackPanel { Spacing = 4, Width = 420 };
        var rank = 0;
        var last = int.MinValue;
        for (var i = 0; i < students.Count; i++)
        {
            var points = totals.GetValueOrDefault(students[i].Id);
            if (points != last) rank = i + 1;
            last = points;

            var line = new Grid();
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            line.Children.Add(new TextBlock
            {
                Text = rank.ToString(),
                Style = Styled("DeskSecondaryText"),
            });

            var name = new TextBlock { Text = students[i].Name, Style = Styled("DeskBodyText") };
            Grid.SetColumn(name, 1);
            line.Children.Add(name);

            var value = Number(points.ToString());
            Grid.SetColumn(value, 2);
            line.Children.Add(value);

            panel.Children.Add(line);
        }

        await new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = period.Name + " · 排行榜",
            Content = new ScrollViewer { Content = panel, MaxHeight = 480 },
            CloseButtonText = "关闭",
        }.ShowAsync();
    }

    private async Task ExportAsync(Period period)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileSavePicker
            {
                SuggestedFileName = $"{Services.ClassName}-{period.Name}-流水",
            };
            picker.FileTypeChoices.Add("Excel 工作簿", new List<string> { ".xlsx" });
            WinRT.Interop.InitializeWithWindow.Initialize(picker,
                WinRT.Interop.WindowNative.GetWindowHandle(App.Window!));

            var file = await picker.PickSaveFileAsync();
            if (file is null) return;

            var students = Services.Students.ListActive();
            var scheme = Services.Exams.ActiveScheme();
            var termPoints = Services.Entries.TermTotals(period.TermKey);
            var ranking = Services.Exams.BuildRanking(students, termPoints, period.TermKey, scheme,
                Services.Settings.AbsencePolicy);
            var exams = Services.Exams.ListExams(period.Id);

            var bundle = new ClassPoints.Data.ExportBundle(
                ranking, exams,
                Services.Exams.ScoresForTerm(period.TermKey, exams.Select(e => e.Id).ToList()),
                Services.Entries.Entries(period.Id, 100_000),
                scheme, period, Services.Settings.AbsencePolicy, period.TermKey,
                students, Services.Exams.Weights(scheme.Id),
                Services.DeskOperator.DisplayName, DateTime.Now,
                new[] { "这是历史周期" + period.Name + "的导出，不含当前周期的数据。" });

            await File.WriteAllBytesAsync(file.Path, ClassPoints.Data.WorkbookBuilder.Build(bundle));
            Shell?.Toast("已导出 " + file.Path, 5);
        }
        catch (Exception ex)
        {
            Services.LogError("export-archive", ex);
            Shell?.Toast("导出失败：" + ex.Message, 6);
        }
    }

    private async void OnRename(object sender, RoutedEventArgs e)
    {
        var period = Services.CurrentPeriod;
        var box = new TextBox { Header = "周期名称", Text = period.Name };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "给这个周期改个名字",
            Content = box,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        if (string.IsNullOrWhiteSpace(box.Text)) return;
        Services.Periods.Rename(period.Id, box.Text.Trim());
        Reload();
    }

    // ---------------- 重置积分 ----------------

    private async void OnResetPeriod(object sender, RoutedEventArgs e)
    {
        var period = Services.CurrentPeriod;
        var count = Services.Periods.EntryCount(period.Id);
        var nextName = NextPeriodName();

        var nameBox = new TextBox { Header = "新周期名称", Text = nextName };
        var panel = new StackPanel { Spacing = 12, Width = 400 };
        panel.Children.Add(new TextBlock
        {
            Text = $"「{period.Name}」会归档，{count} 条记录一条不删，随时能在「历史周期」里查看和导出。" +
                   "学期编号不变，总评还是连续的。",
            Style = Styled("DeskSecondaryText"),
        });
        panel.Children.Add(nameBox);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "重置积分？",
            Content = panel,
            PrimaryButtonText = "归档并开始新周期",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        if (string.IsNullOrWhiteSpace(nameBox.Text)) return;

        Rollover(nameBox.Text.Trim(), newTerm: false);
    }

    private string NextPeriodName()
    {
        var now = DateTime.Now;
        return $"{now:yyyy 年 M 月}";
    }

    // ---------------- 重置学期 ----------------

    private void OnHardResetHold()
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                var box = new TextBox { PlaceholderText = "确认重置" };
                var panel = new StackPanel { Spacing = 12, Width = 420 };
                panel.Children.Add(new TextBlock
                {
                    Text = "积分、成绩、名单一起归档，学期从今天重来。旧数据不删，" +
                           "但总评和榜单从零起算。这是唯一不能撤销的操作。",
                    Style = Styled("DeskSecondaryText"),
                });
                panel.Children.Add(new TextBlock
                {
                    Text = "请输入「确认重置」四个字：",
                    Style = Styled("DeskBodyText"),
                });
                panel.Children.Add(box);

                var dialog = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = "重置学期",
                    Content = panel,
                    PrimaryButtonText = "重置学期",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Close,
                };

                if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
                if (box.Text.Trim() != "确认重置")
                {
                    Shell?.Toast("没有输入「确认重置」，已取消", 4);
                    return;
                }

                Rollover(ClassPoints.Data.Database.TermNameFor(DateTime.Now), newTerm: true);
            }
            catch (Exception ex)
            {
                Services.LogError("hard-reset", ex);
                Shell?.Toast("重置失败：" + ex.Message, 6);
            }
        });
    }

    private void Rollover(string name, bool newTerm)
    {
        try
        {
            var period = Services.CurrentPeriod;

            // 第 2 步：先归档（Excel 四表 + 数据库副本），再做任何切换。
            // 归档失败就不重置 —— 顺序反了的话，一旦导出报错，数据就"看不见了"。
            var archivePath = Services.Backups.BackupNow(newTerm ? "term-reset" : "period-reset",
                Services.BuildExportBundle());

            var next = Services.Periods.Rollover(name, newTerm);
            Services.Settings.LastBackupAt = Clock.NowMs();
            Services.Audit.Add("period.rollover", null, next.Id,
                $"{period.Name} → {next.Name}（{(newTerm ? "重置学期" : "重置积分")}），归档 {Path.GetFileName(archivePath)}");

            // 第 7 步：清派生状态。大屏会话里的身份、选中、周期封顶计数都不该跨周期存在。
            Services.Session.SignOut();

            Reload();
            Shell?.Toast($"已归档「{period.Name}」，新周期「{next.Name}」开始。" +
                         (newTerm ? "学期编号已更新。" : "学期编号不变。"), 6);
            Shell?.Navigate(DeskNav.Periods);
        }
        catch (Exception ex)
        {
            Services.LogError("rollover", ex);
            Shell?.Toast("归档失败，已中止重置：" + ex.Message, 8);
        }
    }

    // ---------------- 小工具 ----------------

    private static Style Styled(string key) => (Style)Application.Current.Resources[key];

    private static void Put(Grid grid, int column, FrameworkElement element)
    {
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }

    private static TextBlock Number(string text) => new()
    {
        Text = text,
        FontFamily = (FontFamily)Application.Current.Resources["NumberFont"],
        FontSize = 15,
        HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Center,
    };
}
