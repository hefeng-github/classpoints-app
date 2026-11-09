using ClassPoints.App.Services;
using ClassPoints.Core;
using ClassPoints.Data;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClassPoints.App.Desk;

/// <summary>
/// 打印页：一边选版式，一边就是浏览器里的真实排版。
/// 预览和打印是同一份 HTML —— 见过"预览挺好、打出来少半列"的人，就再也不会用第二次。
/// </summary>
public sealed partial class DeskPrintPage : Page
{
    private string _html = "";
    private bool _ready;

    public DeskPrintPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        // WebView2 是进程内浏览器，不显式关掉的话退出时会在 COM 终结器里崩一次。
        Unloaded += (_, _) =>
        {
            try { Preview.Close(); }
            catch (Exception ex) { Services.LogError("print-unload", ex); }
        };
    }

    private static AppServices Services => App.Services;

    private DeskShell? Shell => App.Desk;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_ready) return;

        try
        {
            foreach (var printer in PrintService.ListPrinters())
                PrinterCombo.Items.Add(printer);
            if (PrinterCombo.Items.Count > 0) PrinterCombo.SelectedIndex = 0;
            if (PrinterCombo.Items.Count == 0)
                PrinterCombo.Items.Add("没有找到打印机");

            StudentCombo.Items.Add(new ComboBoxItem { Content = "全班逐个打印", Tag = "" });
            foreach (var s in Services.Students.ListActive().OrderBy(s => s.SeatNo ?? int.MaxValue))
                StudentCombo.Items.Add(new ComboBoxItem { Content = $"{s.Name}（{s.SeatText}）", Tag = s.Id });
            StudentCombo.SelectedIndex = 0;

            await Preview.EnsureCoreWebView2Async();
            if (Preview.CoreWebView2 is null)
            {
                StatusText.Text = "预览要 Edge WebView2。Win10/11 一般自带，缺了去微软官网装。";
                PreviewHint.Visibility = Visibility.Collapsed;
                return;
            }

            Preview.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            Preview.CoreWebView2.Settings.IsStatusBarEnabled = false;

            _ready = true;
            Build();
        }
        catch (Exception ex)
        {
            Services.LogError("desk-print", ex);
            StatusText.Text = "预览初始化失败：" + ex.Message;
            PreviewHint.Visibility = Visibility.Collapsed;
        }
    }

    private int SelectedSheet =>
        SheetFinal.IsChecked == true ? 1
        : SheetPersonal.IsChecked == true ? 2
        : SheetRecovery.IsChecked == true ? 3
        : SheetArchive.IsChecked == true ? 4
        : 0;

    private void OnSheetChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;

        var sheet = SelectedSheet;
        RankOptions.Visibility = sheet == 0 ? Visibility.Visible : Visibility.Collapsed;
        PersonalOptions.Visibility = sheet == 2 ? Visibility.Visible : Visibility.Collapsed;
        RecoveryOptions.Visibility = sheet == 3 ? Visibility.Visible : Visibility.Collapsed;
        ArchiveOptions.Visibility = sheet == 4 ? Visibility.Visible : Visibility.Collapsed;

        Build();
    }

    private void OnOptionChanged(object sender, object e)
    {
        if (!_ready) return;
        Build();
    }

    private void Build()
    {
        if (Preview.CoreWebView2 is null) return;

        try
        {
            StatusText.Text = "";
            var ctx = Services.BuildPrintContext();
            var period = Services.CurrentPeriod;
            var scheme = Services.Exams.ActiveScheme();
            var students = Services.Students.ListActive();
            var term = Services.Entries.TermTotals(period.TermKey);
            var exams = Services.Exams.ListExams(period.Id);
            var weights = Services.Exams.Weights(scheme.Id);
            var ranking = Services.Exams.BuildRanking(students, term, period.TermKey, scheme,
                Services.Settings.AbsencePolicy);

            _html = SelectedSheet switch
            {
                1 => PrintTemplates.FinalGrade(ctx, ranking.Rows, exams, weights, scheme,
                        Services.Settings.AbsencePolicy),
                2 => BuildPersonal(ctx, ranking, students, exams, weights, scheme),
                3 => BuildRecovery(ctx),
                4 => BuildArchive(ctx, period, students.Count),
                _ => PrintTemplates.RankingTable(ctx, ranking.Rows,
                        IncludeTotalCheck.IsChecked == true,
                        scheme.ExamPercent,
                        scheme.Normalize),
            };

            Preview.NavigateToString(_html);
            PreviewHint.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            Services.LogError("print-build", ex);
            StatusText.Text = "生成失败：" + ex.Message;
        }
    }

    private string BuildPersonal(PrintContext ctx, RankingResult ranking, List<Student> students,
        List<Exam> exams, Dictionary<string, double> weights, Scheme scheme)
    {
        var period = Services.CurrentPeriod;
        var examIds = exams.Select(e => e.Id).ToList();
        var scores = Services.Exams.ScoresForTerm(period.TermKey, examIds)
            .ToDictionary(s => (s.ExamId, s.StudentId));

        var selected = (StudentCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        var targets = string.IsNullOrEmpty(selected)
            ? students.OrderBy(s => s.SeatNo ?? int.MaxValue).ToList()
            : students.Where(s => s.Id == selected).ToList();

        var pages = new List<string>();
        foreach (var student in targets)
        {
            var row = ranking.Rows.FirstOrDefault(r => r.StudentId == student.Id);
            if (row is null) continue;

            pages.Add(PrintTemplates.PersonalReport(ctx, student, row, exams, weights, scores, scheme,
                students.Count,
                Services.Entries.EntriesForStudent(student.Id, 12),
                Services.Entries.RuleDistribution(period.Id, student.Id)));
        }

        if (pages.Count == 0)
            return PrintTemplates.PersonalReportBatch(ctx, new[] { PrintTemplates.E(ctx.ClassName) });

        return PrintTemplates.PersonalReportBatch(ctx, pages);
    }

    private string BuildRecovery(PrintContext ctx)
    {
        // 恢复码明文只在生成那一刻出现过一次，库里只有哈希 —— 所以这里不能"重印旧卡"，
        // 只能提示去设置页重新生成一张（旧的立刻失效）。这是刻意的。
        RecoveryHint.Text = Services.Settings.HasRecovery
            ? "本机已设过恢复码。明文只显示一次，没法重印。" +
              "要新的一张，去「设置」重新生成，然后立刻打印。"
            : "还没生成恢复码。去「设置」生成，再回来打印。";

        return PrintTemplates.RecoveryCard(Services.ClassName, "—— —— —— ——", Services.Settings.ClassCode,
            DateTime.Now);
    }

    private string BuildArchive(PrintContext ctx, Period period, int studentCount)
    {
        var entries = Services.Entries.Entries(period.Id, 100_000);
        var path = Path.Combine(Database.BackupRoot, $"归档-{period.Name}.xlsx");
        ArchiveHint.Text = $"当前周期「{period.Name}」，共 {entries.Count} 条记录、{studentCount} 名学生。" +
                           "封面校验码和归档包上的对上，就是同一份。";

        return PrintTemplates.ArchiveCover(ctx, entries.Count, studentCount, path, ctx.Checksum);
    }

    private async void OnPrint(object sender, RoutedEventArgs e)
    {
        var printer = (PrinterCombo.SelectedItem as PrintService.PrinterInfo)?.Name;
        StatusText.Text = "正在发送到打印机…";
        var (ok, message) = await PrintService.PrintAsync(Preview, printer);
        StatusText.Text = ok
            ? $"已发送到 {printer ?? "默认打印机"}（无页眉页脚）。"
            : message;
    }

    private async void OnSaveHtml(object sender, RoutedEventArgs e)
    {
        var name = SelectedSheet switch
        {
            1 => "期末总评表",
            2 => "个人成绩单",
            3 => "恢复卡",
            4 => "周期归档封面",
            _ => "班级排名表",
        };

        var path = await PrintService.SaveHtmlAsync(_html, $"{Services.ClassName}-{name}-{DateTime.Today:yyyyMMdd}");
        StatusText.Text = path is null ? "已取消" : "已保存到 " + path;
    }
}
