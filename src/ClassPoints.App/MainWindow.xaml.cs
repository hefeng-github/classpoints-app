using Windows.UI;
using ClassPoints.App.Services;
using ClassPoints.Core;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using ClassPoints.App.Theme;

namespace ClassPoints.App;

/// <summary>
/// 窗口外壳。同一个 exe 三种形态：大屏端、电脑端、只读查询端，形态由首次启动向导定。
/// 窗口的位置、尺寸写进 settings，下次启动还原；超出当前屏幕时夹回可见区域。
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly ContentDialog? _startupDialog;
    private bool _started;

    public MainWindow()
    {
        InitializeComponent();
        Root.Loaded += OnRootLoaded;
        Closed += OnClosed;
    }

    public MainWindow(ContentDialog dialog) : this()
    {
        _startupDialog = dialog;
    }

    private AppServices Services => App.Services;

    private async void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        if (_started) return;
        _started = true;

        if (_startupDialog is not null)
        {
            _startupDialog.XamlRoot = Root.XamlRoot;
            await _startupDialog.ShowAsync();
            Application.Current.Exit();
            return;
        }

        if (Environment.GetEnvironmentVariable("CP_NORESIZE") != "1") RestoreWindow();

        // 先定外观再挂界面：否则第一帧会用错的色，切换时闪一下
        App.ApplyTheme(Services.Settings.Theme);

        // 形态与配置包都归向导管。以前这两件事是命令行参数，老师换台机器就得改快捷方式，
        // 忘了改就静默进错形态（大屏端没有导入入口，电脑端没有记分台），所以收进向导里问一次。
        var snapshot = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CP_SNAP"));
        if (snapshot)
        {
            // 验收脚本：形态由 CP_MODE 指定（CP_MODE 不算"定过"，所以单看 ModeKnown 不够），
            // 两者都没有就走向导 —— 截向导自己的页面。
            if (App.ModeKnown || App.Options.ModeOverride is not null) ShowShell();
            else ShowOnboarding();
            return;
        }

        if (!App.ModeKnown || !Services.Settings.OnboardingDone)
            ShowOnboarding();
        else
            ShowShell();
    }

    // ---------------- 路由 ----------------

    private void ShowShell()
    {
        switch (App.Mode)
        {
            case AppMode.Board:
            {
                Services.Log("创建大屏外壳");
                var shell = new Board.BoardShell();
                App.Board = shell;
                App.Desk = null;
                App.Viewer = null;
                Title = "班级积分 · 大屏";
                Host.Content = shell;
                Services.Log("大屏外壳已挂载");
                break;
            }

            case AppMode.Viewer:
            {
                Services.Log("创建只读端外壳");
                var shell = new Viewer.ViewerShell();
                App.Viewer = shell;
                App.Board = null;
                App.Desk = null;
                Title = "班级积分 · 只读查询";
                Host.Content = shell;
                break;
            }

            default:
            {
                Services.Log("创建电脑端外壳");
                var shell = new Desk.DeskShell();
                App.Desk = shell;
                App.Board = null;
                App.Viewer = null;
                Title = "班级积分 · " + Services.ClassName;
                Host.Content = shell;
                break;
            }
        }

        AppWindow.Title = Title;
        MaybeRunSnapshots();
    }

    /// <summary>
    /// 验收通道：CP_SNAP=&lt;目录&gt; 时逐页自截图后退出。
    /// 远程会话/虚拟显示器上桌面截图拿不到 WinUI 的合成结果，只能让程序自己渲染。
    /// </summary>
    private void MaybeRunSnapshots()
    {
        var snapDir = Environment.GetEnvironmentVariable("CP_SNAP");
        if (!string.IsNullOrWhiteSpace(snapDir)) RunSnapshots(snapDir);
    }

    private async void RunSnapshots(string dir)
    {
        try
        {
            await Task.Delay(1200);

            if (Host.Content is Onboarding.OnboardingView wizard)
            {
                for (var i = 0; i < wizard.StepCount; i++)
                {
                    wizard.DebugGoToStep(i);
                    await Task.Delay(1200);
                    if (await Snapshot.SaveAsync(Root, Path.Combine(dir, $"wizard-{i + 1}.png")))
                        Services.Log($"快照 wizard-{i + 1}");
                }
            }
            else if (App.Board is { } board)
            {
                // 记分台要写权限，先造一个班主任身份（只影响内存会话，不落库）
                var who = new ClassPoints.Core.Member
                {
                    Id = "snapshot-owner",
                    DisplayName = "班主任",
                    Role = ClassPoints.Core.MemberRole.Owner,
                };
                Services.Session.Identify(who, asAdmin: true);

                (int Index, string Name)[] pages =
                [
                    (BoardNav.Wall, "board-1-wall"),
                    (BoardNav.Score, "board-2-score"),
                    (BoardNav.Counters, "board-3-counters"),
                    (BoardNav.Lookup, "board-4-lookup"),
                    (BoardNav.Entries, "board-5-entries"),
                    (BoardNav.Settings, "board-6-settings"),
                ];

                foreach (var (index, name) in pages)
                {
                    board.Navigate(index);
                    await Task.Delay(1400);
                    if (await Snapshot.SaveAsync(Root, Path.Combine(dir, name + ".png")))
                        Services.Log("快照 " + name);
                }

                await ScrollToBottomAsync();
                if (await Snapshot.SaveAsync(Root, Path.Combine(dir, "board-6b-settings-bottom.png")))
                    Services.Log("快照 board-6b-settings-bottom");

                // 再看一眼"未认人"的明细：操作人列必须整列消失（教室是公开屏幕）
                Services.Session.SignOut();
                board.Navigate(BoardNav.Entries);
                await Task.Delay(1400);
                if (await Snapshot.SaveAsync(Root, Path.Combine(dir, "board-5b-entries-anon.png")))
                    Services.Log("快照 board-5b-entries-anon");
            }
            else if (App.Viewer is { } viewer)
            {
                (int Index, string Name)[] pages =
                [
                    (ViewerNav.Lookup, "viewer-1-lookup"),
                    (ViewerNav.Rank, "viewer-2-rank"),
                    (ViewerNav.Counters, "viewer-3-counters"),
                ];
                foreach (var (index, name) in pages)
                {
                    viewer.Navigate(index);
                    await Task.Delay(1400);
                    if (await Snapshot.SaveAsync(Root, Path.Combine(dir, name + ".png")))
                        Services.Log("快照 " + name);
                }
            }
            else if (App.Desk is { } desk)
            {
                string[] names = ["desk-1-home", "desk-2-students", "desk-3-counters", "desk-4-exams",
                                  "desk-5-print", "desk-6-periods", "desk-7-settings"];
                for (var i = 0; i < names.Length; i++)
                {
                    desk.Navigate(i);
                    // 打印页的预览是 WebView2 现渲染的，1.5 秒只够它起进程，抓到的会是"正在生成预览"。
                    await Task.Delay(i == 4 ? 5000 : 1500);
                    if (await Snapshot.SaveAsync(Root, Path.Combine(dir, names[i] + ".png")))
                        Services.Log("快照 " + names[i]);
                }

                // 设置页比一屏长，底部几节（同步、其它）首屏看不到 —— 滚到底再抓一张
                await ScrollToBottomAsync();
                if (await Snapshot.SaveAsync(Root, Path.Combine(dir, "desk-7b-settings-sync.png")))
                    Services.Log("快照 desk-7b-settings-sync");

                // 当场切一次主题：不重建页面，验证共享画刷与 ThemeResource 都跟着变
                var before = Services.Settings.Theme;
                App.ApplyTheme(before == Theme.AppTheme.Dark ? Theme.AppTheme.Light : Theme.AppTheme.Dark);
                await Task.Delay(900);
                if (await Snapshot.SaveAsync(Root, Path.Combine(dir, "desk-8-theme-switched.png")))
                    Services.Log("快照 desk-8-theme-switched");
                App.ApplyTheme(before);
            }

            // 退出前先离开打印页：WebView2 还活着时直接 Exit 会在终结器里崩。
            App.Desk?.Navigate(DeskNav.Home);
            await Task.Delay(400);

            Services.Log("快照完成");
            Application.Current.Exit();
        }
        catch (Exception ex)
        {
            Services.LogError("snapshots", ex);
            Application.Current.Exit();
        }
    }

    /// <summary>
    /// 把可见范围里最长的那个 ScrollViewer 滚到底。自截图只能拍视口，
    /// 长页面必须滚一下才看得到底部。
    /// </summary>
    private async Task ScrollToBottomAsync()
    {
        ScrollViewer? best = null;
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(Root);
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            var count = VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(node, i);
                if (child is ScrollViewer sv && sv.ScrollableHeight > (best?.ScrollableHeight ?? 0))
                    best = sv;
                queue.Enqueue(child);
            }
        }

        if (best is null) return;
        best.ChangeView(null, best.ScrollableHeight, null, disableAnimation: true);
        await Task.Delay(500);
    }

    /// <summary>设置页换形态：整个外壳换掉，进程不重启。数据、会话、日志都还在。</summary>
    public void SwitchToMode(AppMode mode)
    {
        App.SetMode(mode);
        Services.Settings.OnboardingDone = true;
        ShowShell();
    }

    /// <summary>重新跑一遍向导：形态清空，向导会从"这台电脑做什么"重新问。</summary>
    public void RunWizard()
    {
        App.ForgetMode();
        ShowOnboarding();
    }

    private void ShowOnboarding()
    {
        var onboarding = new Onboarding.OnboardingView();
        onboarding.Completed += (_, _) =>
        {
            Services.Settings.OnboardingDone = true;
            ShowShell();
        };
        Host.Content = onboarding;
        MaybeRunSnapshots();
    }

    // ---------------- 窗口尺寸与标题栏 ----------------

    private void RestoreWindow()
    {
        var settings = Services.Settings;
        var (w, h, x, y) = settings.GetWindow();

        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var board = App.Mode == AppMode.Board;

        if (w <= 0 || h <= 0)
        {
            w = board ? System.Math.Min(1920, area.Width) : System.Math.Min(1360, area.Width);
            h = board ? System.Math.Min(1048, area.Height) : System.Math.Min(880, area.Height);
        }

        w = System.Math.Clamp(w, board ? 1024 : 900, area.Width);
        h = System.Math.Clamp(h, board ? 700 : 600, area.Height);

        var left = x is int px ? System.Math.Clamp(px, area.X, area.X + area.Width - w) : area.X + (area.Width - w) / 2;
        var top = y is int py ? System.Math.Clamp(py, area.Y, area.Y + area.Height - h) : area.Y + (area.Height - h) / 2;

        AppWindow.MoveAndResize(new RectInt32(left, top, w, h));
    }

    /// <summary>外观切换后重画一次标题栏按钮的深浅，否则浅色主题上会留三个白图标。</summary>
    public void RefreshTitleBar() => ConfigureTitleBar();

    private void ConfigureTitleBar()
    {
        if (!AppWindowTitleBar.IsCustomizationSupported()) return;

        var bar = AppWindow.TitleBar;
        bar.BackgroundColor = Colors.Transparent;
        bar.InactiveBackgroundColor = Colors.Transparent;
        bar.ButtonBackgroundColor = Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Colors.Transparent;

        if (Pal.IsDark)
        {
            bar.ButtonForegroundColor = Colors.White;
            bar.ButtonInactiveForegroundColor = Pal.Secondary.Color;
            bar.ButtonHoverBackgroundColor = Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF);
            bar.ButtonPressedBackgroundColor = Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF);
        }
        else
        {
            bar.ButtonForegroundColor = Pal.Primary.Color;
            bar.ButtonInactiveForegroundColor = Pal.Secondary.Color;
            bar.ButtonHoverBackgroundColor = Color.FromArgb(0x14, 0x00, 0x00, 0x00);
            bar.ButtonPressedBackgroundColor = Color.FromArgb(0x22, 0x00, 0x00, 0x00);
        }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        try
        {
            var pos = AppWindow.Position;
            var size = AppWindow.Size;
            Services.Settings.SetWindow(size.Width, size.Height, pos.X, pos.Y);
            Services.Reader.Dispose();
            Services.Log("窗口关闭");
        }
        catch (Exception ex)
        {
            Services?.LogError("window-close", ex);
        }
    }
}
