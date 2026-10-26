using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using FootageVault.Core;
using FootageVault.Services;

namespace FootageVault.ViewModels;

/// <summary>Preview: chosen folders as a tree (folder → sub-folders → clips), all editable before upload.</summary>
public sealed class PreviewViewModel : ObservableObject
{
    private readonly AppHost _host = AppHost.Current;
    public ObservableCollection<FolderViewModel> Folders { get; } = new();
    public List<SkippedFile> Skipped { get; private set; } = new();

    private string _summary = "";
    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }

    public bool HasFolders => Folders.Count > 0;
    public bool IsEmpty => Folders.Count == 0;
    public string SkippedText => Skipped.Count == 0 ? "No skipped files" : $"Skipped files ({Skipped.Count})";
    public bool HasSkipped => Skipped.Count > 0;

    public void Load()
    {
        Folders.Clear();
        var plan = _host.Plan;
        Skipped = plan?.Skipped.ToList() ?? new();
        if (plan is not null)
            foreach (var f in plan.Folders) Folders.Add(new FolderViewModel(this, f));
        Refresh();
    }

    /// <summary>Recalculate numbers and titles after any edit, and refresh what's shown.</summary>
    public void Refresh()
    {
        if (_host.Plan is { } plan) Planner.Renumber(plan, _host.Store);
        foreach (var f in Folders) f.Refresh();
        var clips = Folders.SelectMany(f => f.Folder.AllClips).ToList();
        var ticked = clips.Count(c => c.Selected);
        Summary = clips.Count == 0 ? "" : $"{Folders.Count} folder{S(Folders.Count)} · {ticked} of {clips.Count} clip{S(clips.Count)} will upload";
        OnPropertyChanged(nameof(HasFolders));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(SkippedText));
        OnPropertyChanged(nameof(HasSkipped));
    }

    public QueueResult Queue(IEnumerable<FolderViewModel> folders)
    {
        var plan = _host.Plan!;
        var list = folders.ToList();
        var result = Planner.Queue(plan, list.Select(f => f.Folder), _host.Store, _host.Settings, _host.Settings.RenameOnDisk);
        if (result.Success)
        {
            foreach (var f in list) Folders.Remove(f);
            if (plan.Folders.Count == 0) _host.Plan = null;
            _host.NotifyPlanChanged();
        }
        Refresh();
        return result;
    }

    public void RemoveFolder(FolderViewModel f)
    {
        _host.Plan?.Folders.Remove(f.Folder);
        Folders.Remove(f);
        if (_host.Plan is { Folders.Count: 0 }) _host.Plan = null;
        _host.NotifyPlanChanged();
        Refresh();
    }

    internal static string S(int n) => n == 1 ? "" : "s";
}

public sealed class FolderViewModel : ObservableObject
{
    private readonly PreviewViewModel _owner;
    public PlanFolder Folder { get; }
    public ObservableCollection<StoryViewModel> Stories { get; } = new();

    public FolderViewModel(PreviewViewModel owner, PlanFolder folder)
    {
        _owner = owner;
        Folder = folder;
        foreach (var s in folder.Stories) Stories.Add(new StoryViewModel(owner, s));
    }

    /// <summary>Folder name; changing it renames the folder on disk when uploaded (if that setting is on).</summary>
    public string Name
    {
        get => Folder.Name;
        set
        {
            var clean = Titles.Clean(value);
            if (clean.Length == 0 || clean == Folder.Name)
            {
                OnPropertyChanged();
                return;
            }
            Folder.Name = clean;
            OnPropertyChanged();
            _owner.Refresh();
        }
    }

    public string Path => Folder.Path;
    public bool Renamed => Folder.Name != Folder.OriginalName;
    public string RenameNote => Renamed ? $"Was \"{Folder.OriginalName}\"" : "";

    public string Counts
    {
        get
        {
            var all = Folder.AllClips.ToList();
            var subs = Folder.Stories.Count(s => s.Nested);
            return $"{all.Count(c => c.Selected)} of {all.Count} clip{PreviewViewModel.S(all.Count)}" +
                   (subs > 0 ? $" · {subs} sub-folder{PreviewViewModel.S(subs)}" : "");
        }
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Counts));
        OnPropertyChanged(nameof(Renamed));
        OnPropertyChanged(nameof(RenameNote));
        foreach (var s in Stories) s.Refresh();
    }
}

public sealed class StoryViewModel : ObservableObject
{
    private readonly PreviewViewModel _owner;
    private bool _syncing;
    public PlanStory Story { get; }
    public ObservableCollection<ClipViewModel> Clips { get; } = new();

    public StoryViewModel(PreviewViewModel owner, PlanStory story)
    {
        _owner = owner;
        Story = story;
        foreach (var c in story.Clips) Clips.Add(new ClipViewModel(owner, c));
        Clips.CollectionChanged += OnReordered;
    }

