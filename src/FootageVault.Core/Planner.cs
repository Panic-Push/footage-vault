namespace FootageVault.Core;

/// <summary>One video in the preview.</summary>
public sealed class PlanClip
{
    public required ScannedClip Clip { get; init; }
    public DateOnly Date { get; set; }
    public bool Selected { get; set; } = true;
    public string? DuplicateNote { get; set; }
    public int Number { get; set; }
    public string Title { get; set; } = "";
    public bool DateEdited { get; set; }

    public string Notes
    {
        get
        {
            if (DuplicateNote is not null) return DuplicateNote;
            if (!Clip.DateFromMetadata && !DateEdited) return "No date in file; used the file's modified date";
            return "";
        }
    }
}

/// <summary>A folder that directly holds videos. Nested ones are named "Top - Sub".</summary>
public sealed class PlanStory
{
    public required PlanFolder Top { get; init; }
    public required string FolderPath { get; init; }
    /// <summary>Folder names below the top folder, e.g. ["Phone 1"]; empty when the videos sit in the top folder.</summary>
    public required List<string> SubSegments { get; init; }
    public List<PlanClip> Clips { get; } = new();

    public bool Nested => SubSegments.Count > 0;
    public bool IsTopItself => !Nested;

    /// <summary>The folder name shown and editable for this story (its own folder).</summary>
    public string FolderName
    {
        get => Nested ? SubSegments[^1] : Top.Name;
        set
        {
            var clean = Titles.Clean(value);
            if (clean.Length == 0) return;
            if (Nested) SubSegments[^1] = clean;
            else Top.Name = clean;
        }
    }

    public string OriginalFolderName => Path.GetFileName(FolderPath);
    public string StoryName => Nested ? string.Join(" - ", new[] { Top.Name }.Concat(SubSegments)) : Top.Name;

    /// <summary>The date most clips share; shown greyed as dd.mm.yy and editable for the whole folder.</summary>
    public DateOnly Date => Clips.Count == 0 ? DateOnly.FromDateTime(DateTime.Today)
        : Clips.GroupBy(c => c.Date).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).First().Key;

    public bool MixedDates => Clips.Select(c => c.Date).Distinct().Count() > 1;

    public void SetDate(DateOnly d)
    {
        foreach (var c in Clips)
        {
            c.Date = d;
            c.DateEdited = true;
        }
    }

    public void Move(int from, int to)
    {
        if (from < 0 || from >= Clips.Count) return;
        to = Math.Clamp(to, 0, Clips.Count - 1);
        var c = Clips[from];
        Clips.RemoveAt(from);
        Clips.Insert(to, c);
    }

    public void SortByRecordingTime()
    {
        var sorted = Clips.OrderBy(c => c.Clip.RecordedIst).ThenBy(c => c.Clip.FileName, NaturalComparer.Instance).ToList();
        Clips.Clear();
        Clips.AddRange(sorted);
    }
}

/// <summary>A top-level story folder (one per chosen folder, or one per sub-folder of a chosen parent folder).</summary>
public sealed class PlanFolder
{
    public required string Path { get; init; }
    public string Name { get; set; } = "";
    public string OriginalName => System.IO.Path.GetFileName(Path);
    public List<PlanStory> Stories { get; } = new();
    public IEnumerable<PlanClip> AllClips => Stories.SelectMany(s => s.Clips);
}

public sealed class Plan
{
    public List<PlanFolder> Folders { get; } = new();
    public List<SkippedFile> Skipped { get; } = new();
    public IEnumerable<PlanStory> Stories => Folders.SelectMany(f => f.Stories);
}

public static class Planner
{
    public static Plan Build(ScanResult scan, Store store)
    {
        var plan = new Plan();
        plan.Skipped.AddRange(scan.Skipped);
        var seen = new Dictionary<string, string>();
        foreach (var s in scan.Stories)
        {
            var folder = plan.Folders.FirstOrDefault(f => string.Equals(f.Path, s.TopFolderPath, StringComparison.OrdinalIgnoreCase));
            if (folder is null)
            {
                folder = new PlanFolder { Path = s.TopFolderPath, Name = s.Segments[0] };
                plan.Folders.Add(folder);
            }
            var story = new PlanStory { Top = folder, FolderPath = s.FolderPath, SubSegments = s.Segments.Skip(1).ToList() };
            foreach (var c in s.Clips)
            {
                var pc = new PlanClip { Clip = c, Date = DateOnly.FromDateTime(c.RecordedIst.DateTime) };
                if (store.FindUploaded(c.Fingerprint) is { } done)
                {
                    pc.DuplicateNote = $"Already uploaded on {(done.FinishedAt ?? done.QueuedAt).ToString("dd.MM.yy")} as {done.Title}";
                    pc.Selected = false;
                }
                else if (store.FindPending(c.Fingerprint) is { } pending)
                {
                    pc.DuplicateNote = $"Already in the queue as {pending.Title}";
                    pc.Selected = false;
                }
                else if (seen.TryGetValue(c.Fingerprint, out var other))
                {
                    pc.DuplicateNote = $"Same video as {other} in this scan";
                    pc.Selected = false;
                }
                else seen[c.Fingerprint] = c.FileName;
                story.Clips.Add(pc);
            }
            folder.Stories.Add(story);
        }
        Renumber(plan, store);
        return plan;
    }

