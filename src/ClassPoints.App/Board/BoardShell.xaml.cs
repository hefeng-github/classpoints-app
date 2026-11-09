using Windows.UI;
using ClassPoints.App.Services;
using ClassPoints.Core;
using ClassPoints.Data;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ClassPoints.App.Theme;

namespace ClassPoints.App.Board;

/// <summary>
/// 大屏端外壳。它管的不是"页面长什么样"，而是"页面之间怎么走"：
/// 三个正交状态位、四条超时、一个常驻的读卡监听。
/// </summary>
public sealed partial class BoardShell : UserControl
{
    private static readonly SolidColorBrush ActiveBg = Pal.AccentBg;
    private static readonly SolidColorBrush ActiveFg = Pal.Accent;
    private static readonly SolidColorBrush IdleFg = Pal.Secondary;

    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private TaskCompletionSource<Member?>? _identifyTcs;
    private int _currentIndex = 1;
    private bool _paneOpen;

    public BoardShell()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public AppServices Services => AppServices.Instance;

    public BoardSession Session => Services.Session;

    public int CurrentIndex => _currentIndex;

    public string DebugFrameContent =>
        $"{ContentFrame.Content?.GetType().Name ?? "null"} " +
        $"{ContentFrame.ActualWidth}x{ContentFrame.ActualHeight} " +
        $"vis={ContentFrame.Visibility} idx={_currentIndex}";

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            LoadInternal();
        }
        catch (Exception ex)
        {
            Services.LogError("board-shell-loaded", ex);
            Toast("界面初始化失败：" + ex.Message, 8);
        }
    }

    private void LoadInternal()
    {
        Sounds.Enabled = Services.Settings.SoundOn;
        PaneClassText.Text = Services.ClassTitle;
        HeadClassText.Text = Services.ClassTitle;
        HeadModeText.Text = "教室大屏";
        RefreshClock();
        Session.Changed += OnSessionChanged;

        _tick.Tick += OnTick;
        _tick.Start();

        // 同步只在后台跑，永不挡在"加一分"的路径上
        Services.Pump.Start();

        // 常驻读卡监听：在看板前碰一下卡就直接进记分台，这是班委最常用的路径
        Services.Reader.CardDetected += OnCardDetected;
        try
        {
            Services.Reader.Start(string.IsNullOrWhiteSpace(Services.Settings.ReaderPort)
                ? null
                : Services.Settings.ReaderPort);
        }
        catch (Exception ex)
        {
            Services.LogError("reader-start", ex);
        }

        // 有身份才有记分台；匿名时停在看板（看板是停留页，不是"主页"）
        Navigate(Session.CanWrite ? BoardNav.Score : BoardNav.Wall);
        RefreshRail();
        Services.Log("大屏外壳初始化完成");
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _tick.Stop();
        Session.Changed -= OnSessionChanged;
        Services.Reader.CardDetected -= OnCardDetected;
        Services.Reader.Stop();
    }

    // ---------------- 导航 ----------------

    private void OnRailClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string tag) return;
        _ = NavigateAsync(int.Parse(tag));
    }

    private void OnPaneItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string tag) return;
        ClosePane();
        _ = NavigateAsync(int.Parse(tag));
    }

    private async Task NavigateAsync(int index)
    {
        // 记分台和事件计数都要写权限，进之前先认人
        if (index is BoardNav.Score or BoardNav.Counters && !Session.CanWrite)
        {
            var ok = await EnsureIdentifiedAsync();
            if (!ok) return;
        }
        Navigate(index);
    }

    public void Navigate(int index)
    {
        _currentIndex = index;
        Type page = index switch
        {
            BoardNav.Score => typeof(BoardScorePage),
            BoardNav.Counters => typeof(BoardCountersPage),
            BoardNav.Lookup => typeof(BoardLookupPage),
            BoardNav.Entries => typeof(BoardEntriesPage),
            BoardNav.Settings => typeof(BoardSettingsPage),
            _ => typeof(BoardWallPage),
        };

        if (ContentFrame.CurrentSourcePageType != page)
            ContentFrame.Navigate(page);

        RefreshRail();
    }

    private void RefreshRail()
    {
        // 按钮的 Tag 才是逻辑页号，显示顺序和页号不一样（记分台、事件在最上面）
        var buttons = new[] { NavScore, NavCounters, NavWall, NavLookup, NavEntries, NavSettings };
        foreach (var button in buttons)
        {
            var index = int.Parse((string)button.Tag);
            var active = index == _currentIndex;
            button.Background = active ? ActiveBg : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            if (button.Content is FontIcon icon) icon.Foreground = active ? ActiveFg : IdleFg;
        }

        HeadSyncText.Text = Session.SyncText;
        HeadClockText.Foreground = Pal.Primary;
    }

    private void OnPaneToggleClick(object sender, RoutedEventArgs e) => TogglePane();

    private void TogglePane()
    {
        _paneOpen = !_paneOpen;
        PaneLayer.Visibility = _paneOpen ? Visibility.Visible : Visibility.Collapsed;
        PaneClassText.Text = Services.ClassTitle;
    }

    private void ClosePane()
    {
        _paneOpen = false;
        PaneLayer.Visibility = Visibility.Collapsed;
    }

    private void OnPaneScrimTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e) => ClosePane();

    // ---------------- 会话与超时 ----------------

    private void OnSessionChanged()
    {
        PaneClassText.Text = Services.ClassTitle;
        HeadClassText.Text = Services.ClassTitle;
        if (!Session.CanWrite && _currentIndex is BoardNav.Score or BoardNav.Counters)
            Navigate(BoardNav.Wall);
    }

    private void OnTick(object? sender, object e)
    {
        var settings = Services.Settings;

        if (Session.ShouldAdminExit(settings.LockTimeoutSec))
        {
            Session.ExitAdmin();
            Toast("已退出管理模式，仍可记分");
        }

        // 有选中项不计时：老师框选 12 人后转身喊一句话，30 秒静默很正常
        if (Session.ShouldIdleExit(settings.IdleTimeoutSec) &&
            _currentIndex is BoardNav.Score or BoardNav.Counters)
        {
            Session.SignOut();
            Navigate(BoardNav.Wall);
            Toast("已回到看板");
        }

        RefreshClock();
        // 查分页严格计时（它自己处理，避免和班委会话抢）
    }

    /// <summary>状态条上的时钟。每秒一次，只在秒数变化时才写 Text（避免无谓的布局）。</summary>
    private string _clockMinute = "";

    private void RefreshClock()
    {
        var now = DateTime.Now;
        var minute = now.ToString("HH:mm");
        if (minute != _clockMinute)
        {
            _clockMinute = minute;
            HeadClockText.Text = minute;
            HeadDateText.Text = now.ToString("M 月 d 日") + " " + Weekday(now.DayOfWeek);
        }
    }

    private static string Weekday(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "周一",
        DayOfWeek.Tuesday => "周二",
        DayOfWeek.Wednesday => "周三",
        DayOfWeek.Thursday => "周四",
        DayOfWeek.Friday => "周五",
        DayOfWeek.Saturday => "周六",
        _ => "周日",
    };

    // ---------------- 认人 ----------------

    private void OnCardDetected(CardRead read)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var member = Services.Members.FindByCard(read.Uid);
            HandleCard(member, read);
        });
    }

    private void HandleCard(Member? member, CardRead read)
    {
        if (member is null)
        {
            Toast($"这张卡还没登记（{read.ShortUid}）");
            if (_identifyTcs is not null)
                IdentifyHint.Text = $"这张卡（{read.ShortUid}）没登记。到电脑端「班委管理」加一下。";
            Sounds.Play(Cue.Error);
            return;
        }

        if (_identifyTcs is not null)
        {
            CompleteIdentify(member);
            return;
        }

        // 已认人时再刷一张卡 = 换人，不是叠加。不提示的话，后一个人会以为前一个人的分是自己的。
        if (Session.Current is not null && Session.Current.Id != member.Id)
        {
            _ = ConfirmSwitchAsync(member);
            return;
        }

        Session.Identify(member);
        Sounds.Play(Cue.Identify);
        Toast($"{member.DisplayName} · 可以开始了");
        Navigate(BoardNav.Score);
    }

    private async Task ConfirmSwitchAsync(Member next)
    {
        var previous = Session.Current?.DisplayName ?? "上一个人";
        var confirmed = await ConfirmAsync("换人", $"当前是 {previous}，要换成 {next.DisplayName} 吗？",
            $"换成 {next.DisplayName}", "保持当前");
        if (!confirmed) return;

        Session.Identify(next);
        Sounds.Play(Cue.Identify);
        Toast($"已切换为 {next.DisplayName}");
        Navigate(BoardNav.Score);
    }

    /// <summary>页面在写操作前调用。已认人直接返回 true。</summary>
    public async Task<bool> EnsureIdentifiedAsync(bool requireAdmin = false)
    {
        if (Session.CanWrite && (!requireAdmin || Session.IsAdmin)) return true;

        if (requireAdmin && Session.CanWrite)
        {
            var ok = await AskPinAsync("管理模式密码", "改名单、改分值、撤销记录都要这一步", requireManager: true);
            if (ok) Session.EnterAdmin();
            return ok;
        }

        var member = await ShowIdentifyOverlayAsync();
        if (member is null) return false;

        Session.Identify(member, asAdmin: requireAdmin);
        return !requireAdmin || Session.IsAdmin;
    }

    private Task<Member?> ShowIdentifyOverlayAsync()
    {
        _identifyTcs = new TaskCompletionSource<Member?>();
        IdentifyTitle.Text = "请刷卡";
        IdentifySub.Text = "刷卡，或输入管理密码。";
        IdentifyHint.Text = "没卡？到电脑端「设置 → 班委管理」登记。";
        IdentifyLayer.Visibility = Visibility.Visible;

        // 30 秒没人理就收起，避免一个模态框永远压在屏幕上
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        timeout.Tick += (_, _) =>
        {
            timeout.Stop();
            if (_identifyTcs is not null) CompleteIdentify(null);
        };
        timeout.Start();

        return _identifyTcs.Task;
    }

    private void CompleteIdentify(Member? member)
    {
        var tcs = _identifyTcs;
        _identifyTcs = null;
        IdentifyLayer.Visibility = Visibility.Collapsed;

        if (member is not null)
        {
            Session.Identify(member);
            Sounds.Play(Cue.Identify);
        }
        tcs?.TrySetResult(member);
    }

    private void OnIdentifyCancelClick(object sender, RoutedEventArgs e) => CompleteIdentify(null);

    private async void OnIdentifyPinClick(object sender, RoutedEventArgs e)
    {
        var ok = await AskPinAsync("管理模式密码", "输入 6 位数字", requireManager: true);
        if (!ok) return;

        var member = Session.Current;
        CompleteIdentify(member);
        if (member is not null)
        {
            Session.EnterAdmin();
            Navigate(BoardNav.Score);
        }
    }

    // ---------------- 复用的小工具 ----------------

    /// <summary>PIN 校验。返回 true 表示通过并已认人（管理态由调用方决定）。</summary>
    public Task<bool> AskPinAsync(string title, string sub, bool requireManager)
    {
        var tcs = new TaskCompletionSource<bool>();

        Pin.ShowVerify(title, sub, pin =>
        {
            if (Session.IsPinLocked) return $"请等 {Session.PinLockSecondsLeft} 秒再试";

            var member = Session.VerifyPin(pin);
            if (member is null) return "密码不对";

            if (Session.Current is null) Session.Identify(member);
            if (requireManager && !member.IsManager) return "这不是班主任的密码";

            if (Session.Current is not null && Session.Current.Id != member.Id)
                Session.Identify(member);

            return null;
        });

        void OnCompleted(string _)
        {
            Pin.Completed -= OnCompleted;
            Pin.Cancelled -= OnCancelled;
            tcs.TrySetResult(true);
        }

        void OnCancelled()
        {
            Pin.Completed -= OnCompleted;
            Pin.Cancelled -= OnCancelled;
            tcs.TrySetResult(false);
        }

        Pin.Completed += OnCompleted;
        Pin.Cancelled += OnCancelled;
        return tcs.Task;
    }

    /// <summary>只收数字不校验（设置新密码时用两次）。</summary>
    public Task<string?> AskDigitsAsync(string title, string sub, int digits = 6)
    {
        var tcs = new TaskCompletionSource<string?>();
        Pin.ShowInput(title, sub, digits);

        void OnCompleted(string pin)
        {
            Pin.Completed -= OnCompleted;
            Pin.Cancelled -= OnCancelled;
            tcs.TrySetResult(pin);
        }

        void OnCancelled()
        {
            Pin.Completed -= OnCompleted;
            Pin.Cancelled -= OnCancelled;
            tcs.TrySetResult(null);
        }

        Pin.Completed += OnCompleted;
        Pin.Cancelled += OnCancelled;
        return tcs.Task;
    }

    public async Task<bool> ConfirmAsync(string title, string message, string primary, string close)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            PrimaryButtonText = primary,
            CloseButtonText = close,
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary;
    }

    public void Toast(string message, double seconds = 3) => ToastLayer.Show(message, seconds);
}
