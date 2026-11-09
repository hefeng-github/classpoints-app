using ClassPoints.App.Services;
using ClassPoints.Core;
using ClassPoints.Data;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;
using ClassPoints.App.Theme;

namespace ClassPoints.App.Desk;

/// <summary>
/// 成绩页（仅电脑端）：导入 → 权重 → 排名预览 → 导出。
/// 这一页的所有滑块都要"当场看到后果"，所以任何一次调整都直接重算并重排预览，
/// 280 ms 防抖只是为了不让拖动过程把 CPU 打满。
/// </summary>
public sealed partial class DeskExamsPage : Page
{
    private static readonly AbsencePolicy[] Policies =
    [
        AbsencePolicy.Redistribute,
        AbsencePolicy.Zero,
        AbsencePolicy.ClassAverage,
    ];

    private Scheme _scheme = new();
    private List<Exam> _exams = [];
    private Dictionary<string, double> _weights = [];
    private RankingResult? _ranking;
    private RankingResult? _baseline;
    private List<Student> _students = [];
    private Dictionary<string, int> _termPoints = [];

    private bool _loading;
    private bool _showAll;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _debounce;

    public DeskExamsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Reload();
    }

    private static AppServices Services => App.Services;

    private DeskShell? Shell => App.Desk;

    private void Reload()
    {
        try
        {
            _loading = true;
            var period = Services.CurrentPeriod;
            _exams = Services.Exams.ListExams(period.Id);
            _scheme = Services.Exams.ActiveScheme();
            _weights = Services.Exams.Weights(_scheme.Id);
            _students = Services.Students.ListActive();
            _termPoints = Services.Entries.TermTotals(period.TermKey);

            ExamWeightSlider.Value = _scheme.ExamPercent;
            DailyWeightSlider.Value = _scheme.DailyPercent;
            NormalizeCheck.IsChecked = _scheme.Normalize;

            PolicyCombo.ItemsSource = Policies.Select(p => PolicyLabel(p)).ToList();
            PolicyCombo.SelectedIndex = Array.IndexOf(Policies, Services.Settings.AbsencePolicy);
            if (PolicyCombo.SelectedIndex < 0) PolicyCombo.SelectedIndex = 0;

            _loading = false;

            RenderExams();
            RenderWeights();
            Recompute(resetBaseline: true);
        }
        catch (Exception ex)
        {
            _loading = false;
            Services.LogError("desk-exams", ex);
            Shell?.Toast("成绩页读取失败：" + ex.Message, 6);
        }
    }

    private static string PolicyLabel(AbsencePolicy p) => p switch
    {
        AbsencePolicy.Zero => "缺考记 0 分",
        AbsencePolicy.ClassAverage => "缺考按该场全班均分替代",
        _ => "缺考那场不计，权重摊给其余场次（默认）",
    };

    // ---------------- 一、考试列表 ----------------

    private void RenderExams()
    {
        ExamList.Children.Clear();

        if (_exams.Count == 0)
        {
            ExamList.Children.Add(new Border
            {
                Padding = new Thickness(18, 4, 18, 18),
                Child = new TextBlock
                {
                    Text = "还没有成绩。点右上「选择文件导入」，支持 xlsx / xls / csv。",
                    Style = Styled("DeskSecondaryText"),
                },
            });
            return;
        }

        foreach (var exam in _exams)
        {
            var scores = Services.Exams.Scores(exam.Id);
            var present = scores.Count(s => !s.Absent && s.Score is not null);
            var absent = scores.Count(s => s.Absent);
            var missing = System.Math.Max(0, _students.Count - present - absent);

            var grid = new Grid { ColumnSpacing = 12, Padding = new Thickness(18, 8, 18, 8), MinHeight = 44 };
            foreach (var w in new[] { 110d, 0d, 80d, 80d, 80d, 140d, 140d })
                grid.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = w <= 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(w),
                });

            Put(grid, 0, new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(exam.ExamDate) ? "—" : exam.ExamDate!,
                Style = Styled("DeskSecondaryText"),
                VerticalAlignment = VerticalAlignment.Center,
            });

            Put(grid, 1, new TextBlock
            {
                Text = exam.Name,
                Style = Styled("DeskBodyText"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });

            Put(grid, 2, Number(exam.FullScore.ToString("0.#")));
            Put(grid, 3, Number(present.ToString()));
            Put(grid, 4, Number(missing > 0 ? $"{absent} + {missing} 缺" : absent.ToString()));

            Put(grid, 5, new TextBlock
            {
                Text = Clock.ToLocal(exam.ImportedAt).ToString("yyyy-MM-dd HH:mm"),
                Style = Styled("DeskSecondaryText"),
                VerticalAlignment = VerticalAlignment.Center,
            });

            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            var full = new Button { Content = "改满分", Style = Styled("DeskButton"), Height = 28, MinWidth = 60 };
            full.Click += async (_, _) => await EditFullScoreAsync(exam);
            actions.Children.Add(full);

            var rename = new Button { Content = "改名", Style = Styled("DeskButton"), Height = 28, MinWidth = 54 };
            rename.Click += async (_, _) => await RenameAsync(exam);
            actions.Children.Add(rename);

            var del = new Button { Content = "删除", Style = Styled("DeskButton"), Height = 28, MinWidth = 54 };
            del.Click += async (_, _) => await DeleteExamAsync(exam);
            actions.Children.Add(del);

            Put(grid, 6, actions);

            ExamList.Children.Add(new Border
            {
                BorderBrush = Pal.Stroke,
                BorderThickness = new Thickness(0, 1, 0, 0),
                Child = grid,
            });
        }
    }

    private async Task EditFullScoreAsync(Exam exam)
    {
        var box = new NumberBox
        {
            Header = "满分",
            Value = exam.FullScore,
            Minimum = 1,
            Maximum = 100000,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
        };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "修改满分",
            Content = new StackPanel
            {
                Spacing = 10,
                Width = 300,
                Children =
                {
                    box,
                    new TextBlock
                    {
                        Text = "改满分只影响「考试百分制」这一列。",
                        Style = Styled("DeskSecondaryText"),
                    },
                },
            },
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        Services.Exams.UpdateFullScore(exam.Id, box.Value);
        Reload();
    }

    private async Task RenameAsync(Exam exam)
    {
        var box = new TextBox { Header = "考试名称", Text = exam.Name };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "重命名考试",
            Content = box,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        if (string.IsNullOrWhiteSpace(box.Text)) return;
        Services.Exams.Rename(exam.Id, box.Text.Trim());
        Reload();
    }

    private async Task DeleteExamAsync(Exam exam)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "删除这场考试？",
            Content = $"{exam.Name} 的 {Services.Exams.Scores(exam.Id).Count} 条成绩会被删除，" +
                      "总评里这场考试的权重也会清掉。积分不受影响。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        Services.Exams.Delete(exam.Id);
        Services.Audit.Add("exam.delete", null, exam.Id, exam.Name);
        Reload();
    }

    // ---------------- 二、权重 ----------------

    private void RenderWeights()
    {
        _loading = true;

        var examPercent = _scheme.ExamPercent;
        ExamWeightLabel.Text = $"考试占 {examPercent}%";
        DailyWeightLabel.Text = $"平时占 {100 - examPercent}%";

        WeightList.Children.Clear();

        if (_exams.Count == 0)
        {
            WeightList.Children.Add(new TextBlock
            {
                Text = "没考试时只有平时分，权重不起作用。",
                Style = Styled("DeskSecondaryText"),
            });
            WeightSumText.Text = "";
            _loading = false;
            UpdateImpact();
            return;
        }

        foreach (var exam in _exams)
        {
            var value = _weights.TryGetValue(exam.Id, out var w) ? w : 0;

            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });

            row.Children.Add(new TextBlock
            {
                Text = exam.Name,
                Style = Styled("DeskBodyText"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });

            var slider = new Slider { Minimum = 0, Maximum = 100, StepFrequency = 5, Value = value * 100 };
            var number = new NumberBox
            {
                Value = value * 100,
                Minimum = 0,
                Maximum = 100,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
                SmallChange = 5,
            };

            var examId = exam.Id;
            void Push(double percent)
            {
                _weights[examId] = percent / 100;
                if (System.Math.Abs(slider.Value - percent) > 0.01) slider.Value = percent;
                if (System.Math.Abs(number.Value - percent) > 0.01) number.Value = percent;
                UpdateWeightSum();
                DebouncedRecompute();
            }

            slider.ValueChanged += (_, e) => { if (!_loading) Push(e.NewValue); };
            number.ValueChanged += (_, e) => { if (!_loading && !double.IsNaN(e.NewValue)) Push(e.NewValue); };

            Grid.SetColumn(slider, 1);
            row.Children.Add(slider);
            Grid.SetColumn(number, 2);
            row.Children.Add(number);

            WeightList.Children.Add(row);
        }

        _loading = false;
        UpdateWeightSum();
        UpdateImpact();
    }

    private double WeightSum => _weights.Values.Sum();

    private void UpdateWeightSum()
    {
        if (_exams.Count == 0) return;
        var sum = WeightSum * 100;
        WeightSumText.Text = $"合计 {sum:0.#}%";
        WeightSumText.Foreground = System.Math.Abs(sum - 100) < 0.5 ? Pal.Positive : Pal.Negative;
        ExportButton.IsEnabled = System.Math.Abs(sum - 100) < 0.5 || _exams.Count == 0;
    }

    private void UpdateImpact()
    {
        var examMean = _ranking is { Rows.Count: > 0 }
            ? _ranking.Rows.Average(r => r.ExamRaw)
            : 0;
        var pointsMean = _ranking is { Rows.Count: > 0 }
            ? _ranking.Rows.Average(r => (double)r.Points)
            : 0;

        ImpactText.Text = examMean <= 0
            ? "没有成绩，看不出实际影响。导入后按全班平均换算。"
            : SchemeMath.ImpactText(examMean, pointsMean, _scheme);

        FormulaText.Text = _scheme.FormulaText + " · 平时分 = 学期累计积分";
    }

    private void OnWeightSliderChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading) return;

        var exam = (int)System.Math.Round(ExamWeightSlider.Value / 5) * 5;
        exam = System.Math.Clamp(exam, 0, 100);
        _scheme.ExamWeight = exam / 100.0;
        _scheme.DailyWeight = 1 - _scheme.ExamWeight;

        _loading = true;
        ExamWeightSlider.Value = exam;
        DailyWeightSlider.Value = 100 - exam;
        ExamWeightLabel.Text = $"考试占 {exam}%";
        DailyWeightLabel.Text = $"平时占 {100 - exam}%";
        _loading = false;

        Services.Exams.SaveScheme(_scheme);
        DebouncedRecompute();
    }

    private void OnNormalizeChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _scheme.Normalize = NormalizeCheck.IsChecked == true;
        Services.Exams.SaveScheme(_scheme);
        Recompute(resetBaseline: false);
    }

    private void OnPolicyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || PolicyCombo.SelectedIndex < 0) return;
        Services.Settings.AbsencePolicy = Policies[PolicyCombo.SelectedIndex];
        Recompute(resetBaseline: false);
    }

    private void OnAutoBalance(object sender, RoutedEventArgs e)
    {
        if (_exams.Count == 0) return;
        _weights = SchemeMath.AutoBalance(_weights);
        Services.Exams.SetWeights(_scheme.Id, _weights);
        RenderWeights();
        DebouncedRecompute();
        Shell?.Toast("权重已按原比例配平到 100%", 2);
    }

    private void DebouncedRecompute()
    {
        _debounce ??= DispatcherQueue.CreateTimer();
        _debounce.Interval = TimeSpan.FromMilliseconds(280);
        _debounce.IsRepeating = false;
        _debounce.Tick -= OnDebounceTick;
        _debounce.Tick += OnDebounceTick;
        _debounce.Start();
    }

    private void OnDebounceTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        Services.Exams.SetWeights(_scheme.Id, _weights);
        Recompute(resetBaseline: false);
    }

    // ---------------- 三、排名预览 ----------------

    private void Recompute(bool resetBaseline)
    {
        try
        {
            var period = Services.CurrentPeriod;
            _ranking = Services.Exams.BuildRanking(_students, _termPoints, period.TermKey, _scheme,
                Services.Settings.AbsencePolicy);

            if (resetBaseline || _baseline is null) _baseline = _ranking;

            RenderRank();
            UpdateImpact();
        }
        catch (Exception ex)
        {
            Services.LogError("rank-preview", ex);
        }
    }

    private void RenderRank()
    {
        RankList.Children.Clear();
        if (_ranking is null)
        {
            RankEmpty.Visibility = Visibility.Visible;
            return;
        }

        RankEmpty.Visibility = _ranking.Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var rows = _showAll ? _ranking.Rows : _ranking.Rows.Take(20).ToList();
        foreach (var r in rows)
        {
            var grid = new Grid { ColumnSpacing = 12, Padding = new Thickness(18, 7, 18, 7), MinHeight = 38 };
            foreach (var w in new[] { 60d, 0d, 100d, 110d, 110d, 110d })
                grid.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = w <= 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(w),
                });

            Put(grid, 0, Number(r.Rank.ToString()));

            var who = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            who.Children.Add(new TextBlock { Text = r.Name, Style = Styled("DeskBodyText") });
            if (r.AbsentExams.Count > 0)
                who.Children.Add(new TextBlock
                {
                    Text = "缺考 " + string.Join("、", r.AbsentExams),
                    Style = Styled("DeskSecondaryText"),
                    Foreground = Pal.Warn,
                });
            Put(grid, 1, who);

            Put(grid, 2, Number(r.Points.ToString()));
            Put(grid, 3, Number(r.ExamText));
            Put(grid, 4, Number(r.ExamPercentText));

            var total = Number(r.TotalText);
            total.Foreground = Pal.Accent;
            total.FontWeight = FontWeights.SemiBold;
            Put(grid, 5, total);

            RankList.Children.Add(new Border
            {
                BorderBrush = Pal.Stroke,
                BorderThickness = new Thickness(0, 1, 0, 0),
                Child = grid,
            });
        }

        if (_ranking.Rows.Count > 20)
        {
            var more = new Button
            {
                Content = _showAll ? "只看前 20 名" : $"显示全部 {_ranking.Rows.Count} 人",
                Style = Styled("DeskButton"),
                Margin = new Thickness(18, 10, 18, 14),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            more.Click += (_, _) =>
            {
                _showAll = !_showAll;
                RenderRank();
            };
            RankList.Children.Add(more);
        }

        ChangeText.Text = _ranking.HasExamData
            ? RankingCalculator.DescribeTopChanges(_baseline!, _ranking, 10)
            : "没有考试成绩时，总评就是平时分。";
    }

    // ---------------- 四、导入与导出 ----------------

    private async void OnImportScores(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".xlsx");
            picker.FileTypeFilter.Add(".xlsm");
            picker.FileTypeFilter.Add(".xls");
            picker.FileTypeFilter.Add(".csv");
            WinRT.Interop.InitializeWithWindow.Initialize(picker,
                WinRT.Interop.WindowNative.GetWindowHandle(App.Window!));

            var file = await picker.PickSingleFileAsync();
            if (file is null) return;

            var grid = file.FileType.Equals(".csv", StringComparison.OrdinalIgnoreCase)
                ? CsvFile.Read(file.Path)
                : ExcelReader.Read(file.Path);

            if (grid.RowCount < 2)
            {
                Shell?.Toast("这个文件里没有成绩数据（至少要有表头 + 一行）");
                return;
            }

            var parsed = ScoreSheetParser.Parse(grid, _students);
            await ShowImportDialogAsync(file.Name, file.Path, grid, parsed);
        }
        catch (Exception ex)
        {
            Services.LogError("import-scores", ex);
            Shell?.Toast("导入失败：" + ex.Message, 6);
        }
    }

    private async Task ShowImportDialogAsync(string fileName, string path, SheetGrid grid, ScoreParseResult parsed)
    {
        var nameBox = new TextBox { Header = "考试名称", Text = GuessExamName(fileName), Width = 240 };
        var fullBox = new NumberBox
        {
            Header = "满分",
            Value = 750,
            Minimum = 1,
            Maximum = 100000,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            Width = 160,
        };
        var dateBox = new TextBox
        {
            Header = "考试日期",
            Text = DateTime.Today.ToString("yyyy-MM-dd"),
            Width = 160,
        };

        var summary = new TextBlock
        {
            Style = Styled("DeskSecondaryText"),
            TextWrapping = TextWrapping.Wrap,
            Text = parsed.SummaryText,
        };

        var list = new StackPanel { Spacing = 2 };
        void RenderRows()
        {
            list.Children.Clear();
            foreach (var row in parsed.Rows.Take(200))
            {
                var line = new Grid { ColumnSpacing = 10, Padding = new Thickness(0, 3, 0, 3) };
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                line.Children.Add(new TextBlock { Text = row.RawName, Style = Styled("DeskBodyText") });

                var score = Number(row.ScoreText);
                Grid.SetColumn(score, 1);
                line.Children.Add(score);

                var status = new TextBlock
                {
                    Text = row.Kind switch
                    {
                        ScoreMatchKind.Matched => row.Matched is null ? "匹配" : $"{row.Matched.Name} · {row.Matched.SeatText}",
                        ScoreMatchKind.Absent => "缺考，按口径计入",
                        ScoreMatchKind.Summary => "非学生行，已忽略",
                        ScoreMatchKind.Duplicate => "两人同名，请选择",
                        ScoreMatchKind.Unmatched => "名单里没有这个人",
                        _ => "分数无法识别",
                    },
                    Style = Styled("DeskSecondaryText"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = row.Kind switch
                    {
                        ScoreMatchKind.Matched => Pal.Positive,
                        ScoreMatchKind.Absent => Pal.Warn,
                        ScoreMatchKind.Summary => Pal.Secondary,
                        _ => Pal.Negative,
                    },
                };
                Grid.SetColumn(status, 2);
                line.Children.Add(status);

                if (row.Kind == ScoreMatchKind.Unmatched)
                {
                    var add = new Button { Content = "加入名单", Style = Styled("DeskButton"), Height = 26, MinWidth = 70 };
                    add.Click += (_, _) =>
                    {
                        var student = Services.Students.Insert(new Student
                        {
                            Name = row.CleanName.Length > 0 ? row.CleanName : NameNormalizer.Clean(row.RawName),
                            SeatNo = (_students.Max(s => s.SeatNo) ?? 0) + 1,
                        });
                        _students.Add(student);
                        row.Matched = student;
                        row.Kind = ScoreMatchKind.Matched;
                        row.Include = true;
                        summary.Text = parsed.SummaryText;
                        RenderRows();
                    };
                    Grid.SetColumn(add, 3);
                    line.Children.Add(add);
                }

                list.Children.Add(line);
            }
        }
        RenderRows();

        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        header.Children.Add(nameBox);
        header.Children.Add(fullBox);
        header.Children.Add(dateBox);

        var note = new TextBlock
        {
            Style = Styled("DeskSecondaryText"),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 620,
            Text = "只按姓名匹配。同名的跳过，汇总行也跳过。",
        };

        var panel = new StackPanel { Spacing = 12, Width = 640 };
        panel.Children.Add(header);
        panel.Children.Add(summary);
        panel.Children.Add(note);
        panel.Children.Add(new ScrollViewer { Content = list, MaxHeight = 320 });

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"导入成绩 · {fileName}",
            Content = new ScrollViewer { Content = panel, MaxHeight = 620 },
            PrimaryButtonText = "导入",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        var existing = _exams.FirstOrDefault(x =>
            string.Equals(x.Name, nameBox.Text.Trim(), StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            panel.Children.Insert(0, new TextBlock
            {
                Text = $"这场考试已有 {Services.Exams.Scores(existing.Id).Count} 条成绩，导入会整场覆盖。",
                Style = Styled("DeskSecondaryText"),
                Foreground = Pal.Warn,
            });
            dialog.DefaultButton = ContentDialogButton.Close;
        }

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var examName = nameBox.Text.Trim();
        if (examName.Length == 0)
        {
            Shell?.Toast("考试名称不能为空");
            return;
        }

        var rowsToWrite = parsed.Rows
            .Where(r => r.Include && r.Kind is ScoreMatchKind.Matched or ScoreMatchKind.Absent)
            .Select(r => (StudentId: r.Matched!.Id, Score: r.Score, Absent: r.Kind == ScoreMatchKind.Absent))
            .ToList();

        if (rowsToWrite.Count == 0)
        {
            Shell?.Toast("没有可导入的行");
            return;
        }

        var result = Services.Exams.ImportScores(Services.CurrentPeriod.Id, examName,
            double.IsNaN(fullBox.Value) ? 750 : fullBox.Value,
            dateBox.Text.Trim(), rowsToWrite, Path.GetFileName(path));

        Services.Audit.Add("exam.import", null, result.ExamId,
            $"{examName}：写入 {result.Written}，缺考 {result.Absent}");

        Reload();
        Shell?.Toast($"已导入 {examName}：{result.Written} 人" +
                     (result.Absent > 0 ? $"，缺考 {result.Absent} 人" : ""), 5);
    }

    private static string GuessExamName(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        foreach (var suffix in new[] { "成绩", "成绩单", "分数" })
            name = name.Replace(suffix, "");
        return name.Length == 0 ? "考试" : name;
    }

    private void OnPreflight(object sender, RoutedEventArgs e)
    {
        var issues = CollectIssues();
        var panel = new StackPanel { Spacing = 8, Width = 420 };
        panel.Children.Add(new TextBlock
        {
            Text = issues.Count == 0 ? "没有问题，可以导出。" : $"发现 {issues.Count} 个需要注意的地方：",
            Style = Styled("DeskBodyText"),
        });
        foreach (var issue in issues)
            panel.Children.Add(new TextBlock
            {
                Text = "· " + issue,
                Style = Styled("DeskSecondaryText"),
                Foreground = Pal.Warn,
            });
        if (issues.Count > 0)
            panel.Children.Add(new TextBlock
            {
                Text = "不影响导出，但会写进「参数说明」那张表。",
                Style = Styled("DeskSecondaryText"),
            });

        _ = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "导出前检查",
            Content = panel,
            CloseButtonText = "知道了",
        }.ShowAsync();
    }

    private List<string> CollectIssues()
    {
        var issues = new List<string>();

        if (_students.Count == 0) issues.Add("名单是空的，导出的表只有表头。");
        if (_exams.Count == 0) issues.Add("没有导入任何考试成绩，总评等于平时分。");

        var sum = WeightSum * 100;
        if (_exams.Count > 0 && System.Math.Abs(sum - 100) >= 0.5)
            issues.Add($"各场考试权重合计 {sum:0.#}%，不等于 100%。");

        var missing = _exams.Sum(e =>
        {
            var scores = Services.Exams.Scores(e.Id);
            return System.Math.Max(0, _students.Count - scores.Count);
        });
        if (missing > 0) issues.Add($"{missing} 人次没有成绩记录（缺考或未导入）。");

        var zero = _ranking?.Rows.Count(r => r.Points == 0) ?? 0;
        if (zero > 0) issues.Add($"{zero} 人本学期积分为 0。");

        return issues;
    }

    private async void OnExport(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileSavePicker
            {
                SuggestedFileName = $"{Services.ClassName}-总评-{DateTime.Today:yyyyMMdd}",
            };
            picker.FileTypeChoices.Add("Excel 工作簿", new List<string> { ".xlsx" });
            WinRT.Interop.InitializeWithWindow.Initialize(picker,
                WinRT.Interop.WindowNative.GetWindowHandle(App.Window!));

            var file = await picker.PickSaveFileAsync();
            if (file is null) return;

            var bundle = Services.BuildExportBundle(Services.Settings.AbsencePolicy, CollectIssues());
            using (var stream = await file.OpenStreamForWriteAsync())
            {
                stream.SetLength(0);
                var bytes = WorkbookBuilder.Build(bundle);
                await stream.WriteAsync(bytes);
            }

            Services.Audit.Add("export.excel", null, file.Name, "");
            Shell?.Toast("已导出到 " + file.Path, 6);
        }
        catch (Exception ex)
        {
            Services.LogError("export-excel", ex);
            Shell?.Toast("导出失败：" + ex.Message, 6);
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
