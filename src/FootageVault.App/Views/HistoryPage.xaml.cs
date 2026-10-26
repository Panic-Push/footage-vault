using System.Text;
using FootageVault.Core;
using FootageVault.Helpers;
using FootageVault.Services;
using FootageVault.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace FootageVault.Views;

public sealed partial class HistoryPage : Page, IActivatable
{
    public HistoryViewModel Vm { get; } = new();

    public HistoryPage()
    {
        InitializeComponent();
    }

    public void OnActivated() => Vm.ReloadStories();

    private HistoryItem? Current => List.SelectedItem as HistoryItem;

    private void Copy(string text, string message)
    {
        var data = new DataPackage();
        data.SetText(text);
        Clipboard.SetContent(data);
        CopiedTip.Title = "Copied";
        CopiedTip.Subtitle = message;
        CopiedTip.IsOpen = true;
    }

    private async void OnCopy(object sender, RoutedEventArgs e)
    {
        if (Current?.Link is { } link) Copy(link, "The link is on the clipboard.");
        else await Ui.Inform(XamlRoot, "Copy link", "Select an uploaded clip first.");
    }

    private void OnCopyAll(object sender, RoutedEventArgs e)
    {
        var lines = Vm.Items.Where(i => i.Link is not null).Select(i => $"{i.Title}\n{i.Link}").ToList();
        if (lines.Count == 0) return;
        Copy(string.Join("\n\n", lines), $"{lines.Count} link{(lines.Count == 1 ? "" : "s")} on the clipboard, ready to paste.");
    }

    private void OnOpen(object sender, RoutedEventArgs e)
    {
        if (Current?.Link is { } link) Shell.Open(link);
    }

    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => OnOpen(sender, e);

    private async void OnExport(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker { SuggestedFileName = $"footage-vault-history-{DateTime.Now:yyyy-MM-dd}" };
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.Hwnd);
        picker.FileTypeChoices.Add("CSV for Excel", new List<string> { ".csv" });
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        var sb = new StringBuilder();
        sb.Append(string.Join(",", CsvLog.Columns)).Append("\r\n");
        foreach (var i in Vm.Items)
            sb.Append(CsvLog.Row(i.Record, i.Status, i.Record.Error ?? "", i.Record.FinishedAt ?? i.Record.QueuedAt)).Append("\r\n");
        await File.WriteAllTextAsync(file.Path, sb.ToString(), new UTF8Encoding(true));
        CopiedTip.Title = "Exported";
        CopiedTip.Subtitle = file.Name;
        CopiedTip.IsOpen = true;
    }
}
