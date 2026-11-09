using ClassPoints.App.Services;
using ClassPoints.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ClassPoints.App.Theme;

namespace ClassPoints.App.Desk;

/// <summary>
/// 电脑端外壳。它没有会话态、没有超时 —— 坐在办公室里的人是"登录着"的，
/// 但每个页面的动作都要求明确点一下（保存、导入、重置），不做自动提交。
/// </summary>
public sealed partial class DeskShell : UserControl
{
    private static readonly SolidColorBrush ActiveBg = Pal.AccentBg;
    private static readonly SolidColorBrush ActiveFg = Pal.Accent;
    private static readonly SolidColorBrush IdleFg = Pal.Secondary;
    private static readonly SolidColorBrush Transparent = new(Windows.UI.Color.FromArgb(0x00, 0, 0, 0));

    private int _index = -1;

    public DeskShell()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    public AppServices Services => AppServices.Instance;

    public int CurrentIndex => _index;

    /// <summary>页面之间互相跳用（比如总览里的「导入名单」直接跳名单页）。</summary>
    public void Navigate(int index)
    {
        var page = index switch
        {
            DeskNav.Students => typeof(DeskStudentsPage),
            DeskNav.Counters => typeof(DeskCountersPage),
            DeskNav.Exams => typeof(DeskExamsPage),
            DeskNav.Print => typeof(DeskPrintPage),
            DeskNav.Periods => typeof(DeskPeriodsPage),
            DeskNav.Settings => typeof(DeskSettingsPage),
            _ => typeof(DeskHomePage),
        };

        _index = index;
        if (DeskFrame.CurrentSourcePageType != page) DeskFrame.Navigate(page);
        Refresh();
    }

    public void Toast(string message, double seconds = 3) => ToastLayer.Show(message, seconds);

    /// <summary>设置页改了形态之后重挂外壳：同一个 exe 换角色，不用重开程序。</summary>
    public void ReloadShell()
    {
        _index = -1;
        Navigate(DeskNav.Home);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_index < 0) Navigate(DeskNav.Home);
        Services.Pump.Start();
        Refresh();
    }

    private void OnNavClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string tag) return;
        Navigate(int.Parse(tag));
    }

    private void Refresh()
    {
        ClassNameText.Text = Services.ClassName;
        PeriodText.Text = Services.ClassTitle.Replace(Services.ClassName + " · ", "");
        ModeText.Text = "班主任电脑 · 本机存储";

        var buttons = new[] { NavHome, NavStudents, NavCounters, NavExams, NavPrint, NavPeriods, NavSettings };
        for (var i = 0; i < buttons.Length; i++)
        {
            var active = i == _index;
            buttons[i].Background = active ? ActiveBg : Transparent;
            buttons[i].Foreground = active ? ActiveFg : IdleFg;
            // 展开态下内层 TextBlock 不会继承按钮前景色，得单独设一次
            if (buttons[i].Content is StackPanel sp)
            {
                if (sp.Children.Count > 0 && sp.Children[0] is FontIcon icon)
                    icon.Foreground = active ? ActiveFg : IdleFg;
                if (sp.Children.Count > 1 && sp.Children[1] is TextBlock tb)
                    tb.Foreground = active ? ActiveFg : IdleFg;
            }
        }

        var last = Services.Settings.LastBackupAt;
        BackupText.Text = last <= 0
            ? "还没有备份"
            : $"上次备份 {Clock.LocalDate(last):MM-dd HH:mm}";

        VersionText.Text = "班级积分 v" + AppServices.AppVersion + " · 本机存储";
    }
}
