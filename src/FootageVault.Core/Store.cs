using System.Text.Json;
using System.Text.Json.Serialization;

namespace FootageVault.Core;

public enum ClipState { Queued, Uploading, Uploaded, Failed }

/// <summary>One clip in the queue or history. Numbers and titles are fixed when it is queued.</summary>
public sealed class ClipRecord
{
    public long Id { get; set; }
    public string Story { get; set; } = "";
    public int Seq { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public string OriginalName { get; set; } = "";
    public long Size { get; set; }
    public long ModifiedTicks { get; set; }
    public string Fingerprint { get; set; } = "";
    public string RecDate { get; set; } = "";          // yyyy-MM-dd
    public ClipState State { get; set; } = ClipState.Queued;
    public string? VideoId { get; set; }
    public string? Privacy { get; set; }
    public int Attempts { get; set; }
    public string? Error { get; set; }
    public string? ResumableUri { get; set; }
    public long BytesSent { get; set; }
    public bool Restarted { get; set; }
    public bool Hidden { get; set; }
    public DateTimeOffset QueuedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }

    public ClipRecord Clone() => (ClipRecord)MemberwiseClone();
}

internal sealed class StoreData
{
    public long NextId { get; set; } = 1;
    public List<ClipRecord> Clips { get; set; } = new();
    public Dictionary<string, int> UploadsPerDay { get; set; } = new();
}

