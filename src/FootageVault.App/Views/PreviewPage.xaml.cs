using FootageVault.Core;
using FootageVault.Helpers;
using FootageVault.Services;
using FootageVault.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace FootageVault.Views;

public sealed partial class PreviewPage : Page, IActivatable
{
    private readonly AppHost _host = AppHost.Current;
    public PreviewViewModel Vm { get; } = new();
    private Plan? _shown;

    public PreviewPage()
    {
        InitializeComponent();
        _host.PlanChanged += () => _host.Ui(Reload);
        Reload();
    }

    public void OnActivated() => Reload();

    private void Reload()
    {
        // Rebuild only when the plan changed (new folders added); keep edits otherwise.
        var plan = _host.Plan;
        var count = plan?.Folders.Count ?? 0;
        if (!ReferenceEquals(plan, _shown) || count != Vm.Folders.Count)
        {
            _shown = plan;
            Vm.Load();
        }
        else Vm.Refresh();
    }

    // Enter in a name/date box applies it at once (otherwise it applies when you leave the box).
    private void OnCommitKey(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter || sender is not TextBox box) return;
        switch (box.DataContext)
        {
            case FolderViewModel f:
                f.Name = box.Text;
                break;
            case StoryViewModel s when box.PlaceholderText == "dd.mm.yy":
                s.DateText = box.Text;
                break;
            case StoryViewModel s:
                s.FolderName = box.Text;
                break;
        }
        e.Handled = true;
    }

    private void OnSortStory(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is StoryViewModel s) s.SortByTime();
    }

    private void OnRemoveFolder(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is FolderViewModel f)
        {
            Vm.RemoveFolder(f);
            _shown = _host.Plan;
        }
    }

    private async void OnUploadFolder(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is FolderViewModel f) await Upload(new[] { f });
    }

    private async void OnUploadAll(object sender, RoutedEventArgs e) => await Upload(Vm.Folders.ToList());

    private async Task Upload(IReadOnlyList<FolderViewModel> folders)
    {
        // make sure an edit still in a text box is applied first
        FocusManager.TryMoveFocus(FocusNavigationDirection.Next, new FindNextElementOptions { SearchRoot = XamlRoot.Content });
        Vm.Refresh();
        var clips = folders.SelectMany(f => f.Folder.AllClips).Count(c => c.Selected);
        if (clips == 0)
        {
            await Ui.Inform(XamlRoot, "Nothing selected", "Tick at least one clip to upload.");
            return;
        }
        var rename = _host.Settings.RenameOnDisk;
        var body = $"{clips} clip{(clips == 1 ? "" : "s")} will be added to the upload queue as {_host.Settings.EffectivePrivacy}." +
                   (rename ? "\n\nFolders you renamed, and every ticked video file, will be renamed on your disk to match their titles. " +
                             "Close any File Explorer windows showing these folders first." : "");
        if (!await Ui.Confirm(XamlRoot, rename ? "Rename and upload?" : "Upload?", body, "Upload")) return;

        var result = Vm.Queue(folders);
        _shown = _host.Plan;
        if (!result.Success)
        {
            Message.Severity = InfoBarSeverity.Error;
            Message.Title = "Couldn't rename";
            Message.Message = result.Error ?? "";
            Message.IsOpen = true;
            return;
        }
        Message.IsOpen = false;
        App.Window?.GoTo("queue");
    }

    private async void OnShowSkipped(object sender, RoutedEventArgs e)
    {
        var text = string.Join("\n", Vm.Skipped.Select(s => $"{s.Path}\n    {s.Reason}"));
        await Ui.Inform(XamlRoot, "Skipped files", text.Length == 0 ? "No files were skipped." : text);
    }

    private void OnAddMore(object sender, RoutedEventArgs e) => App.Window?.GoTo("home");
}
