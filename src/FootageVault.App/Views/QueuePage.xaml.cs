using FootageVault.Helpers;
using FootageVault.Services;
using FootageVault.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FootageVault.Views;

public sealed partial class QueuePage : Page, IActivatable, ITickable
{
    private readonly AppHost _host = AppHost.Current;
    public QueueViewModel Vm { get; } = new();

    public QueuePage()
    {
        InitializeComponent();
        _host.Engine.Changed += () => _host.Ui(Vm.Refresh);
        Vm.Refresh();
    }

    public void OnActivated() => Vm.Refresh();
    public void OnTick() => Vm.Refresh();

    private List<QueueItemViewModel> Selected() => List.SelectedItems.OfType<QueueItemViewModel>().ToList();

    private void OnPause(object sender, RoutedEventArgs e) => _host.Engine.PauseAll();
    private void OnResume(object sender, RoutedEventArgs e) => _host.Engine.Resume();
    private void OnGoHome(object sender, RoutedEventArgs e) => App.Window?.GoTo("home");

    private void OnRetry(object sender, RoutedEventArgs e)
    {
        _host.Store.RetryFailed();
        Vm.Refresh();
    }

    private async void OnRemove(object sender, RoutedEventArgs e)
    {
        var items = Selected();
        if (items.Count == 0)
        {
            await Ui.Inform(XamlRoot, "Remove", "Select one or more clips first (Ctrl+click to pick several).");
            return;
        }
        if (!await Ui.Confirm(XamlRoot, "Remove from the queue?",
                $"{items.Count} clip{(items.Count == 1 ? "" : "s")} will be taken out of the queue. Files on your disk and videos " +
                "already on YouTube are not touched.", "Remove")) return;
        var removed = _host.Engine.Remove(items.Select(i => i.Id));
        if (removed < items.Count)
            await Ui.Inform(XamlRoot, "Some clips were kept",
                $"{items.Count - removed} clip(s) are uploaded or uploading right now. Select Pause all first to remove a clip that is uploading.");
        Vm.Refresh();
    }

    private void OnOpen(object sender, RoutedEventArgs e)
    {
        foreach (var i in Selected().Where(i => i.VideoId.Length > 0).Take(5))
            Shell.Open($"https://www.youtube.com/watch?v={i.VideoId}");
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        _host.Store.HideFinished();
        Vm.Refresh();
    }
}
