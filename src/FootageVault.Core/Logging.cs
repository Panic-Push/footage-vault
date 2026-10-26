using System.Globalization;
using System.Text;

namespace FootageVault.Core;

/// <summary>Technical log for troubleshooting, one file per day, kept 14 days. Never contains tokens.</summary>
public static class Log
{
    private static readonly object Lock = new();
    private static string? _folder;

    public static void Init(string folder)
    {
        _folder = folder;
        try
        {
            Directory.CreateDirectory(folder);
            foreach (var f in Directory.GetFiles(folder, "app-*.log"))
                if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-14)) File.Delete(f);
        }
        catch (IOException) { }
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", ex is null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        if (_folder is null) return;
        var now = DateTimeOffset.Now;
        lock (Lock)
        {
            try
            {
                File.AppendAllText(Path.Combine(_folder, $"app-{now:yyyy-MM-dd}.log"),
                    $"{now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}{Environment.NewLine}", Encoding.UTF8);
            }
            catch (IOException) { }
        }
    }
}

/// <summary>Excel-friendly upload log (one row per attempt).</summary>
public static class CsvLog
{
    public static readonly string[] Columns =
    {
        "timestamp_ist", "story", "sequence", "title", "source_path", "original_name", "file_size_mb", "fingerprint",
        "recording_date", "status", "youtube_video_id", "youtube_url", "privacy", "attempts", "error_message",
    };

    private static readonly object Lock = new();

    public static string Escape(string? v)
    {
        v ??= "";
        return v.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }

    public static string Row(ClipRecord c, string status, string error, DateTimeOffset now)
    {
        var url = c.VideoId is { Length: > 0 } ? $"https://www.youtube.com/watch?v={c.VideoId}" : "";
        var fields = new[]
        {
            TimeZones.ToIst(now).ToString("yyyy-MM-dd HH:mm:ss"), c.Story, c.Seq.ToString(), c.Title, c.SourcePath,
            c.OriginalName, (c.Size / 1048576.0).ToString("0.00", CultureInfo.InvariantCulture), c.Fingerprint,
            c.RecDate, status, c.VideoId ?? "", url, c.Privacy ?? "", c.Attempts.ToString(), error,
        };
        return string.Join(",", fields.Select(Escape));
    }

    /// <summary>Never throws: a log open in Excel must not stop uploads.</summary>
    public static void Append(string folder, ClipRecord c, string status, string error = "")
    {
        var row = Row(c, status, error, DateTimeOffset.UtcNow);
        lock (Lock)
        {
            try
            {
                Directory.CreateDirectory(folder);
                try { WriteRow(Path.Combine(folder, "upload_log.csv"), row); }
                catch (IOException) { WriteRow(Path.Combine(folder, "upload_log_while_locked.csv"), row); }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"Could not write the CSV log: {ex.Message}");
            }
        }
    }

    private static void WriteRow(string path, string row)
    {
        var isNew = !File.Exists(path) || new FileInfo(path).Length == 0;
        using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        using var w = new StreamWriter(fs, isNew ? new UTF8Encoding(true) : new UTF8Encoding(false));
        if (isNew) w.Write(string.Join(",", Columns) + "\r\n");
        w.Write(row + "\r\n");
    }
}
