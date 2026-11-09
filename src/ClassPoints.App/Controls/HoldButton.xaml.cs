using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace ClassPoints.App.Controls;

/// <summary>
/// 长按触发的危险按钮（重置、删除班级数据）。
/// 用制度补偿触屏误触：进度环走满 3 秒才算数，中途松手立刻回零。
/// </summary>
public sealed partial class HoldButton : UserControl
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private double _elapsed;
    private bool _pressing;

    public HoldButton()
    {
        InitializeComponent();

        _timer.Tick += (_, _) =>
        {
            _elapsed += 0.05;
            Progress.Value = System.Math.Min(1, _elapsed / HoldSeconds);
            if (_elapsed < HoldSeconds) return;

            Reset();
            Confirmed?.Invoke();
        };

        Root.PointerPressed += OnPressed;
        Root.PointerReleased += OnReleased;
        Root.PointerCanceled += OnReleased;
        Root.PointerCaptureLost += OnReleased;
        Root.PointerExited += OnReleased;
    }

    public double HoldSeconds { get; set; } = 3;

    public string Label
    {
        get => LabelText.Text;
        set => LabelText.Text = value;
    }

    public event Action? Confirmed;

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        _pressing = true;
        _elapsed = 0;
        Progress.Value = 0;
        Root.CapturePointer(e.Pointer);
        _timer.Start();
    }

    private void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_pressing) return;
        Reset();
    }

    private void Reset()
    {
        _pressing = false;
        _timer.Stop();
        _elapsed = 0;
        Progress.Value = 0;
        try { Root.ReleasePointerCaptures(); } catch (Exception) { }
    }
}
