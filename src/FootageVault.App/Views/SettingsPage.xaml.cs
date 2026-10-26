using System.Reflection;
using FootageVault.Core;
using FootageVault.Helpers;
using FootageVault.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace FootageVault.Views;

/// <summary>Settings save as soon as they change, like Windows Settings.</summary>
public sealed partial class SettingsPage : Page, IActivatable, ITickable
{
    private readonly AppHost _host = AppHost.Current;
    private bool _loading = true;   // ignore change events until loaded

    public SettingsPage()
    {
        InitializeComponent();
        _host.PropertyChanged += (_, _) => _host.Ui(RefreshInfo);
        Load();
    }

    public void OnActivated() => Load();
    public void OnTick() => RefreshInfo();

    private void Load()
    {
        _loading = true;
        var s = _host.Settings;
        ParallelBox.Value = s.ParallelUploads;
        Limit.Value = s.DailyLimit;
        Rename.IsOn = s.RenameOnDisk;
        Description.Text = s.DescriptionTemplate;
        Audit.IsOn = s.AuditApproved;
        Privacy.SelectedIndex = s.AuditApproved && s.Privacy == "unlisted" ? 1 : 0;
        Privacy.IsEnabled = s.AuditApproved;
        Theme.SelectedIndex = s.Theme switch { "light" => 1, "dark" => 2, _ => 0 };
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        VersionInfo.Text = v is null ? "" : $"Version {v.Major}.{v.Minor}.{v.Build}";
        _loading = false;
        RefreshInfo();
    }

    private void RefreshInfo()
    {
        var used = _host.Engine.UploadsToday();
        var limit = _host.Settings.DailyLimit;
        LimitInfo.Text = $"Google allows 100 a day by default. Today: {used} made, {Math.Max(0, limit - used)} left. " +
                         "When the limit is reached, uploads pause and resume by themselves after the reset.";
        AccountInfo.Text = _host.SignedIn ? $"Signed in{(_host.ChannelName.Length > 0 ? " to " + _host.ChannelName : "")}" : "Not signed in";
        SignOutButton.IsEnabled = _host.SignedIn;
        ClientInfo.Text = _host.HasClientSecret ? "Installed. Choose a new file only if you created a new one in Google Cloud." : "Not added yet";
        LogInfo.Text = QueueEngine.LogFolder(_host.Settings);
    }

    private void Save()
    {
        if (_loading) return;
        if (!Audit.IsOn && Privacy.SelectedIndex != 0)
        {
            _loading = true;
            Privacy.SelectedIndex = 0;
            _loading = false;
        }
        Privacy.IsEnabled = Audit.IsOn;
        _host.SaveSettings(s =>
        {
            s.ParallelUploads = double.IsNaN(ParallelBox.Value) ? 2 : (int)Math.Clamp(ParallelBox.Value, 1, 4);
            s.DailyLimit = double.IsNaN(Limit.Value) ? 100 : (int)Math.Max(1, Limit.Value);
            s.RenameOnDisk = Rename.IsOn;
            s.DescriptionTemplate = string.IsNullOrWhiteSpace(Description.Text) ? new AppSettings().DescriptionTemplate : Description.Text;
            s.AuditApproved = Audit.IsOn;
            s.Privacy = Audit.IsOn ? ((Privacy.SelectedItem as ComboBoxItem)?.Tag as string ?? "private") : "private";
            s.Theme = (Theme.SelectedItem as ComboBoxItem)?.Tag as string ?? "system";
        });
        App.Window?.ApplyTheme(_host.Settings.Theme);
        RefreshInfo();
    }

    private void OnNumberChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) => Save();
    private void OnToggled(object sender, RoutedEventArgs e) => Save();
    private void OnSelection(object sender, SelectionChangedEventArgs e) => Save();
    private void OnTextCommitted(object sender, RoutedEventArgs e) => Save();

    private async void OnSignOut(object sender, RoutedEventArgs e)
    {
        if (await Ui.Confirm(XamlRoot, "Sign out?", "Uploads stop until you sign in again. The queue is kept.", "Sign out"))
            _host.SignOut();
        RefreshInfo();
    }

    private async void OnChooseClient(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.Hwnd);
        picker.FileTypeFilter.Add(".json");
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        var error = _host.ImportClientSecret(file.Path);
        if (error is not null) await Ui.Inform(XamlRoot, "That file didn't work", error);
        else await Ui.Inform(XamlRoot, "Client file added", "Now sign in on the Home page.");
        RefreshInfo();
    }

    private void OnOpenLog(object sender, RoutedEventArgs e)
    {
        var folder = QueueEngine.LogFolder(_host.Settings);
        Directory.CreateDirectory(folder);
        Shell.Open(folder);
    }

    private async void OnChangeLog(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.Hwnd);
        picker.FileTypeFilter.Add("*");
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;
        _host.SaveSettings(s => s.LogFolder = folder.Path);
        RefreshInfo();
    }

    private void OnOpenData(object sender, RoutedEventArgs e) => Shell.Open(_host.DataDir);
}