    public bool Nested => Story.Nested;
    public bool IsTopItself => !Story.Nested;
    public string Heading => Story.Nested ? "" : "Videos in this folder";

    /// <summary>Sub-folder name (nested stories only).</summary>
    public string FolderName
    {
        get => Story.FolderName;
        set
        {
            var clean = Titles.Clean(value);
            if (clean.Length == 0 || clean == Story.FolderName)
            {
                OnPropertyChanged();
                return;
            }
            Story.FolderName = clean;
            OnPropertyChanged();
            _owner.Refresh();
        }
    }

    public string RenameNote => Story.Nested && Story.SubSegments[^1] != Story.OriginalFolderName ? $"Was \"{Story.OriginalFolderName}\"" : "";

    /// <summary>The folder's date as dd.mm.yy. Typing a new one changes every clip in this folder.</summary>
    public string DateText
    {
        get => Titles.DateText(Story.Date);
        set
        {
            if (Titles.TryParseDate(value, out var d))
            {
                if (d != Story.Date || Story.MixedDates)
                {
                    Story.SetDate(d);
                    DateError = "";
                    _owner.Refresh();
                }
            }
            else DateError = "Type the date like 26.10.26";
            OnPropertyChanged();
        }
    }

    private string _dateError = "";
    public string DateError { get => _dateError; private set { if (SetProperty(ref _dateError, value)) OnPropertyChanged(nameof(HasDateError)); } }
    public bool HasDateError => _dateError.Length > 0;
    public string MixedNote => Story.MixedDates ? "Clips here have different dates; typing a date sets them all" : "";

    public string TitlePreview => Story.StoryName;

    public void Refresh()
    {
        OnPropertyChanged(nameof(FolderName));
        OnPropertyChanged(nameof(DateText));
        OnPropertyChanged(nameof(RenameNote));
        OnPropertyChanged(nameof(MixedNote));
        OnPropertyChanged(nameof(TitlePreview));
        foreach (var c in Clips) c.Refresh();
    }

    public void SortByTime()
    {
        Story.SortByRecordingTime();
        _syncing = true;
        Clips.Clear();
        foreach (var c in Story.Clips) Clips.Add(new ClipViewModel(_owner, c));
        _syncing = false;
        _owner.Refresh();
    }

    // ListView drag-to-reorder removes and re-inserts the item; mirror the new order into the plan.
    private void OnReordered(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_syncing || e.Action != NotifyCollectionChangedAction.Add) return;
        Story.Clips.Clear();
        Story.Clips.AddRange(Clips.Select(c => c.Clip));
        AppHost.Current.Dispatcher.TryEnqueue(() => _owner.Refresh());
    }
}

public sealed class ClipViewModel : ObservableObject
{
    private readonly PreviewViewModel _owner;
    public PlanClip Clip { get; }

    public ClipViewModel(PreviewViewModel owner, PlanClip clip)
    {
        _owner = owner;
        Clip = clip;
    }

    public bool Selected
    {
        get => Clip.Selected;
        set
        {
            if (Clip.Selected == value) return;
            Clip.Selected = value;
            OnPropertyChanged();
            _owner.Refresh();
        }
    }

    public string FileName => Clip.Clip.FileName;
    public string FullPath => Clip.Clip.Path;
    public string Title => Clip.Selected ? Clip.Title : "Not uploading";
    public string DateText => Titles.DateText(Clip.Date);
    public string Size => Format.Size(Clip.Clip.Size);
    public string Notes => Clip.Notes;
    public bool HasNotes => Clip.Notes.Length > 0;
    public bool IsDuplicate => Clip.DuplicateNote is not null;
    public bool IsWarning => HasNotes && !IsDuplicate;
    public double TitleOpacity => Clip.Selected ? 1.0 : 0.5;

    public void Refresh()
    {
        OnPropertyChanged(nameof(Selected));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(DateText));
        OnPropertyChanged(nameof(Notes));
        OnPropertyChanged(nameof(HasNotes));
        OnPropertyChanged(nameof(IsWarning));
        OnPropertyChanged(nameof(TitleOpacity));
    }
}

public static class Format
{
    public static string Size(double n)
    {
        string[] units = { "B", "KB", "MB", "GB" };
        var i = 0;
        while (n >= 1024 && i < units.Length - 1)
        {
            n /= 1024;
            i++;
        }
        return i == 0 ? $"{n:0} {units[i]}" : $"{n:0.0} {units[i]}";
    }

    public static string Speed(double bps) => bps > 0 ? $"{bps / (1024 * 1024):0.0} MB/s" : "";

    public static string Eta(double seconds)
    {
        var s = (int)seconds;
        if (s <= 0) return "";
        if (s < 90) return $"{s} s left";
        if (s < 5400) return $"{s / 60} min left";
        return $"{s / 3600.0:0.0} h left";
    }
}
