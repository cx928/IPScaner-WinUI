using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace IPScaner.WinUI.Views;

/// <summary>
/// Shared placeholder shown for a feature that has not been ported yet, and the
/// helper the interim page stubs use to render themselves.
/// </summary>
public sealed partial class PlaceholderPage : Page
{
    public PlaceholderPage()
    {
        Content = Build("功能建设中", "该功能正在移植中，敬请期待。");
    }

    /// <summary>Builds a consistent empty/placeholder surface.</summary>
    public static UIElement Build(string title, string subtitle)
    {
        var panel = new StackPanel
        {
            Padding = new Thickness(28, 24, 28, 24),
            Spacing = 8,
        };

        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 24,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });

        panel.Children.Add(new TextBlock
        {
            Text = subtitle,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75,
        });

        return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
}
