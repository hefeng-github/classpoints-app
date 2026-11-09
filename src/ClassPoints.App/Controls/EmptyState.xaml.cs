using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClassPoints.App.Controls;

public sealed partial class EmptyState : UserControl
{
    public EmptyState()
    {
        InitializeComponent();
    }

    public event Action? PrimaryInvoked;

    public event Action? SecondaryInvoked;

    public void Configure(string glyph, string message, string? primary = null, string? secondary = null)
    {
        GlyphIcon.Glyph = glyph;
        MessageText.Text = message;

        PrimaryButton.Content = primary ?? "";
        PrimaryButton.Visibility = string.IsNullOrEmpty(primary) ? Visibility.Collapsed : Visibility.Visible;

        SecondaryButton.Content = secondary ?? "";
        SecondaryButton.Visibility = string.IsNullOrEmpty(secondary) ? Visibility.Collapsed : Visibility.Visible;
    }

    public void Show(bool visible) => Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

    private void OnPrimaryClick(object sender, RoutedEventArgs e) => PrimaryInvoked?.Invoke();

    private void OnSecondaryClick(object sender, RoutedEventArgs e) => SecondaryInvoked?.Invoke();
}
