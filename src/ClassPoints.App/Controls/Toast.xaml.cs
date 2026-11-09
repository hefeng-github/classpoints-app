using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClassPoints.App.Controls;

public sealed partial class Toast : UserControl
{
    private DispatcherTimer? _timer;

    public Toast()
    {
        InitializeComponent();
    }

    public void Show(string message, double seconds = 3)
    {
        MessageText.Text = message;
        Visibility = Visibility.Visible;
        Opacity = 1;

        _timer?.Stop();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        _timer.Tick += (_, _) =>
        {
            _timer?.Stop();
            Visibility = Visibility.Collapsed;
        };
        _timer.Start();
    }

    public void Dismiss()
    {
        _timer?.Stop();
        Visibility = Visibility.Collapsed;
    }
}
