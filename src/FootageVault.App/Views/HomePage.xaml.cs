using System.ComponentModel;
using FootageVault.Core;
using FootageVault.Helpers;
using FootageVault.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace FootageVault.Views;

public sealed partial class HomePage : Page, IActivatable, ITickable
{
    private readonly AppHost _host = AppHost.Current;
    private bool _scanning;

    public HomePage()
    {
        InitializeComponent();
        _host.PropertyChanged += OnHostChanged;
        Refresh();
    }

    public void OnActivated() => Refresh();
    public void OnTick() => RefreshCounts();

    private void OnHostChanged(object? sender, PropertyChangedEventArgs e) => _host.Ui(Refresh);

    private static string Greeting()
    {
        var h = TimeZones.ToIst(DateTimeOffset.UtcNow).Hour;
        return h < 12 ? "Good morning" : h < 17 ? "Good afternoon" : "Good evening";
    }

    private void Refresh()
    {
        Greeting.Text = Greeting();
        SetupBar.IsOpen = !_host.HasClientSecret;
        AuthRing.IsActive = _host.AuthBusy;
        AuthRing.Visibility = Ui.Show(_host.AuthBusy);
        CancelSignInButton.Visibility = Ui.Show(_host.AuthBusy);
        if (_host.AuthBusy)
        {
            AccountTitle.Text = "Signing in…";
            AccountSub.Text = "Finish signing in in your web browser, then come back here.";
        }
        else if (_host.SignedIn)
        {
            AccountTitle.Text = _host.ChannelName.Length > 0 ? _host.ChannelName : "Signed in";
            AccountSub.Text = "Connected YouTube channel. Uploads start as soon as clips are queued.";
        }
        else
        {
            AccountTitle.Text = "Not signed in";
            AccountSub.Text = _host.AuthError.Length > 0 ? _host.AuthError : "Sign in with the Google account that owns the YouTube channel.";
        }
        Avatar.DisplayName = _host.SignedIn ? _host.ChannelName : "";
        SignInButton.Visibility = Ui.Show(!_host.SignedIn && !_host.AuthBusy);
        SignInButton.IsEnabled = _host.HasClientSecret;
        FootNote.Text = (_host.Settings.RenameOnDisk
                            ? "When you upload, folders and video files are renamed on disk to match their titles. "
                            : "Your files on disk are never renamed. ") +
                        (_host.Settings.EffectivePrivacy == "private"
                            ? "Videos upload as Private until Google approves the API audit."
                            : "Videos upload as Unlisted.");
        RefreshCounts();
    }

    private void RefreshCounts()
    {
        var used = _host.Engine.UploadsToday();
        var limit = Math.Max(1, _host.Settings.DailyLimit);
        QuotaRing.Maximum = limit;
        QuotaRing.Value = Math.Min(used, limit);
        QuotaUsed.Text = used.ToString();
        QuotaOf.Text = $"of {limit}";
        QuotaLeft.Text = $"{Math.Max(0, limit - used)} left today";
        QuotaReset.Text = $"Resets {TimeZones.FormatIst(Quota.NextReset(DateTimeOffset.UtcNow))}";
        var c = _host.Store.Counts();
        StatWaiting.Text = c[ClipState.Queued].ToString();
        StatUploading.Text = c[ClipState.Uploading].ToString();
        StatDone.Text = c[ClipState.Uploaded].ToString();
        StatFailed.Text = c[ClipState.Failed].ToString();
    }

    // ------------------------------------------------------------------ sign-in
    private async void OnSignIn(object sender, RoutedEventArgs e) => await _host.SignInAsync();
    private void OnCancelSignIn(object sender, RoutedEventArgs e) => _host.CancelSignIn();

