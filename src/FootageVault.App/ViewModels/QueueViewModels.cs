using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FootageVault.Core;
using FootageVault.Services;
using Microsoft.UI.Xaml.Controls;

namespace FootageVault.ViewModels;

public sealed class QueueViewModel : ObservableObject
{
    private readonly AppHost _host = AppHost.Current;
    public ObservableCollection<QueueItemViewModel> Items { get; } = new();

    private string _summary = "";
    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }

    private double _overall;
    public double Overall { get => _overall; private set => SetProperty(ref _overall, value); }

    private bool _bannerOpen;
    public bool BannerOpen { get => _bannerOpen; private set => SetProperty(ref _bannerOpen, value); }

    private string _bannerTitle = "";
    public string BannerTitle { get => _bannerTitle; private set => SetProperty(ref _bannerTitle, value); }

    private string _bannerText = "";
    public string BannerText { get => _bannerText; private set => SetProperty(ref _bannerText, value); }

    private InfoBarSeverity _bannerSeverity;
    public InfoBarSeverity BannerSeverity { get => _bannerSeverity; private set => SetProperty(ref _bannerSeverity, value); }

    private bool _canPause, _canResume, _canRetry, _canClear;
    public bool CanPause { get => _canPause; private set => SetProperty(ref _canPause, value); }
    public bool CanResume { get => _canResume; private set => SetProperty(ref _canResume, value); }
    public bool CanRetry { get => _canRetry; private set => SetProperty(ref _canRetry, value); }
    public bool CanClear { get => _canClear; private set => SetProperty(ref _canClear, value); }

    public bool IsEmpty => Items.Count == 0;
    public bool HasItems => Items.Count > 0;

    public void Refresh()
    {
        var engine = _host.Engine;
        var rows = _host.Store.QueueRows();
        var active = engine.ActiveIds().ToHashSet();

        // keep existing rows (so selection and scroll position survive), add new ones, drop removed ones
        var byId = Items.ToDictionary(i => i.Id);
        var ids = rows.Select(r => r.Id).ToList();
        if (!Items.Select(i => i.Id).SequenceEqual(ids))
        {
            for (var i = Items.Count - 1; i >= 0; i--)
                if (!ids.Contains(Items[i].Id)) Items.RemoveAt(i);
            for (var i = 0; i < rows.Count; i++)
            {
                if (i < Items.Count && Items[i].Id == rows[i].Id) continue;
                var vm = byId.TryGetValue(rows[i].Id, out var existing) ? existing : new QueueItemViewModel(rows[i].Id);
                if (Items.Contains(vm)) Items.Remove(vm);
                Items.Insert(i, vm);
            }
        }

        long totalBytes = 0, doneBytes = 0;
        double speed = 0;
        foreach (var (r, vm) in rows.Zip(Items))
        {
            var p = engine.ProgressOf(r.Id);
            var sent = r.State == ClipState.Uploaded ? r.Size : p?.Sent ?? r.BytesSent;
            var isActive = active.Contains(r.Id);
            if (r.State != ClipState.Failed)
            {
                totalBytes += r.Size;
                doneBytes += Math.Min(sent, r.Size);
            }
            if (isActive) speed += p?.BytesPerSecond ?? 0;
            vm.Update(r, isActive, sent, p?.BytesPerSecond ?? 0, StatusFor(r, isActive, engine));
        }

        var c = _host.Store.Counts();
        Summary = rows.Count == 0 ? "" :
            $"{c[ClipState.Uploaded]} of {rows.Count} uploaded" + (speed > 0 ? $"  ·  {Format.Speed(speed)}" : "");
        Overall = totalBytes == 0 ? 0 : 100.0 * doneBytes / totalBytes;

        var text = engine.BannerText();
        BannerOpen = text.Length > 0;
        BannerText = text;
        (BannerTitle, BannerSeverity) = engine.UserPaused ? ("Paused", InfoBarSeverity.Informational)
            : !engine.SignedIn ? ("Sign in needed", engine.Reason == PauseReason.SignIn ? InfoBarSeverity.Error : InfoBarSeverity.Warning)
            : engine.Reason switch
            {
                PauseReason.Quota => ("Daily limit reached", InfoBarSeverity.Warning),
                PauseReason.ChannelLimit => ("Channel limit reached", InfoBarSeverity.Error),
                _ => ("", InfoBarSeverity.Informational),
            };
        CanPause = !engine.UserPaused && rows.Count > 0;
        CanResume = engine.UserPaused || engine.Reason is PauseReason.Quota or PauseReason.ChannelLimit;
        CanRetry = c[ClipState.Failed] > 0;
        CanClear = c[ClipState.Uploaded] > 0;
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasItems));
    }

    private static (string Text, string Kind) StatusFor(ClipRecord r, bool active, QueueEngine e)
    {
        if (r.State == ClipState.Uploaded) return ($"Uploaded · {r.Privacy ?? "private"}", "done");
        if (r.State == ClipState.Failed) return ($"Failed: {r.Error ?? "see the log"}", "failed");
        if (active) return ("Uploading", "active");
        if (e.UserPaused) return ("Paused", "waiting");
        if (!e.SignedIn) return ("Waiting for sign-in", "attention");
        return e.Reason switch
        {
            PauseReason.Quota => ("Waiting for the daily limit to reset", "attention"),
            PauseReason.ChannelLimit => ("Waiting: channel upload limit", "attention"),
            _ => (r.State == ClipState.Uploading ? "Will continue where it stopped" : "Waiting to upload", "waiting"),
        };
    }
}

