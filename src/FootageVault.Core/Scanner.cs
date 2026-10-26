using System.Security.Cryptography;

namespace FootageVault.Core;

/// <summary>Reads the recording date and duration from a video file (platform specific).</summary>
public interface IMediaInfoReader
{
    /// <returns>Recording time (any offset; null when the file has none) and duration in ms (0 if unknown).</returns>
    Task<(DateTimeOffset? Recorded, long DurationMs)> ReadAsync(string path);
}

public sealed class NoMediaInfo : IMediaInfoReader
{
    public Task<(DateTimeOffset?, long)> ReadAsync(string path) => Task.FromResult<(DateTimeOffset?, long)>((null, 0));
}

public sealed record ScannedClip(
    string Path, long Size, DateTime ModifiedUtc, DateTimeOffset RecordedIst, bool DateFromMetadata,
    long DurationMs, string Fingerprint)
{
    public string FileName => System.IO.Path.GetFileName(Path);
    public string Extension => System.IO.Path.GetExtension(Path);
}

public sealed record SkippedFile(string Path, string Reason);

/// <summary>
/// One story = one folder that directly contains videos.
/// <see cref="Segments"/> are folder names from the top story folder down, e.g. ["Collector Visit", "Phone 1"].
/// </summary>
public sealed class ScannedStory
{
    public required string FolderPath { get; init; }
    public required string TopFolderPath { get; init; }
    public required IReadOnlyList<string> Segments { get; init; }
    public List<ScannedClip> Clips { get; } = new();
    public bool Nested => Segments.Count > 1;
}

public sealed class ScanResult
{
    public List<ScannedStory> Stories { get; } = new();
    public List<SkippedFile> Skipped { get; } = new();
}

public static class Fingerprint
{
    public const int HashBytes = 8 * 1024 * 1024;

    /// <summary>size + SHA-256 of the first and last 8 MB + duration. Survives renames and moves.</summary>
    public static string Compute(string path, long size, long durationMs)
    {
        using var sha = SHA256.Create();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        var buf = new byte[HashBytes];
        var n = ReadFully(fs, buf);
        sha.TransformBlock(buf, 0, n, null, 0);
        if (size > HashBytes)
        {
            fs.Seek(Math.Max(HashBytes, size - HashBytes), SeekOrigin.Begin);
            n = ReadFully(fs, buf);
            sha.TransformBlock(buf, 0, n, null, 0);
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return $"{size}-{Convert.ToHexString(sha.Hash!)[..40].ToLowerInvariant()}-{durationMs}";
    }

    private static int ReadFully(Stream s, byte[] buf)
    {
        int total = 0, n;
        while (total < buf.Length && (n = s.Read(buf, total, buf.Length - total)) > 0) total += n;
        return total;
    }
}

public sealed class Scanner
{
    public static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mov", ".m4v", ".mts", ".m2ts", ".avi", ".mkv", ".3gp", ".wmv", ".mxf" };

    private readonly IMediaInfoReader _reader;
    private readonly TimeSpan _stableWait;

    public Scanner(IMediaInfoReader reader, TimeSpan? stableWait = null)
    {
        _reader = reader;
        _stableWait = stableWait ?? TimeSpan.FromSeconds(3);
    }

    /// <summary>
    /// Scan one or more chosen folders. Each chosen folder is a story; videos in its sub-folders become nested
    /// stories named "Top - Sub - ...". To add several stories at once, choose several folders.
    /// </summary>
    public async Task<ScanResult> ScanAsync(IEnumerable<string> roots, IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var result = new ScanResult();
        var candidates = new List<(string Dir, FileInfo File, string Top)>();
        var seenRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawRoot in roots)
        {
            var root = Path.GetFullPath(rawRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!Directory.Exists(root) || !seenRoots.Add(root)) continue;
            foreach (var (dir, file) in Walk(root, result.Skipped))
            {
                var top = root;     // every chosen folder is a story; sub-folders become "Top - Sub" stories
                candidates.Add((dir, file, top));
            }
        }

        // Files still being copied: size or time must not change across two checks.
        if (candidates.Count > 0 && _stableWait > TimeSpan.Zero)
        {
            progress?.Report("Checking that files have finished copying...");
            await Task.Delay(_stableWait, ct);
        }

