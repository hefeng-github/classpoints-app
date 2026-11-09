using Windows.UI;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using ClassPoints.App.Theme;

namespace ClassPoints.App.Controls;

/// <summary>
/// 6 位 PIN 九宫格。和大屏的分数键盘是同一套手感 —— 这台机器上没有实体键盘，也不会弹输入法。
/// 连错 5 次锁 1 分钟，锁定期内键盘直接拒收。
/// </summary>
public sealed partial class PinPad : UserControl
{
    private static readonly SolidColorBrush DotEmpty = Pal.Stroke;
    private static readonly SolidColorBrush DotFilled = Pal.Accent;
    private static readonly SolidColorBrush Danger = Pal.Danger;

    private readonly List<Border> _dots = new();
    private readonly System.Text.StringBuilder _value = new();
    private int _digits = 6;
    private Func<string, string?>? _verify;
    private bool _locked;

    public PinPad()
    {
        InitializeComponent();
    }

    public event Action<string>? Completed;

    public event Action? Cancelled;

    public bool IsOpen => Visibility == Visibility.Visible;

    /// <summary>打开并要求校验。<paramref name="verify"/> 返回 null 表示通过，否则返回一句人话错误。</summary>
    public void ShowVerify(string title, string sub, Func<string, string?> verify, int digits = 6)
    {
        _verify = verify;
        ShowInternal(title, sub, digits);
    }

    /// <summary>只收数字不校验（设置新密码、输入确认词用）。</summary>
    public void ShowInput(string title, string sub, int digits = 6)
    {
        _verify = null;
        ShowInternal(title, sub, digits);
    }

    public void SetLocked(bool locked, string? message = null)
    {
        _locked = locked;
        if (locked)
        {
            SubText.Text = message ?? "连错 5 次，请稍后再试";
            SubText.Foreground = Danger;
            _value.Clear();
            RefreshDots();
        }
    }

    public void ShowError(string message)
    {
        SubText.Text = message;
        SubText.Foreground = Danger;
        _value.Clear();
        RefreshDots();
    }

    public void Hide()
    {
        Visibility = Visibility.Collapsed;
        _value.Clear();
        SubText.Foreground = Pal.Secondary;
    }

    private void ShowInternal(string title, string sub, int digits)
    {
        _digits = System.Math.Clamp(digits, 4, 8);
        _value.Clear();
        TitleText.Text = title;
        SubText.Text = sub;
        SubText.Foreground = Pal.Secondary;
        BuildDots();
        Visibility = Visibility.Visible;
    }

    private void BuildDots()
    {
        DotsPanel.Children.Clear();
        _dots.Clear();
        for (var i = 0; i < _digits; i++)
        {
            var dot = new Border
            {
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(11),
                Background = DotEmpty,
            };
            DotsPanel.Children.Add(dot);
            _dots.Add(dot);
        }
    }

    private void RefreshDots()
    {
        for (var i = 0; i < _dots.Count; i++)
            _dots[i].Background = i < _value.Length ? DotFilled : DotEmpty;
    }

    private void OnDigitClick(object sender, RoutedEventArgs e)
    {
        if (_locked) return;
        if (sender is not Button b || b.Tag is not string digit) return;
        if (_value.Length >= _digits) return;

        _value.Append(digit);
        RefreshDots();

        if (_value.Length == _digits) Submit();
    }

    private void OnBackspaceClick(object sender, RoutedEventArgs e)
    {
        if (_locked) return;
        if (_value.Length == 0) return;
        _value.Length--;
        RefreshDots();
    }

    private void Submit()
    {
        var pin = _value.ToString();

        if (_verify is null)
        {
            Hide();
            Completed?.Invoke(pin);
            return;
        }

        var error = _verify(pin);
        if (error is null)
        {
            Hide();
            Completed?.Invoke(pin);
            return;
        }

        ShowError(error);
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    private void OnScrimTapped(object sender, TappedRoutedEventArgs e) => Close();

    private void Close()
    {
        Hide();
        Cancelled?.Invoke();
    }
}
