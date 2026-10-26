using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FootageVault.Helpers;

/// <summary>Small functions used from x:Bind in XAML.</summary>
public static class Ui
{
    public static Visibility Show(bool v) => v ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility Hide(bool v) => v ? Visibility.Collapsed : Visibility.Visible;
    public static Visibility ShowText(string? s) => string.IsNullOrEmpty(s) ? Visibility.Collapsed : Visibility.Visible;
    public static bool Not(bool v) => !v;

    public static async Task<bool> Confirm(XamlRoot root, string title, string body, string primary, string close = "Cancel")
    {
        var d = new ContentDialog
        {
            XamlRoot = root, Title = title, Content = new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = primary, CloseButtonText = close, DefaultButton = ContentDialogButton.Primary,
        };
        return await d.ShowAsync() == ContentDialogResult.Primary;
    }

    public static async Task Inform(XamlRoot root, string title, string body)
    {
        var d = new ContentDialog
        {
            XamlRoot = root, Title = title, CloseButtonText = "OK", DefaultButton = ContentDialogButton.Close,
            Content = new ScrollViewer
            {
                MaxHeight = 420,
                Content = new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
            },
        };
        await d.ShowAsync();
    }
}
