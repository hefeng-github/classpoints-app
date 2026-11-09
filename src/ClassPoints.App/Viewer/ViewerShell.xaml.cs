using ClassPoints.App.Services;
using ClassPoints.App.Theme;
using ClassPoints.Core;
using ClassPoints.Data.Sync;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ClassPoints.App.Viewer;

/// <summary>
/// 只读查询端外壳。
///
/// 这个形态在代码上最重要的一条：**没有任何写入口**。
/// 页面里不出现 +1、导入、撤销、保存这些按钮，外壳也不建会话、不启读卡器。
/// 后台仍会跑同步泵 —— 那是把数据拉下来，不是编辑。
/// </summary>
public sealed partial class ViewerShell : UserControl
{
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(5) };
    private int _index = -1;

    public ViewerShell()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private AppServices Services => AppServices.Instance;

    public int CurrentIndex => _index;

    public void Navigate(int index)
    {
        var page = index switch
        {
            ViewerNav.Rank => typeof(ViewerRankPage),
            ViewerNav.Counters => typeof(ViewerCountersPage),
            _ => typeof(ViewerLookupPage),
        };

        _index = index;
        if (ContentFrame.CurrentSourcePageType != page) ContentFrame.Navigate(page);
        RefreshChrome();
    }

    public void Toast(string message, double seconds = 3) => ToastLayer.Show(message, seconds);

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_index < 0) Navigate(ViewerNav.Lookup);
        Services.Pump.Start();
        RefreshChrome();
        _tick.Tick += OnTick;
        _tick.Start();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _tick.Stop();
        _tick.Tick -= OnTick;
    }

    /// <summary>时钟和同步状态每 5 秒刷一次就够了：这是查询机，不是秒表。</summary>
    private void OnTick(object? sender, object e) => RefreshChrome();

    private void OnTabClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string tag) return;
        Navigate(int.Parse(tag));
    }

    private void RefreshChrome()
    {
        ClassText.Text = Services.ClassName;
        PeriodText.Text = Services.ClassTitle.Replace(Services.ClassName + " · ", "");
        ClockText.Text = DateTime.Now.ToString("HH:mm");
        SyncText.Text = Services.Sync.IsConfigured ? Services.Sync.LastSyncText : "本机数据";

        var tabs = new[] { TabLookup, TabRank, TabCounters };
        for (var i = 0; i < tabs.Length; i++)
        {
            var active = i == _index;
            tabs[i].Background = active ? Pal.AccentBg : Pal.Card;
            tabs[i].Foreground = active ? Pal.Accent : Pal.Secondary;
            tabs[i].BorderBrush = active ? Pal.Accent : Pal.Stroke;
            tabs[i].BorderThickness = new Thickness(active ? 2 : 1);
        }
    }

    /// <summary>
    /// 数据源：只读端唯一会"配置"的地方，而且只涉及"从哪拉"，不涉及"改什么"。
    /// </summary>
    private async void OnDataSource(object sender, RoutedEventArgs e)
    {
        var credentials = new SyncStore().Load();
        var status = new TextBlock
        {
            Text = credentials is null
                ? "现在看的是本机数据。要连云端，填下面三项。"
                : $"已连接 {credentials.BaseUrl} · 班级码 {credentials.ClassCode}\n{Services.Sync.LastSyncText}",
            Style = (Style)Application.Current.Resources["DeskSecondaryText"],
            TextWrapping = TextWrapping.Wrap,
        };

        var url = new TextBox
        {
            Header = "云端地址",
            Text = credentials?.BaseUrl ?? "",
            PlaceholderText = "https://classpoints.xxx.workers.dev",
            Width = 380,
        };
        var code = new TextBox { Header = "班级码", Text = credentials?.ClassCode ?? "", Width = 180 };
        var pin = new PasswordBox { Header = "配对口令（已配过就不用填）", Width = 220, PasswordChar = "●" };

        var pull = new Button { Content = "立即拉取", Style = (Style)Application.Current.Resources["DeskButton"] };
        var pair = new Button { Content = "配对", Style = (Style)Application.Current.Resources["DeskPrimaryButton"] };

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(status);
        panel.Children.Add(new TextBlock
        {
            Text = "只读端只拉不推：在这里配对不会把本机的任何东西写到云端。",
            Style = (Style)Application.Current.Resources["DeskSecondaryText"],
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(url);
        panel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children = { code, pin },
        });
        panel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { pair, pull },
        });

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "数据源",
            Content = panel,
            CloseButtonText = "关闭",
        };

        pair.Click += async (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(url.Text) || string.IsNullOrWhiteSpace(code.Text))
            {
                status.Text = "地址和班级码都要填。";
                return;
            }

            status.Text = "配对中…";
            try
            {
                var report = await Services.Sync.PairAsync(url.Text.Trim(), code.Text.Trim(), pin.Password, "desk");
                status.Text = report.Ok
                    ? "已配对。点「立即拉取」拉一次。"
                    : "配对失败：" + (report.Error ?? "未知原因");
                RefreshChrome();
            }
            catch (Exception ex)
            {
                Services.LogError("viewer-pair", ex);
                status.Text = "配对失败：" + ex.Message;
            }
        };

        pull.Click += async (_, _) =>
        {
            status.Text = "拉取中…";
            try
            {
                var report = await Services.Sync.RunOnceAsync();
                status.Text = report.Ok
                    ? $"已拉取 {report.Pulled} 条。"
                    : "拉取失败：" + (report.Error ?? "未知原因");
                RefreshChrome();
                Navigate(_index);
            }
            catch (Exception ex)
            {
                Services.LogError("viewer-pull", ex);
                status.Text = "拉取失败：" + ex.Message;
            }
        };

        await dialog.ShowAsync();
        RefreshChrome();
    }
}
