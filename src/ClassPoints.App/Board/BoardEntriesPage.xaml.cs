using ClassPoints.App.Controls;
using ClassPoints.App.Services;
using ClassPoints.Core;
using ClassPoints.Data;
using Windows.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ClassPoints.App.Theme;

namespace ClassPoints.App.Board;

/// <summary>
/// 明细（大屏）：只看当天；未认人时隐藏操作人列（教室里公开屏幕，谁扣的分不能随便露）。
/// 撤销走左滑，且不做"全滑即撤销" —— 触屏误滑概率太高。
/// </summary>
public sealed partial class BoardEntriesPage : Page
{
    private static readonly SolidColorBrush Primary = Pal.Primary;
    private static readonly SolidColorBrush Secondary = Pal.Secondary;
    private static readonly SolidColorBrush Positive = Pal.Positive;
    private static readonly SolidColorBrush Negative = Pal.Negative;
    private static readonly SolidColorBrush Danger = Pal.Danger;

    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(20) };

    public BoardEntriesPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private AppServices Services => AppServices.Instance;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Render();
        _refresh.Tick += (_, _) => Render();
        _refresh.Start();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => _refresh.Stop();

    private void OnRefreshClick(object sender, RoutedEventArgs e) => Render();

    private void Render()
    {
        var period = Services.CurrentPeriod;
        var showOperator = Services.Session.CanWrite;

        // 教室里是公开屏幕：谁扣的分不能随便露。未认人（看板/查分状态）时，
        // 操作人列连宽度一起收掉，不是只把文字变透明。
        OperatorColumn.Width = showOperator ? new GridLength(130) : new GridLength(0);
        OperatorHeader.Visibility = showOperator ? Visibility.Visible : Visibility.Collapsed;

        var from = Clock.StartOfDay(DateTime.Today);
        var all = Services.Entries.Entries(period.Id, limit: 400)
            .Where(e => e.OccurredAt >= from)
            .ToList();

        EntryList.Items.Clear();
        CountText.Text = all.Count == 0 ? "" : $"共 {all.Count} 条";

        foreach (var entry in all)
        {
            var row = BuildRow(entry, showOperator);
            var swipe = new SwipeControl { Content = row };
            var items = new SwipeItems { Mode = SwipeMode.Execute };
            var undo = new SwipeItem
            {
                Text = "撤销",
                IconSource = new SymbolIconSource { Symbol = Symbol.Undo },
                Background = Danger,
            };
            undo.Invoked += async (_, _) => await UndoEntryAsync(entry);
            items.Add(undo);
            swipe.RightItems = items;
            EntryList.Items.Add(swipe);
        }

        var empty = all.Count == 0;
        Empty.Show(empty);
        if (empty)
        {
            // 这页只看当天，空状态就别说"这个周期"了 —— 标题写着"今天的记录"，正文换个口径会让人以为筛选出错
            Empty.Configure("\uE8A5",
                "今天还没有记录。加的第一条分会出现在这里。",
                "去记分台", null);
            Empty.PrimaryInvoked += GotoScore;
        }
    }

    private void GotoScore() => App.Board?.Navigate(BoardNav.Score);

    private FrameworkElement BuildRow(Entry entry, bool showOperator)
    {
        var grid = new Grid
        {
            Padding = new Thickness(0, 10, 0, 10),
            Background = Pal.Card,
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });

        // 姓名列定宽：留着当星号列的话，操作人一收掉，多出来的宽度全灌进姓名列，
        // 名字缩在左边、右边空一大片。多出来的宽度应该给「项目 / 备注」。
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = showOperator ? new GridLength(130) : new GridLength(0),
        });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });

        var time = new TextBlock
        {
            Text = Clock.ToLocal(entry.OccurredAt).ToString("HH:mm"),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 18,
            Foreground = Secondary,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0),
        };
        Grid.SetColumn(time, 0);

        var name = new TextBlock
        {
            Text = string.IsNullOrEmpty(entry.StudentName) ? "—" : entry.StudentName,
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Foreground = Primary,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(name, 1);

        var detail = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        detail.Children.Add(new TextBlock
        {
            Text = entry.RuleLabel ?? "",
            FontSize = 18,
            Foreground = Primary,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var suffix = entry.DetailSuffix;
        if (!string.IsNullOrEmpty(suffix) || !string.IsNullOrEmpty(entry.Note))
            detail.Children.Add(new TextBlock
            {
                Text = string.IsNullOrEmpty(entry.Note) ? suffix : $"{entry.Note}　{suffix}".Trim(),
                FontSize = 13,
                Foreground = entry.IsReversed ? Danger : Secondary,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        Grid.SetColumn(detail, 2);

        var op = new TextBlock
        {
            Text = entry.OperatorName,
            FontSize = 15,
            Foreground = Secondary,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Visibility = showOperator ? Visibility.Visible : Visibility.Collapsed,
        };
        Grid.SetColumn(op, 3);

        var delta = new TextBlock
        {
            Text = entry.IsReversed ? "已撤销" : entry.DeltaText,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            Foreground = entry.IsReversed ? Secondary : entry.Delta >= 0 ? Positive : Negative,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        Grid.SetColumn(delta, 4);

        grid.Children.Add(time);
        grid.Children.Add(name);
        grid.Children.Add(detail);
        grid.Children.Add(op);
        grid.Children.Add(delta);
        return grid;
    }

    private async Task UndoEntryAsync(Entry entry)
    {
        if (entry.IsReversed || entry.IsReversal) return;

        var session = Services.Session;
        if (!session.CanWrite)
        {
            Toast("请先刷卡");
            return;
        }

        var rows = Services.Entries.BatchRows(entry.BatchId);
        var permission = EntryService.CheckReversePermission(rows, Services.Operator, session.IsAdmin, Clock.NowMs());

        if (permission == ReversePermission.Denied)
        {
            Toast("没有权限撤销这一批");
            return;
        }

        if (permission == ReversePermission.NeedsPassword)
        {
            var shell = App.Board;
            if (shell is null) return;
            var ok = await shell.AskPinAsync("撤销别人的记录", "需要管理密码", requireManager: true);
            if (!ok) return;
            session.EnterAdmin();
        }

        try
        {
            Services.Entries.ReverseSingle(entry.Id, Services.Operator, "board.entries");
            Sounds.Play(Cue.Undo);
            Toast($"已撤销 {entry.StudentName} 的 {entry.RuleLabel} {entry.DeltaText}");
            Render();
        }
        catch (Exception ex)
        {
            Services.LogError("undo-entry", ex);
            Toast("撤销失败：" + ex.Message);
        }
    }

    private void Toast(string message) => App.Board?.Toast(message);
}
