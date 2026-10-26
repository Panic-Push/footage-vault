using System.Diagnostics;
using System.Reflection;

namespace FootageVault.Core.Tests;

[AttributeUsage(AttributeTargets.Method)]
public sealed class TestAttribute : Attribute { }

public sealed class AssertFailed : Exception
{
    public AssertFailed(string m) : base(m) { }
}

public static class Assert
{
    public static void True(bool cond, string what = "")
    {
        if (!cond) throw new AssertFailed("Expected true. " + what);
    }

    public static void False(bool cond, string what = "") => True(!cond, what);

    public static void Equal<T>(T expected, T actual, string what = "")
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new AssertFailed($"Expected <{expected}> but got <{actual}>. {what}");
    }

    public static void Contains(string needle, string? hay, string what = "")
    {
        if (hay is null || !hay.Contains(needle, StringComparison.OrdinalIgnoreCase))
            throw new AssertFailed($"Expected \"{hay}\" to contain \"{needle}\". {what}");
    }

    public static T Throws<T>(Action a) where T : Exception
    {
        try { a(); }
        catch (T ex) { return ex; }
        catch (Exception ex) { throw new AssertFailed($"Expected {typeof(T).Name} but got {ex.GetType().Name}: {ex.Message}"); }
        throw new AssertFailed($"Expected {typeof(T).Name} but nothing was thrown");
    }

    public static async Task<T> ThrowsAsync<T>(Func<Task> a) where T : Exception
    {
        try { await a(); }
        catch (T ex) { return ex; }
        catch (Exception ex) { throw new AssertFailed($"Expected {typeof(T).Name} but got {ex.GetType().Name}: {ex.Message}"); }
        throw new AssertFailed($"Expected {typeof(T).Name} but nothing was thrown");
    }

    public static async Task Eventually(Func<bool> cond, string what, int timeoutMs = 10000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (cond()) return;
            await Task.Delay(50);
        }
        throw new AssertFailed("Timed out waiting for: " + what);
    }
}

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var filter = args.FirstOrDefault();
        var tests = typeof(Program).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static).Select(m => (t, m)))
            .Where(x => x.m.GetCustomAttribute<TestAttribute>() is not null)
            .Where(x => filter is null || $"{x.t.Name}.{x.m.Name}".Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.t.Name).ThenBy(x => x.m.Name).ToList();
        int passed = 0, failed = 0;
        foreach (var (t, m) in tests)
        {
            var name = $"{t.Name}.{m.Name}";
            var sw = Stopwatch.StartNew();
            try
            {
                var r = m.Invoke(null, null);
                if (r is Task task)
                {
                    var finished = await Task.WhenAny(task, Task.Delay(60000));
                    if (finished != task) throw new AssertFailed("Test timed out after 60 s");
                    await task;
                }
                passed++;
                Console.WriteLine($"  PASS  {name} ({sw.ElapsedMilliseconds} ms)");
            }
            catch (Exception ex)
            {
                var inner = ex is TargetInvocationException { InnerException: { } ie } ? ie : ex;
                failed++;
                Console.WriteLine($"  FAIL  {name}: {inner.GetType().Name}: {inner.Message}");
                if (inner is not AssertFailed) Console.WriteLine(inner.StackTrace);
            }
        }
        Console.WriteLine($"\n{passed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }
}

/// <summary>A temporary folder deleted afterwards.</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fvtest_" + Guid.NewGuid().ToString("N")[..8]);

    public TempDir() => Directory.CreateDirectory(Path);

    public string File(string rel, int size = 1000, byte fill = 1)
    {
        var p = System.IO.Path.Combine(Path, rel);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!);
        var data = new byte[size];
        Array.Fill(data, fill);
        if (size > 4) BitConverter.GetBytes(rel.GetHashCode()).CopyTo(data, 0);   // different content per file
        System.IO.File.WriteAllBytes(p, data);
        return p;
    }

    public string Sub(string rel) => System.IO.Path.Combine(Path, rel);

    public void Dispose()
    {
        try { Directory.Delete(Path, true); } catch (IOException) { }
    }
}

public sealed class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 26, 6, 0, 0, TimeSpan.Zero);
}

/// <summary>Media reader returning fixed dates by file name.</summary>
public sealed class FakeMedia : IMediaInfoReader
{
    public Dictionary<string, DateTimeOffset> Dates { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task<(DateTimeOffset?, long)> ReadAsync(string path) =>
        Task.FromResult<(DateTimeOffset?, long)>((Dates.TryGetValue(System.IO.Path.GetFileName(path), out var d) ? d : null, 1000));
}
