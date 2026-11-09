using ClassPoints.App.Services;
using ClassPoints.Core;
using Microsoft.UI.Xaml;

namespace ClassPoints.App;

public partial class App : Application
{
    public static AppServices Services { get; private set; } = null!;

    public static StartupOptions Options { get; private set; } = new();

    /// <summary>这台机器这次以什么形态运行。启动时从设置里读，向导里选定后当场改。</summary>
    public static AppMode Mode { get; private set; } = AppMode.Desk;

    /// <summary>形态是否已经定过。false 时启动直接进向导，而不是猜一个。</summary>
    public static bool ModeKnown { get; private set; }

    public static void SetMode(AppMode mode)
    {
        Mode = mode;
        ModeKnown = true;
        Services.Settings.AppMode = mode;
    }

    /// <summary>只给验收脚本用：不改库，只改这一次的形态。</summary>
    public static void OverrideMode(AppMode mode) => Mode = mode;

    /// <summary>设置页的「重新运行首次配置」：把形态清掉，让向导重新问一遍。</summary>
    public static void ForgetMode()
    {
        Services.Settings.AppMode = null;
        Services.Settings.OnboardingDone = false;
        ModeKnown = false;
    }

    public static Board.BoardShell? Board { get; set; }

    public static Desk.DeskShell? Desk { get; set; }

    public static Viewer.ViewerShell? Viewer { get; set; }

    public static MainWindow? Window { get; set; }

    /// <summary>
    /// 切换外观。三件事必须一起做，少一件就会出现"一半深一半浅"：
    /// ① 记住偏好；② 改 C# 共享画刷的颜色（已经挂在界面上的元素会跟着变）；
    /// ③ 改根元素的 RequestedTheme（让 XAML 里的 ThemeResource 重新解析）。
    /// </summary>
    public static void ApplyTheme(Theme.AppTheme theme)
    {
        Services.Settings.Theme = theme;
        Theme.Pal.Apply(theme);

        if (Window?.Content is FrameworkElement root)
        {
            root.RequestedTheme = theme switch
            {
                Theme.AppTheme.Light => ElementTheme.Light,
                Theme.AppTheme.Dark => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };
        }

        Window?.RefreshTitleBar();
    }

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            try
            {
                Services?.LogError("unhandled", e.Exception);
            }
            catch (Exception)
            {
                // 日志本身出错就放弃，不能让异常处理再抛一次
            }
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Options = StartupOptions.Parse(Environment.GetCommandLineArgs());

        try
        {
            Services = AppServices.Initialize(Options);
        }
        catch (Exception ex)
        {
            // 库打不开时不能静默退出，否则老师看到的是"点了没反应"
            ShowFatal(ex);
            return;
        }

        // 形态：设置里定过就用它；验收脚本的 CP_MODE 只覆盖这一次，不算"定过"
        if (Services.Settings.AppMode is AppMode stored)
        {
            Mode = stored;
            ModeKnown = true;
        }
        if (Options.ModeOverride is AppMode forced) Mode = forced;

        try
        {
            Window = new MainWindow();
            Window.Activate();
            Services.Log("主窗口已创建");
        }
        catch (Exception ex)
        {
            Services.LogError("create-window", ex);
            ShowFatal(ex);
        }
    }

    private static void ShowFatal(Exception ex)
    {
        var dialog = new Microsoft.UI.Xaml.Controls.ContentDialog
        {
            Title = "数据库打不开",
            Content = $"{ex.Message}\n\n数据库位置：{Data.Database.DefaultDbPath}\n\n" +
                      "文件被占用就关掉那个程序再启动；损坏就到备份目录挑最近的复制回来。",
            CloseButtonText = "退出",
        };

        var window = new MainWindow(dialog);
        window.Activate();
    }
}