    /// <summary>Number ticked clips in their current order. Each story + date continues after the upload log.</summary>
    public static void Renumber(Plan plan, Store store)
    {
        // Two stories can end up with the same name after a rename; they share one sequence.
        var counters = new Dictionary<(string, DateOnly), int>();
        foreach (var story in plan.Stories)
        {
            var name = story.StoryName;
            foreach (var c in story.Clips)
            {
                if (!c.Selected)
                {
                    c.Number = 0;
                    c.Title = "";
                    continue;
                }
                var key = (name.ToUpperInvariant(), c.Date);
                if (!counters.TryGetValue(key, out var n)) n = store.NextSequence(name, c.Date);
                c.Number = n;
                counters[key] = n + 1;
                c.Title = Titles.Build(name, story.Nested, n, c.Date);
            }
        }
    }

    /// <summary>
    /// Queue every ticked clip of the given folders. When <paramref name="renameOnDisk"/> is on, folders whose
    /// name was changed are renamed and each ticked video is renamed to its title, all or nothing.
    /// </summary>
    public static QueueResult Queue(Plan plan, IEnumerable<PlanFolder> folders, Store store, AppSettings settings, bool renameOnDisk)
        => Queue(plan, folders, store, settings, renameOnDisk, new DiskRenamer());

    internal static QueueResult Queue(Plan plan, IEnumerable<PlanFolder> folders, Store store, AppSettings settings,
        bool renameOnDisk, DiskRenamer renamer)
    {
        var targets = folders.ToList();
        Renumber(plan, store);
        var clips = targets.SelectMany(f => f.Stories).SelectMany(s => s.Clips.Where(c => c.Selected).Select(c => (s, c))).ToList();
        if (clips.Count == 0) return QueueResult.Fail("Tick at least one clip to upload.");

        if (renameOnDisk)
        {
            foreach (var f in targets)
            {
                if (!string.Equals(f.Name, f.OriginalName, StringComparison.Ordinal)) renamer.RenameFolder(f.Path, f.Name);
                foreach (var s in f.Stories.Where(s => s.Nested && s.SubSegments[^1] != s.OriginalFolderName))
                    renamer.RenameFolder(s.FolderPath, s.SubSegments[^1]);
            }
            foreach (var (_, c) in clips)
            {
                var newName = Titles.SafeFileName(c.Title) + c.Clip.Extension;
                if (!string.Equals(newName, c.Clip.FileName, StringComparison.Ordinal)) renamer.RenameFile(c.Clip.Path, newName);
            }
            var error = renamer.Execute();
            if (error is not null) return QueueResult.Fail(error);
        }

        var records = new List<ClipRecord>();
        foreach (var (s, c) in clips)
        {
            var finalPath = renameOnDisk ? renamer.FinalPath(c.Clip.Path) : c.Clip.Path;
            var fi = new FileInfo(finalPath);
            records.Add(new ClipRecord
            {
                Story = s.StoryName,
                Seq = c.Number,
                Title = c.Title,
                Description = Titles.Description(settings.DescriptionTemplate, s.StoryName, c.Date),
                SourcePath = finalPath,
                OriginalName = c.Clip.FileName,
                Size = fi.Exists ? fi.Length : c.Clip.Size,
                ModifiedTicks = fi.Exists ? fi.LastWriteTimeUtc.Ticks : c.Clip.ModifiedUtc.Ticks,
                Fingerprint = c.Clip.Fingerprint,
                RecDate = c.Date.ToString("yyyy-MM-dd"),
            });
        }
        store.AddRange(records);
        foreach (var f in targets) plan.Folders.Remove(f);
        return QueueResult.Ok(records.Count, renameOnDisk ? renamer.Count : 0);
    }
}

public sealed record QueueResult(bool Success, int Added, int Renamed, string? Error)
{
    public static QueueResult Ok(int added, int renamed) => new(true, added, renamed, null);
    public static QueueResult Fail(string error) => new(false, 0, 0, error);
}