        var stories = new Dictionary<string, ScannedStory>(StringComparer.OrdinalIgnoreCase);
        var i = 0;
        foreach (var (dir, file, top) in candidates)
        {
            ct.ThrowIfCancellationRequested();
            i++;
            var before = (file.Length, file.LastWriteTimeUtc);
            file.Refresh();
            if (!file.Exists)
            {
                result.Skipped.Add(new SkippedFile(file.FullName, "File disappeared during the scan"));
                continue;
            }
            if (file.Length != before.Length || file.LastWriteTimeUtc != before.LastWriteTimeUtc)
            {
                result.Skipped.Add(new SkippedFile(file.FullName, "Still being copied (size changed). Scan again later."));
                continue;
            }
            progress?.Report($"Reading {file.Name} ({i} of {candidates.Count})");
            try
            {
                var (recorded, duration) = await _reader.ReadAsync(file.FullName);
                if (recorded is { } r && r.Year < 1990) recorded = null;
                var fromMeta = recorded is not null;
                var ist = TimeZones.ToIst(recorded ?? new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero));
                var fp = await Task.Run(() => Fingerprint.Compute(file.FullName, file.Length, duration), ct);

                if (!stories.TryGetValue(dir, out var story))
                {
                    var segments = new List<string> { Titles.Clean(Path.GetFileName(top)) };
                    var rel = Path.GetRelativePath(top, dir);
                    if (rel != ".") segments.AddRange(rel.Split(Path.DirectorySeparatorChar).Select(Titles.Clean));
                    story = new ScannedStory { FolderPath = dir, TopFolderPath = top, Segments = segments };
                    stories[dir] = story;
                }
                story.Clips.Add(new ScannedClip(file.FullName, file.Length, file.LastWriteTimeUtc, ist, fromMeta, duration, fp));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                result.Skipped.Add(new SkippedFile(file.FullName, $"Cannot read file ({ex.Message})"));
            }
        }

        foreach (var s in stories.Values)
        {
            var sorted = s.Clips.OrderBy(c => c.RecordedIst).ThenBy(c => c.FileName, NaturalComparer.Instance).ToList();
            s.Clips.Clear();
            s.Clips.AddRange(sorted);
        }
        result.Stories.AddRange(stories.Values
            .OrderBy(s => s.TopFolderPath, NaturalComparer.Instance)
            .ThenBy(s => s.Segments.Count)
            .ThenBy(s => s.FolderPath, NaturalComparer.Instance));
        return result;
    }

    private static bool IsHidden(FileSystemInfo fi)
    {
        if (fi.Name.StartsWith('.') || fi.Name.StartsWith("~$")) return true;
        try { return (fi.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0; }
        catch (IOException) { return false; }
    }

    private static IEnumerable<(string Dir, FileInfo File)> Walk(string root, List<SkippedFile> skipped)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            FileInfo[] files;
            DirectoryInfo[] subdirs;
            try
            {
                var di = new DirectoryInfo(dir);
                files = di.GetFiles();
                subdirs = di.GetDirectories();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skipped.Add(new SkippedFile(dir, $"Cannot open folder ({ex.Message})"));
                continue;
            }
            foreach (var f in files.OrderBy(f => f.Name, NaturalComparer.Instance))
            {
                if (IsHidden(f)) skipped.Add(new SkippedFile(f.FullName, "Hidden or system file"));
                else if (!VideoExtensions.Contains(f.Extension)) skipped.Add(new SkippedFile(f.FullName, "Not a video file type"));
                else if (f.Length == 0) skipped.Add(new SkippedFile(f.FullName, "Empty file (0 bytes)"));
                else yield return (dir, f);
            }
            foreach (var d in subdirs.OrderByDescending(d => d.Name, NaturalComparer.Instance))
            {
                if (IsHidden(d) || d.Name.StartsWith('$')) continue;
                if ((d.Attributes & FileAttributes.ReparsePoint) != 0) continue;     // don't follow links
                stack.Push(d.FullName);
            }
        }
    }
}

/// <summary>Natural order: VID_2 before VID_10, case-insensitive.</summary>
public sealed class NaturalComparer : IComparer<string?>
{
    public static readonly NaturalComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;
        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
            {
                int si = i, sj = j;
                while (i < x.Length && char.IsDigit(x[i])) i++;
                while (j < y.Length && char.IsDigit(y[j])) j++;
                var a = x[si..i].TrimStart('0');
                var b = y[sj..j].TrimStart('0');
                if (a.Length != b.Length) return a.Length.CompareTo(b.Length);
                var c = string.CompareOrdinal(a, b);
                if (c != 0) return c;
            }
            else
            {
                var c = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
                if (c != 0) return c;
                i++;
                j++;
            }
        }
        return (x.Length - i).CompareTo(y.Length - j);
    }
}
