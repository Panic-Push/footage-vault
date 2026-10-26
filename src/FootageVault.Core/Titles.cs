using System.Text;
using System.Text.RegularExpressions;

namespace FootageVault.Core;

/// <summary>YouTube titles, descriptions and on-disk names.</summary>
public static partial class Titles
{
    public const int MaxTitle = 100;

    [GeneratedRegex(@"\s+")] private static partial Regex Spaces();

    /// <summary>Trim surplus spaces and remove characters YouTube rejects (&lt; &gt;) or control characters.</summary>
    public static string Clean(string? name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            if (ch == '<' || ch == '>') continue;
            sb.Append(char.IsControl(ch) ? ' ' : ch);
        }
        return Spaces().Replace(sb.ToString(), " ").Trim();
    }

    public static string DateText(DateOnly d) => $"{d.Day:00}.{d.Month:00}.{d.Year % 100:00}";

    /// <summary>Parse dd.mm.yy (also dd.mm.yyyy, and / or - as separators).</summary>
    public static bool TryParseDate(string? text, out DateOnly date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Trim().Split('.', '/', '-');
        if (parts.Length != 3) return false;
        if (!int.TryParse(parts[0], out var d) || !int.TryParse(parts[1], out var m) || !int.TryParse(parts[2], out var y))
            return false;
        if (parts[2].Length <= 2) y += 2000;
        if (y < 1990 || y > 2100 || m < 1 || m > 12 || d < 1 || d > DateTime.DaysInMonth(y, m)) return false;
        date = new DateOnly(y, m, d);
        return true;
    }

    /// <summary>Flat story: "Collector Visit 1 (26.10.26)". Nested: "Collector Visit - Phone 1 - 1 (26.10.26)".</summary>
    public static string Build(string storyName, bool nested, int number, DateOnly date)
    {
        var suffix = (nested ? $" - {number}" : $" {number}") + $" ({DateText(date)})";
        var story = Clean(storyName);
        if (story.Length == 0) story = "Untitled";
        var room = MaxTitle - suffix.Length;
        if (story.Length > room) story = story[..Math.Max(room, 1)].TrimEnd(' ', '-');
        return story + suffix;
    }

    public static string Description(string template, string story, DateOnly date)
    {
        var text = (template ?? "").Replace("{Story}", Clean(story)).Replace("{Date}", DateText(date));
        text = text.Replace("<", "").Replace(">", "");
        return text.Length > 5000 ? text[..5000] : text;
    }

    private static readonly char[] InvalidName = Path.GetInvalidFileNameChars()
        .Concat(new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' }).Distinct().ToArray();

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>A name that Windows accepts for a file or folder (no extension handling).</summary>
    public static string SafeFileName(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name) sb.Append(Array.IndexOf(InvalidName, ch) >= 0 || char.IsControl(ch) ? '_' : ch);
        var result = sb.ToString().Trim().TrimEnd('.', ' ');
        if (result.Length == 0) result = "_";
        if (Reserved.Contains(result)) result = "_" + result;
        return result.Length > 180 ? result[..180].TrimEnd('.', ' ') : result;
    }
}