/// <summary>
/// Renames files and folders as one all-or-nothing step: everything is checked first, and if any rename fails
/// (file open, folder in use, name taken) the ones already done are put back.
/// </summary>
public sealed class DiskRenamer
{
    private readonly Dictionary<string, string> _folders = new(StringComparer.OrdinalIgnoreCase);   // old full path -> new name
    private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string From, string To, bool Dir)> _done = new();

    /// <summary>Test hook: throw an IOException on the n-th move (1-based) to exercise the undo path.</summary>
    internal int FailOnMove { get; set; }
    private int _moves;

    public int Count => _done.Count;

    public void RenameFolder(string path, string newName) =>
        _folders[Normalize(path)] = Titles.SafeFileName(newName);

    public void RenameFile(string path, string newName) => _files[Normalize(path)] = newName;

    private static string Normalize(string p) => Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>Where a path ends up after all folder and file renames.</summary>
    public string FinalPath(string path)
    {
        var full = Normalize(path);
        if (_files.TryGetValue(full, out var fname)) full = Path.Combine(Path.GetDirectoryName(full)!, fname);
        return MapFolders(full);
    }

    private string MapFolders(string full)
    {
        var root = Path.GetPathRoot(full) ?? "";
        var parts = full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var oldPrefix = root;
        var newPrefix = root;
        foreach (var part in parts)
        {
            oldPrefix = Path.Combine(oldPrefix, part);
            var name = _folders.TryGetValue(oldPrefix, out var n) ? n : part;
            newPrefix = Path.Combine(newPrefix, name);
        }
        return newPrefix;
    }

    /// <returns>null on success, otherwise a message for the operator (and nothing is left changed).</returns>
    public string? Execute()
    {
        // 1. Check everything before touching anything.
        var plannedFileTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (from, name) in _files)
        {
            if (!File.Exists(from)) return $"\"{Path.GetFileName(from)}\" is no longer there. Scan the folder again.";
            var to = Path.Combine(Path.GetDirectoryName(from)!, name);
            if (!plannedFileTargets.Add(to)) return $"Two clips would get the same file name \"{name}\".";
            if (!SameIgnoringCase(from, to) && (File.Exists(to) || Directory.Exists(to)) && !_files.ContainsKey(to))
                return $"Can't rename \"{Path.GetFileName(from)}\": a file called \"{name}\" already exists in that folder.";
            if (IsLocked(from))
                return $"\"{Path.GetFileName(from)}\" is open in another program or still being copied. Close it and try again.";
        }
        foreach (var (from, name) in _folders)
        {
            if (!Directory.Exists(from)) return $"The folder \"{Path.GetFileName(from)}\" is no longer there. Scan again.";
            var to = Path.Combine(Path.GetDirectoryName(from)!, name);
            if (!SameIgnoringCase(from, to) && (Directory.Exists(to) || File.Exists(to)))
                return $"Can't rename the folder \"{Path.GetFileName(from)}\": \"{name}\" already exists next to it.";
        }

        // 2. Files first (using their current folders), then folders deepest first.
        try
        {
            foreach (var (from, name) in _files) Move(from, Path.Combine(Path.GetDirectoryName(from)!, name), dir: false);
            foreach (var (from, name) in _folders.OrderByDescending(kv => kv.Key.Length))
                Move(from, Path.Combine(Path.GetDirectoryName(from)!, name), dir: true);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var what = ex is UnauthorizedAccessException
                ? "Windows didn't allow the rename (the folder may be open in File Explorer, or read-only)."
                : $"Windows didn't allow the rename: {ex.Message}";
            Rollback();
            return what + " Nothing was changed. Close any windows using these files and try again.";
        }
    }

    private void Move(string from, string to, bool dir)
    {
        if (string.Equals(from, to, StringComparison.Ordinal)) return;
        if (SameIgnoringCase(from, to))
        {
            // Case-only change: go through a temporary name (Windows treats them as the same name).
            var tmp = from + ".fvtmp" + Guid.NewGuid().ToString("N")[..6];
            DoMove(from, tmp, dir);
            _done.Add((from, tmp, dir));
            DoMove(tmp, to, dir);
            _done.Add((tmp, to, dir));
            return;
        }
        DoMove(from, to, dir);
        _done.Add((from, to, dir));
    }

    private void DoMove(string from, string to, bool dir)
    {
        if (FailOnMove > 0 && ++_moves == FailOnMove) throw new IOException("Simulated failure");
        if (dir) Directory.Move(from, to);
        else File.Move(from, to);
    }

    private void Rollback()
    {
        for (var i = _done.Count - 1; i >= 0; i--)
        {
            var (from, to, dir) = _done[i];
            try
            {
                if (dir) Directory.Move(to, from);
                else File.Move(to, from);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Error($"Could not undo rename {to} -> {from}", ex);
            }
        }
        _done.Clear();
    }

    private static bool SameIgnoringCase(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool IsLocked(string path)
    {
        try
        {
            using var _ = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return false; }   // read-only file: the move itself will tell us
    }
}
