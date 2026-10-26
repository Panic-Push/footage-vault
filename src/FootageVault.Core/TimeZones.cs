namespace FootageVault.Core;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public static class TimeZones
{
    public static readonly TimeZoneInfo Ist = Find("Asia/Kolkata", "India Standard Time");
    public static readonly TimeZoneInfo Pacific = Find("America/Los_Angeles", "Pacific Standard Time");

    private static TimeZoneInfo Find(string iana, string windows)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(iana); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById(windows); }
    }

    public static DateTimeOffset ToIst(DateTimeOffset t) => TimeZoneInfo.ConvertTime(t, Ist);

    public static string FormatIst(DateTimeOffset t) => ToIst(t).ToString("dd MMM, h:mm tt") + " IST";
}

/// <summary>The YouTube quota day follows Pacific Time: it resets at midnight in California.</summary>
public static class Quota
{
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(2);

    public static string Day(DateTimeOffset utc) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utc, TimeZones.Pacific).DateTime).ToString("yyyy-MM-dd");

    public static DateTimeOffset NextReset(DateTimeOffset utc)
    {
        var pt = TimeZoneInfo.ConvertTime(utc, TimeZones.Pacific);
        var nextMidnight = pt.Date.AddDays(1);                     // local Pacific wall-clock time
        var offset = TimeZones.Pacific.GetUtcOffset(nextMidnight);
        return new DateTimeOffset(nextMidnight, offset).ToUniversalTime();
    }

    public static DateTimeOffset ResumeTime(DateTimeOffset utc) => NextReset(utc) + Grace;
}