/// <summary>
/// Queue and history, saved to a JSON file after every change (written to a temp file and swapped in,
/// so a crash or power cut never leaves a half-written file). Thread safe.
/// </summary>
public sealed class Store
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly object _lock = new();
    private StoreData _data;

    public Store(string path)
    {
        _path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _data = Load(path);
    }

    private static StoreData Load(string path)
    {
        foreach (var p in new[] { path, path + ".bak" })
        {
            try
            {
                if (File.Exists(p)) return JsonSerializer.Deserialize<StoreData>(File.ReadAllText(p), Json) ?? new();
            }
            catch (Exception ex) when (ex is IOException or JsonException) { }
        }
        return new StoreData();
    }

    private void Save()
    {
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_data, Json));
        if (File.Exists(_path)) File.Replace(tmp, _path, _path + ".bak", ignoreMetadataErrors: true);
        else File.Move(tmp, _path);
    }

    public ClipRecord Add(ClipRecord r)
    {
        lock (_lock)
        {
            r.Id = _data.NextId++;
            if (r.QueuedAt == default) r.QueuedAt = DateTimeOffset.UtcNow;
            _data.Clips.Add(r.Clone());
            Save();
            return r.Clone();
        }
    }

    public void AddRange(IEnumerable<ClipRecord> records)
    {
        lock (_lock)
        {
            foreach (var r in records)
            {
                r.Id = _data.NextId++;
                if (r.QueuedAt == default) r.QueuedAt = DateTimeOffset.UtcNow;
                _data.Clips.Add(r.Clone());
            }
            Save();
        }
    }

    public ClipRecord? Get(long id)
    {
        lock (_lock) return _data.Clips.FirstOrDefault(c => c.Id == id)?.Clone();
    }

    public void Update(long id, Action<ClipRecord> change)
    {
        lock (_lock)
        {
            var r = _data.Clips.FirstOrDefault(c => c.Id == id);
            if (r is null) return;
            change(r);
            Save();
        }
    }

    public bool Delete(long id)
    {
        lock (_lock)
        {
            var n = _data.Clips.RemoveAll(c => c.Id == id);
            if (n > 0) Save();
            return n > 0;
        }
    }

    public List<ClipRecord> All()
    {
        lock (_lock) return _data.Clips.Select(c => c.Clone()).ToList();
    }

    public List<ClipRecord> QueueRows()
    {
        lock (_lock) return _data.Clips.Where(c => !c.Hidden).Select(c => c.Clone()).ToList();
    }

    public int NextSequence(string story, DateOnly date)
    {
        var d = date.ToString("yyyy-MM-dd");
        lock (_lock)
        {
            var max = _data.Clips.Where(c => string.Equals(c.Story, story, StringComparison.OrdinalIgnoreCase) && c.RecDate == d)
                .Select(c => c.Seq).DefaultIfEmpty(0).Max();
            return max + 1;
        }
    }

    public ClipRecord? FindUploaded(string fingerprint)
    {
        lock (_lock)
            return _data.Clips.LastOrDefault(c => c.Fingerprint == fingerprint && c.State == ClipState.Uploaded)?.Clone();
    }

    public ClipRecord? FindPending(string fingerprint)
    {
        lock (_lock)
            return _data.Clips.LastOrDefault(c => c.Fingerprint == fingerprint && c.State != ClipState.Uploaded)?.Clone();
    }

    /// <summary>Next clip to upload: clips left "Uploading" by an earlier session go first.</summary>
    public ClipRecord? ClaimNext(ICollection<long> exclude)
    {
        lock (_lock)
        {
            var r = _data.Clips.Where(c => (c.State == ClipState.Uploading || c.State == ClipState.Queued) && !exclude.Contains(c.Id))
                .OrderByDescending(c => c.State == ClipState.Uploading).ThenBy(c => c.Id).FirstOrDefault();
            if (r is null) return null;
            r.State = ClipState.Uploading;
            r.Attempts++;
            Save();
            return r.Clone();
        }
    }

    public bool HasWaiting(ICollection<long> exclude)
    {
        lock (_lock)
            return _data.Clips.Any(c => (c.State == ClipState.Queued || c.State == ClipState.Uploading) && !exclude.Contains(c.Id));
    }

    public Dictionary<ClipState, int> Counts()
    {
        lock (_lock)
        {
            var d = Enum.GetValues<ClipState>().ToDictionary(s => s, _ => 0);
            foreach (var c in _data.Clips.Where(c => !c.Hidden)) d[c.State]++;
            return d;
        }
    }

    public int RetryFailed()
    {
        lock (_lock)
        {
            var n = 0;
            foreach (var c in _data.Clips.Where(c => c.State == ClipState.Failed))
            {
                c.State = ClipState.Queued;
                c.Error = null;
                c.ResumableUri = null;
                c.BytesSent = 0;
                n++;
            }
            if (n > 0) Save();
            return n;
        }
    }

    public void HideFinished()
    {
        lock (_lock)
        {
            foreach (var c in _data.Clips.Where(c => c.State == ClipState.Uploaded)) c.Hidden = true;
            Save();
        }
    }

    public List<ClipRecord> History(string search = "", string? story = null)
    {
        lock (_lock)
        {
            IEnumerable<ClipRecord> q = _data.Clips.Where(c => c.State is ClipState.Uploaded or ClipState.Failed);
            if (!string.IsNullOrWhiteSpace(search))
            {
                var s = search.Trim();
                q = q.Where(c => c.Title.Contains(s, StringComparison.OrdinalIgnoreCase)
                                 || c.Story.Contains(s, StringComparison.OrdinalIgnoreCase)
                                 || c.SourcePath.Contains(s, StringComparison.OrdinalIgnoreCase)
                                 || c.OriginalName.Contains(s, StringComparison.OrdinalIgnoreCase)
                                 || (c.VideoId ?? "").Contains(s, StringComparison.OrdinalIgnoreCase));
            }
            if (!string.IsNullOrEmpty(story)) q = q.Where(c => string.Equals(c.Story, story, StringComparison.OrdinalIgnoreCase));
            return q.OrderByDescending(c => c.Id).Select(c => c.Clone()).ToList();
        }
    }

    public List<string> StoryNames()
    {
        lock (_lock)
            return _data.Clips.Select(c => c.Story).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s, NaturalComparer.Instance).ToList();
    }

    public int UploadsOn(string quotaDay)
    {
        lock (_lock) return _data.UploadsPerDay.TryGetValue(quotaDay, out var n) ? n : 0;
    }

    public void AddUpload(string quotaDay)
    {
        lock (_lock)
        {
            _data.UploadsPerDay[quotaDay] = UploadsOn(quotaDay) + 1;
            Save();
        }
    }
}

public sealed class AppSettings
{
    public string DescriptionTemplate { get; set; } = "Raw footage · {Story} · {Date}";
    public int ParallelUploads { get; set; } = 2;
    public int DailyLimit { get; set; } = 100;
    public string LogFolder { get; set; } = "";
    public string Privacy { get; set; } = "private";
    public bool AuditApproved { get; set; }
    public bool RenameOnDisk { get; set; } = true;
    public string Theme { get; set; } = "system";

    public string EffectivePrivacy => AuditApproved && Privacy == "unlisted" ? "unlisted" : "private";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Load(string path)
    {
        try
        {
            if (File.Exists(path)) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException) { }
        return new AppSettings();
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}
