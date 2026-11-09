using Windows.UI;
using ClassPoints.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using ClassPoints.App.Theme;

namespace ClassPoints.App.Controls;

/// <summary>
/// 自绘数字键盘。系统软键盘有三个毛病：弹出慢、遮半屏、样式脱节，
/// 而一体机常常根本没配键盘 —— 所以分数与密码都走这一套。
/// </summary>
public sealed partial class NumberPad : UserControl
{
    private static readonly SolidColorBrush Normal = Pal.Stroke;
    private static readonly SolidColorBrush Warn = Pal.Warn;
    private static readonly SolidColorBrush SelectedBg = Pal.AccentBg;
    private static readonly SolidColorBrush TagBg = Pal.TileBg;
    private static readonly SolidColorBrush TextPrimary = Pal.Primary;
    private static readonly SolidColorBrush TextSecondary = Pal.Secondary;

    private Rule? _rule;
    private int _peopleCount;
    private int _raw;
    private bool _negative;
    private int _alreadyInPeriod;
    private readonly List<(NoteTemplate Note, bool Selected, Button Chip)> _chips = new();

    public NumberPad()
    {
        InitializeComponent();
    }

    /// <summary>确定：delta 是最终计入值，note 是拼好的理由文本。</summary>
    public event Action<int, string?>? Confirmed;

    /// <summary>超出单次上限、用户要求按原值计入 —— 上层先要管理密码，再调 <see cref="ConfirmForce"/>。</summary>
    public event Action<int, string?>? OverCapRequested;

    public event Action? Cancelled;

    public bool IsOpen => Visibility == Visibility.Visible;

    private int EffectiveValue
    {
        get
        {
            if (_rule is null) return _raw;
            if (!_rule.HasCap || _rule.CapScope != CapScope.Single || _rule.CapValue is not int cap) return _raw;
            if (_raw > 0 && _raw > cap) return cap;
            return _raw;
        }
    }

    public int SignedValue => _negative ? -EffectiveValue : EffectiveValue;

    public int RawSignedValue => _negative ? -_raw : _raw;

    /// <summary>
    /// 打开键盘：预填该类型默认分并全选（想给 8 直接按 8），理由标签按使用频次排。
    /// </summary>
    public void Show(Rule rule, int peopleCount, IReadOnlyList<NoteTemplate> tags, int alreadyInPeriod = 0)
    {
        _rule = rule;
        _peopleCount = peopleCount;
        _alreadyInPeriod = alreadyInPeriod;
        _negative = rule.Delta < 0;
        _raw = System.Math.Abs(rule.Delta);

        TitleText.Text = rule.Label;
        BuildTags(tags);
        Refresh();
        Visibility = Visibility.Visible;
    }

    public void Hide()
    {
        Visibility = Visibility.Collapsed;
        _chips.Clear();
        TagPanel.Children.Clear();
    }

    private void BuildTags(IReadOnlyList<NoteTemplate> tags)
    {
        TagPanel.Children.Clear();
        _chips.Clear();

        foreach (var note in tags.Take(8))
        {
            var chip = new Button
            {
                Content = note.Text,
                Tag = note,
                Height = 40,
                MinWidth = 80,
                Padding = new Thickness(14, 0, 14, 0),
                CornerRadius = new CornerRadius(20),
                FontSize = 15,
                Background = TagBg,
                Foreground = TextSecondary,
                BorderBrush = Normal,
                BorderThickness = new Thickness(1),
            };
            chip.Click += OnTagClick;
            TagPanel.Children.Add(chip);
            _chips.Add((note, false, chip));
        }
    }

    private void OnTagClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button chip || chip.Tag is not NoteTemplate note) return;

        var entry = _chips.FindIndex(c => c.Note.Id == note.Id);
        if (entry < 0) return;

        var current = _chips[entry];
        var selectedCount = _chips.Count(c => c.Selected);

        // 一次最多选两个：「背诵流利 + 声音洪亮」够了，第三个开始就不是标签而是描述了
        if (!current.Selected && selectedCount >= 2) return;

        _chips[entry] = (current.Note, !current.Selected, current.Chip);
        ApplyChipStyle(_chips[entry]);
    }

    private void ApplyChipStyle((NoteTemplate Note, bool Selected, Button Chip) chip)
    {
        chip.Chip.Background = chip.Selected ? SelectedBg : TagBg;
        chip.Chip.Foreground = chip.Selected ? TextPrimary : TextSecondary;
        chip.Chip.BorderBrush = chip.Selected ? Pal.Accent : Normal;
    }

    private string? BuildNote()
    {
        var picked = _chips.Where(c => c.Selected).Select(c => c.Note.Text).ToList();
        return picked.Count == 0 ? null : string.Join(" + ", picked);
    }

    private void OnDigitClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string digit) return;
        if (!int.TryParse(digit, out var d)) return;

        // 四位数封顶：这个系统里没有需要五位数的分值，多按一位只可能是手滑
        if (_raw > 999) return;
        _raw = _raw * 10 + d;
        if (_raw > 9999) _raw = d;
        Refresh();
    }

    private void OnBackspaceClick(object sender, RoutedEventArgs e)
    {
        _raw /= 10;
        Refresh();
    }

    private void OnSignClick(object sender, RoutedEventArgs e)
    {
        _negative = !_negative;
        Refresh();
    }

    private void Refresh()
    {
        var effective = SignedValue;
        ValueText.Text = (effective >= 0 ? "+" : "") + effective;
        ValueText.Foreground = effective >= 0
            ? Pal.Positive
            : Pal.Negative;

        var overCap = _rule is not null && _rule.HasCap && _rule.CapScope == CapScope.Single
                      && _rule.CapValue is int cap && _raw > cap && !_negative;

        if (overCap && _rule!.CapValue is int capValue)
        {
            HintText.Text = $"单次上限 {capValue}，已压到 {capValue}";
            HintText.Foreground = Warn;
            OverCapButton.Visibility = Visibility.Visible;
            OverCapButton.Content = $"要按 {_raw} 计入";
        }
        else
        {
            HintText.Text = _rule is null ? "" : DescribeScope();
            HintText.Foreground = TextSecondary;
            OverCapButton.Visibility = Visibility.Collapsed;
        }

        ScopeText.Text = _peopleCount <= 1 ? "" : $"将给 {_peopleCount} 人";
        ConfirmButton.Content = _peopleCount <= 1
            ? $"确定 {(effective >= 0 ? "+" : "")}{effective}"
            : $"确定 · {_peopleCount} 人 各 {(effective >= 0 ? "+" : "")}{effective}";
    }

    private string DescribeScope()
    {
        if (_rule is null) return "";
        if (!_rule.HasCap) return "不封顶";
        return _rule.CapScope == CapScope.Single
            ? $"单次上限 {_rule.CapValue}"
            : $"每周期上限 {_rule.CapValue}（本周期已得 {_alreadyInPeriod}）";
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        Confirmed?.Invoke(SignedValue, BuildNote());
    }

    private void OnOverCapClick(object sender, RoutedEventArgs e)
    {
        OverCapRequested?.Invoke(RawSignedValue, BuildNote());
    }

    /// <summary>密码校验通过后按原值入账。</summary>
    public void ConfirmForce() => Confirmed?.Invoke(RawSignedValue, BuildNote());

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        Hide();
        Cancelled?.Invoke();
    }

    private void OnScrimTapped(object sender, TappedRoutedEventArgs e)
    {
        // 点空白处关掉：代价只是重来一次，不值得做成模态
        Hide();
        Cancelled?.Invoke();
    }
}