    private async void OnChooseClientFile(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.Hwnd);
        picker.FileTypeFilter.Add(".json");
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        var error = _host.ImportClientSecret(file.Path);
        if (error is not null) await Ui.Inform(XamlRoot, "That file didn't work", error);
        Refresh();
    }

    private void OnOpenQueue(object sender, RoutedEventArgs e) => App.Window?.GoTo("queue");

    // ------------------------------------------------------------------ folders
    private void OnAddFolders(object sender, RoutedEventArgs e) => PickFolders();

    private void OnDropZoneTapped(object sender, TappedRoutedEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement fe && IsInside(fe, AddButton)) return;
        PickFolders();
    }

    private static bool IsInside(DependencyObject? child, DependencyObject parent)
    {
        while (child is not null)
        {
            if (child == parent) return true;
            child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(child);
        }
        return false;
    }

    private void PickFolders()
    {
        if (_scanning) return;
        List<string> folders;
        try { folders = MultiFolderPicker.Pick(App.Hwnd, "Choose one or more story folders"); }
        catch (Exception ex)
        {
            Log.Error("Folder picker failed", ex);
            _ = Ui.Inform(XamlRoot, "Couldn't open the folder picker", ex.Message);
            return;
        }
        if (folders.Count > 0) _ = ScanAsync(folders);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (_scanning || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        e.AcceptedOperation = DataPackageOperation.Link;
        e.DragUIOverride.Caption = "Add folders";
        DropBorder.Stroke = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
    }

    private void OnDragLeave(object sender, DragEventArgs e) =>
        DropBorder.Stroke = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ControlStrongStrokeColorDefaultBrush"];

    private async void OnDrop(object sender, DragEventArgs e)
    {
        OnDragLeave(sender, e);
        if (_scanning || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();
        var folders = items.OfType<StorageFolder>().Select(f => f.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
        if (folders.Count == 0)
        {
            await Ui.Inform(XamlRoot, "Drop folders, not files", "Drag the story folders themselves onto Footage Vault.");
            return;
        }
        await ScanAsync(folders);
    }

    private async Task ScanAsync(List<string> folders)
    {
        _scanning = true;
        SetBusy(true, folders.Count == 1 ? $"Reading {Path.GetFileName(folders[0])}…" : $"Reading {folders.Count} folders…");
        try
        {
            var progress = new Progress<string>(s => SetBusy(true, s));
            var scanner = new Scanner(new ShellMediaInfoReader());
            var result = await Task.Run(() => scanner.ScanAsync(folders, progress));
            if (result.Stories.Count == 0)
            {
                var why = result.Skipped.Count == 0 ? "" :
                    "\n\n" + string.Join("\n", result.Skipped.Take(10).Select(s => $"• {Path.GetFileName(s.Path)}: {s.Reason}"));
                await Ui.Inform(XamlRoot, "No videos found", "No video files were found in the folders you chose." + why);
                return;
            }
            var fresh = Planner.Build(result, _host.Store);
            if (_host.Plan is { } existing)
            {
                foreach (var f in fresh.Folders.Where(f => !existing.Folders.Any(x => string.Equals(x.Path, f.Path, StringComparison.OrdinalIgnoreCase))))
                    existing.Folders.Add(f);
                existing.Skipped.AddRange(fresh.Skipped);
                Planner.Renumber(existing, _host.Store);
            }
            else _host.Plan = fresh;
            _host.NotifyPlanChanged();
            App.Window?.GoTo("preview");
        }
        catch (Exception ex)
        {
            Log.Error("Scan failed", ex);
            await Ui.Inform(XamlRoot, "Couldn't read the folders", ex.Message);
        }
        finally
        {
            _scanning = false;
            SetBusy(false, "");
        }
    }

    private void SetBusy(bool busy, string text)
    {
        ScanRing.IsActive = busy;
        ScanRing.Visibility = Ui.Show(busy);
        DropIcon.Visibility = Ui.Hide(busy);
        AddButton.IsEnabled = !busy;
        DropTitle.Text = busy ? "Scanning" : "Drop story folders here";
        DropSub.Text = busy ? text : "Add as many as you like. Sub-folders become their own stories, e.g. Collector Visit - Phone 1.";
    }
}
