using ClassPoints.App.Services;
using ClassPoints.Core;
using ClassPoints.Data;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;
using ClassPoints.App.Theme;

namespace ClassPoints.App.Desk;

/// <summary>
/// 名单：改名单是"坐下来的活"，所以完整功能都在这一页（大屏端只读 + 向导里粘贴）。
/// 行内不给"编辑"二级页，双击一行直接改，改完点保存 —— 名单一辈子改不到十次，别为它做路由。
/// </summary>
public sealed partial class DeskStudentsPage : Page
{
    private List<Student> _all = [];
    private Dictionary<string, int> _period = [];
    private Dictionary<string, int> _term = [];

    public DeskStudentsPage()
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
            var period = Services.CurrentPeriod;
            _period = Services.Entries.PeriodTotals(period.Id);
            _term = Services.Entries.TermTotals(period.TermKey);
            _all = Services.Students.ListAll();

            var active = _all.Count(s => !s.IsDeleted);
            var deleted = _all.Count(s => s.IsDeleted);
            SubText.Text = $"{active} 人在册" + (deleted > 0 ? $"，{deleted} 人在回收站（30 天内可恢复）" : "")
                         + $" · 座位 {Services.Settings.SeatRows} 排 × {Services.Settings.SeatCols} 座";

            Render();
        }
        catch (Exception ex)
        {
            Services.LogError("desk-students", ex);
            Shell?.Toast("名单读取失败：" + ex.Message, 6);
        }
    }

    private void Render()
    {
        var keyword = SearchBox.Text.Trim();
        var rows = _all
            .Where(s => !s.IsDeleted)
            .Where(s => keyword.Length == 0
                        || s.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                        || (s.StudentNo ?? "").Contains(keyword, StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s.SeatNo ?? int.MaxValue)
            .ThenBy(s => s.Name, StringComparer.CurrentCulture)
            .ToList();

        RowList.Children.Clear();
        EmptyPanel.Visibility = _all.All(s => s.IsDeleted) ? Visibility.Visible : Visibility.Collapsed;

        foreach (var s in rows) RowList.Children.Add(BuildRow(s));
    }

    private UIElement BuildRow(Student s)
    {
        var grid = new Grid { ColumnSpacing = 12, MinHeight = 44, Padding = new Thickness(12, 0, 12, 0) };
        foreach (var w in new[] { 90d, 0d, 110d, 110d, 90d, 90d, 100d, 150d })
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = w <= 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(w),
            });

        Add(grid, 0, new TextBlock
        {
            Text = s.StudentNo ?? "—",
            Style = Styled("DeskSecondaryText"),
            VerticalAlignment = VerticalAlignment.Center,
        });

        var name = new TextBlock
        {
            Text = s.Name,
            Style = Styled("DeskBodyText"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Add(grid, 1, name);

        Add(grid, 2, new TextBlock
        {
            Text = s.SeatText,
            Style = Styled("DeskSecondaryText"),
            VerticalAlignment = VerticalAlignment.Center,
        });

        Add(grid, 3, new TextBlock
        {
            Text = s.GroupName ?? "—",
            Style = Styled("DeskSecondaryText"),
            VerticalAlignment = VerticalAlignment.Center,
        });

        Add(grid, 4, PointsText(_period.GetValueOrDefault(s.Id)));

        Add(grid, 5, PointsText(_term.GetValueOrDefault(s.Id)));

        var state = new TextBlock
        {
            Text = s.Active ? "在册" : "已停用",
            Style = Styled("DeskSecondaryText"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Add(grid, 6, state);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var edit = new Button { Content = "编辑", Style = Styled("DeskButton"), Height = 28, MinWidth = 54 };
        edit.Click += async (_, _) => await EditStudentAsync(s, isNew: false);
        actions.Children.Add(edit);

        var toggle = new Button
        {
            Content = s.Active ? "停用" : "恢复",
            Style = Styled("DeskButton"),
            Height = 28,
            MinWidth = 54,
        };
        toggle.Click += (_, _) => ToggleStudent(s);
        actions.Children.Add(toggle);

        Add(grid, 7, actions);

        return new Border
        {
            Child = grid,
            BorderBrush = Pal.Stroke,
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
    }

    private static TextBlock PointsText(int value)
    {
        var block = new TextBlock
        {
            Text = value.ToString(),
            FontFamily = (FontFamily)Application.Current.Resources["NumberFont"],
            FontSize = 15,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = value < 0
                ? Pal.Negative
                : Pal.Primary,
        };
        return block;
    }

    private static void Add(Grid grid, int column, FrameworkElement element)
    {
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }

    private static Style Styled(string key) => (Style)Application.Current.Resources[key];

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => Render();

    // ---------------- 添加 / 编辑 / 停用 ----------------

    private async Task<bool> EditStudentAsync(Student s, bool isNew)
    {
        var nameBox = new TextBox { Header = "姓名", Text = s.Name };
        var noBox = new TextBox { Header = "学号（可空）", Text = s.StudentNo ?? "" };
        var seatBox = new NumberBox
        {
            Header = "座位号（1 起，按 8 座一排换算）",
            Value = s.SeatNo ?? double.NaN,
            Minimum = 1,
            Maximum = 999,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
        };
        var groupBox = new TextBox { Header = "分组（可空）", Text = s.GroupName ?? "" };

        var panel = new StackPanel { Spacing = 12, Width = 360 };
        panel.Children.Add(nameBox);
        panel.Children.Add(noBox);
        panel.Children.Add(seatBox);
        panel.Children.Add(groupBox);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "编辑学生",
            Content = panel,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return false;

        var name = NameNormalizer.Clean(nameBox.Text);
        if (name.Length == 0)
        {
            Shell?.Toast("姓名不能为空");
            return false;
        }

        s.Name = name;
        s.StudentNo = string.IsNullOrWhiteSpace(noBox.Text) ? null : noBox.Text.Trim();
        s.SeatNo = double.IsNaN(seatBox.Value) ? null : (int)seatBox.Value;
        s.GroupName = string.IsNullOrWhiteSpace(groupBox.Text) ? null : groupBox.Text.Trim();
        s.Active = true;

        if (isNew) Services.Students.Insert(s); else Services.Students.Update(s);
        Reload();
        Shell?.Toast(isNew ? $"已添加 {s.Name}" : $"已保存 {s.Name}", 2);
        return true;
    }

    private void ToggleStudent(Student s)
    {
        if (s.Active)
        {
            Services.Students.SoftDelete(s.Id);
            Shell?.Toast($"{s.Name} 已停用，可在「回收站」恢复", 3);
        }
        else
        {
            Services.Students.Restore(s.Id);
            Shell?.Toast($"{s.Name} 已恢复", 3);
        }
        Reload();
    }

    private async void OnAddStudent(object sender, RoutedEventArgs e)
    {
        var nextSeat = (_all.Where(x => !x.IsDeleted).Max(x => x.SeatNo) ?? 0) + 1;
        var draft = new Student { Name = "", SeatNo = nextSeat };
        await EditStudentAsync(draft, isNew: true);
    }

    // ---------------- 粘贴一列姓名 ----------------

    private async void OnPasteNames(object sender, RoutedEventArgs e)
    {
        var box = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 260,
            PlaceholderText = "一行一个姓名，例如：\n王小明\n李思远\n张伟",
            FontFamily = (FontFamily)Application.Current.Resources["TextFont"],
        };
        var startSeat = new NumberBox
        {
            Header = "从第几个座位开始（默认接在最后）",
            Value = (_all.Where(x => !x.IsDeleted).Max(x => x.SeatNo) ?? 0) + 1,
            Minimum = 1,
            Maximum = 999,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
        };

        var panel = new StackPanel { Spacing = 12, Width = 420 };
        panel.Children.Add(box);
        panel.Children.Add(startSeat);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "粘贴一列姓名",
            Content = panel,
            PrimaryButtonText = "导入",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var lines = box.Text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
        {
            Shell?.Toast("没有可导入的姓名");
            return;
        }

        var added = Services.Students.ImportNames(lines,
            double.IsNaN(startSeat.Value) ? null : (int)startSeat.Value);
        Reload();
        Services.Audit.Add("students.import_paste", null, null, $"{added} 人");
        Shell?.Toast(added == 0 ? "全部重名，没有新增" : $"已导入 {added} 人", 3);
    }

    // ---------------- Excel 导入 ----------------

    private async void OnImportExcel(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.Desktop };
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

            if (grid.RowCount == 0)
            {
                Shell?.Toast("这个文件里没有读到数据");
                return;
            }

            // 名单只认「姓名」一列，其余列尽量识别；识别不准就让用户自己选
            var options = Enumerable.Range(0, grid.ColumnCount)
                .Select(c => new ColumnOption(c, grid.Cell(0, c), grid.RowCount > 1 ? grid.Cell(1, c) : ""))
                .ToList();

            var nameCombo = new ComboBox { Header = "姓名列", ItemsSource = options, Width = 320 };
            nameCombo.SelectedIndex = options.FindIndex(o => o.Header.Contains("姓名") || o.Header.Contains("学生"));
            if (nameCombo.SelectedIndex < 0) nameCombo.SelectedIndex = 0;

            var noCombo = new ComboBox { Header = "学号列（可选）", ItemsSource = options, Width = 320 };
            noCombo.SelectedIndex = options.FindIndex(o => o.Header.Contains("学号") || o.Header.Contains("编号"));

            var seatCombo = new ComboBox { Header = "座位列（可选）", ItemsSource = options, Width = 320 };
            seatCombo.SelectedIndex = options.FindIndex(o => o.Header.Contains("座"));

            var groupCombo = new ComboBox { Header = "分组列（可选）", ItemsSource = options, Width = 320 };
            groupCombo.SelectedIndex = options.FindIndex(o => o.Header.Contains("组"));

            var preview = new TextBlock { Style = Styled("DeskSecondaryText"), TextWrapping = TextWrapping.Wrap };
            void UpdatePreview()
            {
                var lines = new List<string>();
                for (var r = 1; r < System.Math.Min(grid.RowCount, 6); r++)
                {
                    var n = nameCombo.SelectedItem is ColumnOption c ? grid.Cell(r, c.Index) : "";
                    lines.Add($"· {n}");
                }
                preview.Text = $"共 {grid.RowCount - 1} 行数据。前几行：\n" + string.Join("\n", lines);
            }
            nameCombo.SelectionChanged += (_, _) => UpdatePreview();
            UpdatePreview();

            var hint = new TextBlock
            {
                Style = Styled("DeskSecondaryText"),
                Text = "只按姓名匹配：重名跳过，没有的新增。学号、座位按列自动识别。",
            };

            var panel = new StackPanel { Spacing = 12, Width = 360 };
            panel.Children.Add(new TextBlock { Text = file.Name, Style = Styled("DeskBodyText") });
            panel.Children.Add(nameCombo);
            panel.Children.Add(noCombo);
            panel.Children.Add(seatCombo);
            panel.Children.Add(groupCombo);
            panel.Children.Add(preview);
            panel.Children.Add(hint);

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "导入名单",
                Content = new ScrollViewer { Content = panel, MaxHeight = 520 },
                PrimaryButtonText = "导入",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            if (nameCombo.SelectedItem is not ColumnOption nameCol) return;

            var added = 0;
            var skipped = 0;
            var seat = (_all.Where(x => !x.IsDeleted).Max(x => x.SeatNo) ?? 0);

            for (var r = 1; r < grid.RowCount; r++)
            {
                var name = NameNormalizer.StripListIndex(grid.Cell(r, nameCol.Index));
                if (name.Length == 0) continue;
                if (NameNormalizer.IsLikelySummaryRow(name)) { skipped++; continue; }

                var exists = _all.Any(x => !x.IsDeleted && NameNormalizer.NormalizeKey(x.Name) == NameNormalizer.NormalizeKey(name));
                if (exists) { skipped++; continue; }

                int? seatNo = null;
                if (seatCombo.SelectedItem is ColumnOption seatCol
                    && int.TryParse(grid.Cell(r, seatCol.Index), out var parsedSeat) && parsedSeat > 0)
                    seatNo = parsedSeat;
                seatNo ??= ++seat;

                var student = new Student
                {
                    Name = name,
                    StudentNo = noCombo.SelectedItem is ColumnOption noCol && grid.Cell(r, noCol.Index).Length > 0
                        ? grid.Cell(r, noCol.Index).Trim()
                        : null,
                    SeatNo = seatNo,
                    GroupName = groupCombo.SelectedItem is ColumnOption grpCol && grid.Cell(r, grpCol.Index).Length > 0
                        ? grid.Cell(r, grpCol.Index).Trim()
                        : null,
                };
                Services.Students.Insert(student);
                _all.Add(student);
                added++;
            }

            Reload();
            Services.Audit.Add("students.import_excel", null, file.Name, $"新增 {added}，跳过 {skipped}");
            Shell?.Toast($"已新增 {added} 人" + (skipped > 0 ? $"，跳过 {skipped} 行（重名/表头）" : ""), 5);
        }
        catch (Exception ex)
        {
            Services.LogError("import-students", ex);
            Shell?.Toast("导入失败：" + ex.Message, 6);
        }
    }

    // ---------------- 座位布局 ----------------

    private async void OnSeatLayout(object sender, RoutedEventArgs e)
    {
        var rows = new NumberBox
        {
            Header = "排数",
            Value = Services.Settings.SeatRows,
            Minimum = 2,
            Maximum = 10,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
        };
        var cols = new NumberBox
        {
            Header = "每排座位数",
            Value = Services.Settings.SeatCols,
            Minimum = 4,
            Maximum = 12,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
        };
        var reflow = new CheckBox
        {
            Content = "按当前顺序重排座位号（1 起顺排）",
            IsChecked = true,
        };

        var panel = new StackPanel { Spacing = 12, Width = 320 };
        panel.Children.Add(rows);
        panel.Children.Add(cols);
        panel.Children.Add(reflow);
        panel.Children.Add(new TextBlock
        {
            Text = "决定大屏上「几排几座」的显示和记分台的列数。",
            Style = Styled("DeskSecondaryText"),
        });

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "座位布局",
            Content = panel,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        Services.Settings.SeatRows = (int)rows.Value;
        Services.Settings.SeatCols = (int)cols.Value;

        if (reflow.IsChecked == true)
        {
            var ordered = _all.Where(s => !s.IsDeleted)
                .OrderBy(s => s.SeatNo ?? int.MaxValue).ThenBy(s => s.Name)
                .Select(s => s.Id).ToList();
            Services.Students.ReassignSeats(ordered);
        }

        Reload();
        Shell?.Toast("座位布局已保存", 3);
    }

    // ---------------- 回收站 ----------------

    private async void OnRecycle(object sender, RoutedEventArgs e)
    {
        var deleted = _all.Where(s => s.IsDeleted).ToList();
        var panel = new StackPanel { Spacing = 8, Width = 460 };

        if (deleted.Count == 0)
        {
            panel.Children.Add(new TextBlock { Text = "回收站是空的。", Style = Styled("DeskSecondaryText") });
        }
        else
        {
            panel.Children.Add(new TextBlock
            {
                Text = "停用的不进榜单、不占座位，数据保留，随时恢复。",
                Style = Styled("DeskSecondaryText"),
            });

            foreach (var s in deleted)
            {
                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var text = new TextBlock
                {
                    Text = $"{s.Name} · {s.SeatText} · 学期累计 {_term.GetValueOrDefault(s.Id)}",
                    Style = Styled("DeskBodyText"),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                row.Children.Add(text);

                var restore = new Button { Content = "恢复", Style = Styled("DeskButton"), Height = 28 };
                restore.Click += (_, _) =>
                {
                    Services.Students.Restore(s.Id);
                    Reload();
                    Shell?.Toast($"{s.Name} 已恢复", 2);
                };
                Grid.SetColumn(restore, 1);
                row.Children.Add(restore);

                panel.Children.Add(row);
            }
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "回收站",
            Content = new ScrollViewer { Content = panel, MaxHeight = 460 },
            CloseButtonText = "关闭",
        };
        await dialog.ShowAsync();
        Reload();
    }
}