public sealed class QueueItemViewModel : ObservableObject
{
    public long Id { get; }
    public QueueItemViewModel(long id) => Id = id;

    private string _title = "", _status = "", _detail = "", _kind = "waiting", _videoId = "";
    private double _percent;
    private bool _active, _indeterminate;

    public string Title { get => _title; private set => SetProperty(ref _title, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string Detail { get => _detail; private set => SetProperty(ref _detail, value); }
    public double Percent { get => _percent; private set => SetProperty(ref _percent, value); }
    public bool IsActive { get => _active; private set { if (SetProperty(ref _active, value)) OnPropertyChanged(nameof(IsIdle)); } }
    public bool IsIdle => !_active;
    public bool Indeterminate { get => _indeterminate; private set => SetProperty(ref _indeterminate, value); }
    public string VideoId { get => _videoId; private set => SetProperty(ref _videoId, value); }

    /// <summary>done | failed | active | attention | waiting — picks the icon and colour.</summary>
    public string Kind
    {
        get => _kind;
        private set
        {
            if (SetProperty(ref _kind, value))
            {
                OnPropertyChanged(nameof(Glyph));
                OnPropertyChanged(nameof(IsDone));
                OnPropertyChanged(nameof(IsFailed));
                OnPropertyChanged(nameof(IsAttention));
            }
        }
    }

    public bool IsDone => _kind == "done";
    public bool IsFailed => _kind == "failed";
    public bool IsAttention => _kind == "attention";
    public string Glyph => _kind switch
    {
        "done" => "",       // Completed
        "failed" => "",     // ErrorBadge
        "attention" => "",  // Clock
        _ => "",
    };

    public bool ShowBar => _active || (_percent > 0 && _percent < 100 && _kind != "done");
    public string PercentText => $"{_percent:0}%";

    public void Update(ClipRecord r, bool active, long sent, double speed, (string Text, string Kind) status)
    {
        Title = r.Title;
        Status = status.Text;
        Kind = status.Kind;
        VideoId = r.VideoId ?? "";
        IsActive = active;
        Percent = r.State == ClipState.Uploaded ? 100 : r.Size == 0 ? 0 : 100.0 * sent / r.Size;
        Indeterminate = active && sent == 0;
        Detail = active
            ? string.Join("  ·  ", new[]
            {
                $"{Format.Size(sent)} of {Format.Size(r.Size)}",
                speed > 0 ? Format.Speed(speed) : "connecting…",
                speed > 0 ? Format.Eta((r.Size - sent) / speed) : "",
            }.Where(s => s.Length > 0))
            : Format.Size(r.Size);
        OnPropertyChanged(nameof(ShowBar));
        OnPropertyChanged(nameof(PercentText));
    }
}

public sealed class HistoryViewModel : ObservableObject
{
    private readonly AppHost _host = AppHost.Current;
    public ObservableCollection<HistoryItem> Items { get; } = new();
    public ObservableCollection<string> Stories { get; } = new();

    private string _search = "";
    public string Search { get => _search; set { if (SetProperty(ref _search, value)) Refresh(); } }

    private int _storyIndex;
    public int StoryIndex { get => _storyIndex; set { if (SetProperty(ref _storyIndex, value)) Refresh(); } }

    private string _count = "";
    public string Count { get => _count; private set => SetProperty(ref _count, value); }
    public bool IsEmpty => Items.Count == 0;

    public void ReloadStories()
    {
        var keep = StoryIndex > 0 && StoryIndex < Stories.Count ? Stories[StoryIndex] : null;
        Stories.Clear();
        Stories.Add("All stories");
        foreach (var s in _host.Store.StoryNames()) Stories.Add(s);
        _storyIndex = keep is null ? 0 : Math.Max(0, Stories.IndexOf(keep));
        OnPropertyChanged(nameof(StoryIndex));
        Refresh();
    }

    public void Refresh()
    {
        var story = StoryIndex > 0 && StoryIndex < Stories.Count ? Stories[StoryIndex] : null;
        Items.Clear();
        foreach (var r in _host.Store.History(Search, story)) Items.Add(new HistoryItem(r));
        Count = $"{Items.Count} result{(Items.Count == 1 ? "" : "s")}";
        OnPropertyChanged(nameof(IsEmpty));
    }
}

public sealed class HistoryItem
{
    public ClipRecord Record { get; }
    public HistoryItem(ClipRecord r) => Record = r;
    public string Title => Record.Title;
    public string When => Record.FinishedAt is { } t ? TimeZones.ToIst(t).ToString("dd.MM.yy  HH:mm") : "";
    public string Status => Record.State == ClipState.Uploaded ? "Uploaded" : "Failed";
    public bool Uploaded => Record.State == ClipState.Uploaded;
    public string Source => Record.SourcePath;
    public string Detail => Record.State == ClipState.Uploaded
        ? $"{Record.Privacy ?? "private"} · was {Record.OriginalName}"
        : Record.Error ?? "";
    public string? Link => Record.VideoId is { Length: > 0 } v ? $"https://www.youtube.com/watch?v={v}" : null;
}
