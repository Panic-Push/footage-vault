using System.Runtime.InteropServices;
using FootageVault.Core;
using FootageVault.Helpers;
using FootageVault.Services;
using FootageVault.Views;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Graphics;

namespace FootageVault;

public sealed partial class MainWindow : Window
{
    private readonly Dictionary<string, Page> _pages = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _closingConfirmed;

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        try { AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "FootageVault.ico")); } catch (Exception) { }
        SizeToScreen();
        ApplyTheme(AppHost.Current.Settings.Theme);

        // Fluent entrance: each page slides up gently when shown
        ContentFrame.ContentTransitions = new TransitionCollection { new EntranceThemeTransition { FromVerticalOffset = 28 } };
        AppWindow.Closing += OnClosing;
        _timer.Tick += (_, _) => UpdateBadges();
        _timer.Start();
        AppHost.Current.PlanChanged += UpdateBadges;
        Nav.SelectedItem = Nav.MenuItems[0];
    }

    public void ApplyTheme(string theme)
    {
        Root.RequestedTheme = theme switch { "light" => ElementTheme.Light, "dark" => ElementTheme.Dark, _ => ElementTheme.Default };
    }

    public void GoTo(string tag)
    {
        var item = Nav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (string)i.Tag == tag);
        if (item is not null) Nav.SelectedItem = item;
        else if (tag == "settings") Nav.SelectedItem = Nav.SettingsItem;
    }

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var tag = args.IsSettingsSelected ? "settings" : (args.SelectedItem as NavigationViewItem)?.Tag as string ?? "home";
        if (!_pages.TryGetValue(tag, out var page))
        {
            page = tag switch
            {
                "preview" => new PreviewPage(),
                "queue" => new QueuePage(),
                "history" => new HistoryPage(),
                "settings" => new SettingsPage(),
                _ => new HomePage(),
            };
            _pages[tag] = page;
        }
        ContentFrame.Content = page;
        if (page is IActivatable a) a.OnActivated();
    }

    private void UpdateBadges()
    {
        var host = AppHost.Current;
        var c = host.Store.Counts();
        var pending = c[ClipState.Queued] + c[ClipState.Uploading];
        QueueBadge.Value = pending;
        QueueBadge.Visibility = pending > 0 ? Visibility.Visible : Visibility.Collapsed;
        var folders = host.Plan?.Folders.Count ?? 0;
        PreviewBadge.Value = folders;
        PreviewBadge.Visibility = folders > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (ContentFrame.Content is ITickable t) t.OnTick();
    }

    private void SizeToScreen()
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        var w = Math.Min((int)(1240 * scale), (int)(area.Width * 0.92));
        var h = Math.Min((int)(820 * scale), (int)(area.Height * 0.92));
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h));
        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.PreferredMinimumWidth = (int)(900 * scale);
            p.PreferredMinimumHeight = (int)(600 * scale);
        }
    }

    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closingConfirmed) return;
        var busy = AppHost.Current.Engine.ActiveIds().Count;
        if (busy > 0)
        {
            args.Cancel = true;
            var ok = await Ui.Confirm(Content.XamlRoot, "Uploads are running",
                $"{busy} upload{(busy == 1 ? " is" : "s are")} in progress. If you close now, they continue from where they " +
                "stopped the next time you open Footage Vault.", "Close anyway", "Keep open");
            if (!ok) return;
            _closingConfirmed = true;
            AppHost.Current.Shutdown();
            Close();
            return;
        }
        AppHost.Current.Shutdown();
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}

/// <summary>Pages that refresh when shown.</summary>
public interface IActivatable
{
    void OnActivated();
}

/// <summary>Pages that refresh every second while visible.</summary>
public interface ITickable
{
    void OnTick();
}
