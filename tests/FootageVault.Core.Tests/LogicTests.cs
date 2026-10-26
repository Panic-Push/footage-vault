namespace FootageVault.Core.Tests;

public static class TitleTests
{
    [Test]
    public static void FlatAndNestedFormats()
    {
        var d = new DateOnly(2026, 10, 26);
        Assert.Equal("Collector Visit 1 (26.10.26)", Titles.Build("Collector Visit", false, 1, d));
        Assert.Equal("Collector Visit - Phone 1 - 1 (26.10.26)", Titles.Build("Collector Visit - Phone 1", true, 1, d));
    }

    [Test]
    public static void LongNameKeepsNumberAndDate()
    {
        var t = Titles.Build(new string('X', 300), true, 123, new DateOnly(2026, 1, 2));
        Assert.Equal(100, t.Length);
        Assert.True(t.EndsWith(" - 123 (02.01.26)"), t);
    }

    [Test]
    public static void CleaningAndDescription()
    {
        Assert.Equal("Big Story", Titles.Clean("  Big   <Story>  "));
        Assert.Equal("Raw footage · Flood · 26.10.26",
            Titles.Description("Raw footage · {Story} · {Date}", "Flood", new DateOnly(2026, 10, 26)));
    }

    [Test]
    public static void DateParsing()
    {
        Assert.True(Titles.TryParseDate("26.10.26", out var a));
        Assert.Equal(new DateOnly(2026, 10, 26), a);
        Assert.True(Titles.TryParseDate("1/2/2027", out var b));
        Assert.Equal(new DateOnly(2027, 2, 1), b);
        Assert.False(Titles.TryParseDate("31.02.26", out _));
        Assert.False(Titles.TryParseDate("26.10", out _));
        Assert.False(Titles.TryParseDate("dd.mm.yy", out _));
    }

    [Test]
    public static void SafeFileNames()
    {
        Assert.Equal("A_B_C", Titles.SafeFileName("A:B?C"));
        Assert.Equal("_CON", Titles.SafeFileName("CON"));
        Assert.Equal("Name", Titles.SafeFileName("Name. "));
    }

    [Test]
    public static void NaturalOrder()
    {
        var list = new[] { "VID_10.mp4", "vid_2.mp4", "VID_1.mp4" }.OrderBy(x => x, NaturalComparer.Instance).ToArray();
        Assert.Equal("VID_1.mp4,vid_2.mp4,VID_10.mp4", string.Join(",", list));
    }
}

public static class QuotaTests
{
    [Test]
    public static void ResetIsMidnightPacificInIst()
    {
        var summer = Quota.NextReset(new DateTimeOffset(2026, 7, 10, 12, 0, 0, TimeSpan.Zero));
        Assert.Equal("12:30", TimeZones.ToIst(summer).ToString("HH:mm"));
        var winter = Quota.NextReset(new DateTimeOffset(2026, 12, 10, 12, 0, 0, TimeSpan.Zero));
        Assert.Equal("13:30", TimeZones.ToIst(winter).ToString("HH:mm"));
    }

    [Test]
    public static void QuotaDayFollowsPacific()
    {
        Assert.Equal("2026-10-26", Quota.Day(new DateTimeOffset(2026, 10, 27, 1, 0, 0, TimeSpan.Zero)));
    }

    [Test]
    public static void IstConversionGivesRightDateAfterMidnight()
    {
        // 20:30 UTC on 25 Oct = 02:00 IST on 26 Oct
        var ist = TimeZones.ToIst(new DateTimeOffset(2026, 10, 25, 20, 30, 0, TimeSpan.Zero));
        Assert.Equal(new DateTime(2026, 10, 26, 2, 0, 0), ist.DateTime);
    }
}

public static class ScannerTests
{
    private static Scanner NewScanner(IMediaInfoReader? r = null) => new(r ?? new FakeMedia(), TimeSpan.Zero);

    [Test]
    public static async Task FlatStoryFolderAndSkips()
    {
        using var t = new TempDir();
        t.File("Collector Visit/VID_2.MOV");
        t.File("Collector Visit/VID_10.MOV");
        t.File("Collector Visit/notes.txt");
        t.File("Collector Visit/empty.mp4", 0);
        t.File("Collector Visit/.hidden.mp4");
        var r = await NewScanner().ScanAsync(new[] { t.Sub("Collector Visit") });
        Assert.Equal(1, r.Stories.Count);
        Assert.Equal("Collector Visit", string.Join("/", r.Stories[0].Segments));
        Assert.Equal("VID_2.MOV,VID_10.MOV", string.Join(",", r.Stories[0].Clips.Select(c => c.FileName)));
        Assert.Equal(3, r.Skipped.Count);
        Assert.True(r.Skipped.Any(s => s.Reason.Contains("Empty")));
        Assert.True(r.Skipped.Any(s => s.Reason.Contains("Hidden")));
        Assert.True(r.Skipped.Any(s => s.Reason.Contains("Not a video")));
    }

    [Test]
    public static async Task SeveralStoryFoldersWithNestedOnes()
    {
        using var t = new TempDir();
        t.File("Footage/Collector Visit/main.mp4");
        t.File("Footage/Collector Visit/Phone 1/a.mp4");
        t.File("Footage/Collector Visit/Phone 2/b.mp4");
        t.File("Footage/Flood Relief/c.mp4");
        t.File("Footage/Rally/Cam/Card 1/d.mp4");
        var r = await NewScanner().ScanAsync(new[]
            { t.Sub("Footage/Collector Visit"), t.Sub("Footage/Flood Relief"), t.Sub("Footage/Rally") });
        var names = r.Stories.Select(s => string.Join(" - ", s.Segments)).ToList();
        Assert.Equal("Collector Visit,Collector Visit - Phone 1,Collector Visit - Phone 2,Flood Relief,Rally - Cam - Card 1",
            string.Join(",", names));
        Assert.True(r.Stories[1].Nested);
        Assert.Equal(t.Sub("Footage/Collector Visit"), r.Stories[1].TopFolderPath);
    }

    [Test]
    public static async Task StoryFolderChosenDirectlyKeepsItsNameAsTop()
    {
        using var t = new TempDir();
        t.File("Collector Visit/Phone 1/a.mp4");
        t.File("Collector Visit/x.mp4");
        var r = await NewScanner().ScanAsync(new[] { t.Sub("Collector Visit") });
        Assert.Equal("Collector Visit,Collector Visit - Phone 1",
            string.Join(",", r.Stories.Select(s => string.Join(" - ", s.Segments))));
    }

    [Test]
    public static async Task MultipleFoldersAtOnceAndDuplicatesIgnored()
    {
        using var t = new TempDir();
        t.File("A/a.mp4");
        t.File("B/b.mp4");
        var r = await NewScanner().ScanAsync(new[] { t.Sub("A"), t.Sub("B"), t.Sub("A") });
        Assert.Equal(2, r.Stories.Count);
    }

    [Test]
    public static async Task RecordingTimeBeatsFileName()
    {
        using var t = new TempDir();
        t.File("S/VID_0001.MP4");
        t.File("S/VID_9999.MP4");
        var m = new FakeMedia();
        m.Dates["VID_9999.MP4"] = new DateTimeOffset(2026, 10, 26, 3, 30, 0, TimeSpan.Zero);
        m.Dates["VID_0001.MP4"] = new DateTimeOffset(2026, 10, 26, 4, 30, 0, TimeSpan.Zero);
        var r = await NewScanner(m).ScanAsync(new[] { t.Sub("S") });
        Assert.Equal("VID_9999.MP4,VID_0001.MP4", string.Join(",", r.Stories[0].Clips.Select(c => c.FileName)));
        Assert.True(r.Stories[0].Clips[0].DateFromMetadata);
    }

    [Test]
    public static async Task FileStillGrowingIsSkipped()
    {
        using var t = new TempDir();
        var f = t.File("G/g.mp4");
        var scanner = new Scanner(new FakeMedia(), TimeSpan.FromMilliseconds(600));
        var grow = Task.Run(async () =>
        {
            await Task.Delay(200);
            await File.AppendAllTextAsync(f, "more");
        });
        var r = await scanner.ScanAsync(new[] { t.Sub("G") });
        await grow;
        Assert.Equal(0, r.Stories.Count);
        Assert.Contains("Still being copied", r.Skipped[0].Reason);
    }

    [Test]
    public static void FingerprintSurvivesRenameAndDiffersForOtherContent()
    {
        using var t = new TempDir();
        var a = t.File("a.mp4", 20_000_000, 7);
        var b = t.Sub("renamed.mp4");
        File.Copy(a, b);
        var c = t.File("c.mp4", 20_000_000, 9);
        var fa = Fingerprint.Compute(a, new FileInfo(a).Length, 5);
        Assert.Equal(fa, Fingerprint.Compute(b, new FileInfo(b).Length, 5));
        Assert.False(fa == Fingerprint.Compute(c, new FileInfo(c).Length, 5));
    }
}

public static class PlannerTests
{
    private static readonly DateTimeOffset Day = new(2026, 10, 26, 5, 0, 0, TimeSpan.Zero);  // 10:30 IST

    private static FakeMedia Dates(params string[] files)
    {
        var m = new FakeMedia();
        for (var i = 0; i < files.Length; i++) m.Dates[files[i]] = Day.AddMinutes(i);
        return m;
    }

    [Test]
    public static async Task TitlesForFlatAndNestedStories()
    {
        using var t = new TempDir();
        t.File("Collector Visit/main1.mp4");
        t.File("Collector Visit/Phone 1/a.mp4");
        t.File("Collector Visit/Phone 1/b.mp4");
        var store = new Store(t.Sub("state.json"));
        var scan = await new Scanner(Dates("main1.mp4", "a.mp4", "b.mp4"), TimeSpan.Zero).ScanAsync(new[] { t.Sub("Collector Visit") });
        var plan = Planner.Build(scan, store);
        Assert.Equal(1, plan.Folders.Count);
        var titles = plan.Stories.SelectMany(s => s.Clips).Select(c => c.Title).ToList();
        Assert.Equal("Collector Visit 1 (26.10.26)|Collector Visit - Phone 1 - 1 (26.10.26)|Collector Visit - Phone 1 - 2 (26.10.26)",
            string.Join("|", titles));
    }

    [Test]
    public static async Task FolderDateChangesEveryClipAndRenamesUpdateTitles()
    {
        using var t = new TempDir();
        t.File("Visit/Phone 1/a.mp4");
        t.File("Visit/Phone 1/b.mp4");
        var store = new Store(t.Sub("state.json"));
        var plan = Planner.Build(await new Scanner(Dates("a.mp4", "b.mp4"), TimeSpan.Zero).ScanAsync(new[] { t.Sub("Visit") }), store);
        var story = plan.Stories.Single();
        story.SetDate(new DateOnly(2026, 10, 27));
        story.FolderName = "Camera A";
        plan.Folders[0].Name = "Collector Visit";
        Planner.Renumber(plan, store);
        Assert.Equal("Collector Visit - Camera A - 1 (27.10.26)", story.Clips[0].Title);
        Assert.Equal("Collector Visit - Camera A - 2 (27.10.26)", story.Clips[1].Title);
        Assert.Equal(new DateOnly(2026, 10, 27), story.Date);
        Assert.Equal("", story.Clips[0].Notes);   // date edited: no warning
    }

    [Test]
    public static async Task UntickedClipsDontUseNumbersAndReorderRenumbers()
    {
        using var t = new TempDir();
        t.File("S/a.mp4");
        t.File("S/b.mp4");
        t.File("S/c.mp4");
        var store = new Store(t.Sub("state.json"));
        var plan = Planner.Build(await new Scanner(Dates("a.mp4", "b.mp4", "c.mp4"), TimeSpan.Zero).ScanAsync(new[] { t.Sub("S") }), store);
        var s = plan.Stories.Single();
        s.Clips[0].Selected = false;
        Planner.Renumber(plan, store);
        Assert.Equal("0,1,2", string.Join(",", s.Clips.Select(c => c.Number)));
        s.Clips[0].Selected = true;
        s.Move(2, 0);
        Planner.Renumber(plan, store);
        Assert.Equal("c.mp4,a.mp4,b.mp4", string.Join(",", s.Clips.Select(c => c.Clip.FileName)));
        Assert.Equal("1,2,3", string.Join(",", s.Clips.Select(c => c.Number)));
    }

    [Test]
    public static async Task QueueRenamesFoldersAndFilesOnDisk()
    {
        using var t = new TempDir();
        t.File("Footage/Visit/x.mp4");
        t.File("Footage/Visit/Phone 1/a.MOV");
        t.File("Footage/Visit/Phone 1/b.MOV");
        t.File("Footage/Visit/Phone 1/notes.txt");
        var store = new Store(t.Sub("state.json"));
        var plan = Planner.Build(await new Scanner(Dates("x.mp4", "a.MOV", "b.MOV"), TimeSpan.Zero).ScanAsync(new[] { t.Sub("Footage/Visit") }), store);
        plan.Folders[0].Name = "Collector Visit";
        plan.Stories.Single(s => s.Nested).FolderName = "Camera A";
        var r = Planner.Queue(plan, plan.Folders.ToList(), store, new AppSettings(), renameOnDisk: true);
        Assert.True(r.Success, r.Error ?? "");
        Assert.Equal(3, r.Added);
        var top = t.Sub("Footage/Collector Visit");
        Assert.True(File.Exists(Path.Combine(top, "Collector Visit 1 (26.10.26).mp4")), "flat file renamed");
        Assert.True(File.Exists(Path.Combine(top, "Camera A", "Collector Visit - Camera A - 1 (26.10.26).MOV")));
        Assert.True(File.Exists(Path.Combine(top, "Camera A", "Collector Visit - Camera A - 2 (26.10.26).MOV")));
        Assert.True(File.Exists(Path.Combine(top, "Camera A", "notes.txt")), "non-video untouched");
        Assert.False(Directory.Exists(t.Sub("Footage/Visit")));
        foreach (var rec in store.All())
        {
            Assert.True(File.Exists(rec.SourcePath), "store points at the renamed file: " + rec.SourcePath);
            Assert.Equal(new FileInfo(rec.SourcePath).Length, rec.Size);
        }
        Assert.Equal("a.MOV", store.All().Single(c => c.Seq == 1 && c.Story.Contains("Camera")).OriginalName);
        Assert.Equal(0, plan.Folders.Count);
    }

    [Test]
    public static async Task RenameFailureChangesNothing()
    {
        using var t = new TempDir();
        t.File("Visit/Phone 1/a.mp4");
        t.File("Visit/Phone 1/b.mp4");
        var store = new Store(t.Sub("state.json"));
        var plan = Planner.Build(await new Scanner(Dates("a.mp4", "b.mp4"), TimeSpan.Zero).ScanAsync(new[] { t.Sub("Visit") }), store);
        plan.Folders[0].Name = "Collector Visit";
        var renamer = new DiskRenamer { FailOnMove = 3 };   // 2 file moves succeed, the folder move fails
        var r = Planner.Queue(plan, plan.Folders.ToList(), store, new AppSettings(), true, renamer);
        Assert.False(r.Success);
        Assert.Contains("Nothing was changed", r.Error);
        Assert.True(File.Exists(t.Sub("Visit/Phone 1/a.mp4")), "first file put back");
        Assert.True(File.Exists(t.Sub("Visit/Phone 1/b.mp4")), "second file put back");
        Assert.Equal(2, Directory.GetFiles(t.Sub("Visit/Phone 1")).Length);
        Assert.Equal(0, store.All().Count);
        Assert.Equal(1, plan.Folders.Count);   // still in the preview to try again
    }

    [Test]
    public static async Task NameAlreadyTakenIsRefusedBeforeAnyChange()
    {
        using var t = new TempDir();
        t.File("Footage/Visit/a.mp4");
        Directory.CreateDirectory(t.Sub("Footage/Taken"));
        var store = new Store(t.Sub("state.json"));
        var plan = Planner.Build(await new Scanner(Dates("a.mp4"), TimeSpan.Zero).ScanAsync(new[] { t.Sub("Footage/Visit") }), store);
        plan.Folders[0].Name = "Taken";
        var r = Planner.Queue(plan, plan.Folders.ToList(), store, new AppSettings(), true);
        Assert.False(r.Success);
        Assert.Contains("already exists", r.Error);
        Assert.True(File.Exists(t.Sub("Footage/Visit/a.mp4")));
    }

    [Test]
    public static async Task CaseOnlyRenameWorks()
    {
        using var t = new TempDir();
        t.File("Footage/visit/a.mp4");
        var store = new Store(t.Sub("state.json"));
        var plan = Planner.Build(await new Scanner(Dates("a.mp4"), TimeSpan.Zero).ScanAsync(new[] { t.Sub("Footage/visit") }), store);
        plan.Folders[0].Name = "Visit";
        var r = Planner.Queue(plan, plan.Folders.ToList(), store, new AppSettings(), true);
        Assert.True(r.Success, r.Error ?? "");
        Assert.True(Directory.GetDirectories(t.Sub("Footage")).Select(Path.GetFileName).Contains("Visit"));
    }

    [Test]
    public static async Task WithoutDiskRenameOnlyTitlesChange()
    {
        using var t = new TempDir();
        t.File("Visit/a.mp4");
        var store = new Store(t.Sub("state.json"));
        var plan = Planner.Build(await new Scanner(Dates("a.mp4"), TimeSpan.Zero).ScanAsync(new[] { t.Sub("Visit") }), store);
        plan.Folders[0].Name = "Collector Visit";
        var r = Planner.Queue(plan, plan.Folders.ToList(), store, new AppSettings(), false);
        Assert.True(r.Success);
        Assert.True(File.Exists(t.Sub("Visit/a.mp4")));
        Assert.Equal("Collector Visit 1 (26.10.26)", store.All()[0].Title);
    }

    [Test]
    public static async Task DuplicatesAndNumberingAcrossBatches()
    {
        using var t = new TempDir();
        t.File("S/a.mp4");
        t.File("S/b.mp4");
        var store = new Store(t.Sub("state.json"));
        var media = Dates("a.mp4", "b.mp4", "new.mp4", "copy.mp4");
        var plan = Planner.Build(await new Scanner(media, TimeSpan.Zero).ScanAsync(new[] { t.Sub("S") }), store);
        Planner.Queue(plan, plan.Folders.ToList(), store, new AppSettings(), false);
        foreach (var c in store.All()) store.Update(c.Id, r => r.State = ClipState.Uploaded);

        File.Copy(t.Sub("S/a.mp4"), t.Sub("S/copy.mp4"));
        t.File("S/new.mp4");
        var again = Planner.Build(await new Scanner(media, TimeSpan.Zero).ScanAsync(new[] { t.Sub("S") }), store);
        var clips = again.Stories.Single().Clips;
        Assert.True(clips.Single(c => c.Clip.FileName == "copy.mp4").DuplicateNote!.StartsWith("Already uploaded"));
        Assert.False(clips.Single(c => c.Clip.FileName == "a.mp4").Selected);
        Assert.Equal("S 3 (26.10.26)", clips.Single(c => c.Clip.FileName == "new.mp4").Title);
    }
}

public static class StoreTests
{
    [Test]
    public static void SurvivesReloadAndClaimsInterruptedFirst()
    {
        using var t = new TempDir();
        var path = t.Sub("state.json");
        var s = new Store(path);
        var a = s.Add(new ClipRecord { Title = "A", Fingerprint = "fa" });
        var b = s.Add(new ClipRecord { Title = "B", Fingerprint = "fb" });
        s.Update(b.Id, r => r.State = ClipState.Uploading);
        var s2 = new Store(path);
        Assert.Equal(2, s2.All().Count);
        Assert.Equal("B", s2.ClaimNext(new List<long>())!.Title);
        Assert.Equal("A", s2.ClaimNext(new List<long> { b.Id })!.Title);
        Assert.Equal(1, new Store(path).Get(a.Id)!.Attempts);
    }

    [Test]
    public static void CsvHasHeaderOnceBomAndEscaping()
    {
        using var t = new TempDir();
        var c = new ClipRecord { Story = "S", Seq = 1, Title = "S 1 (26.10.26)", SourcePath = @"C:\x.mp4", Size = 1048576, VideoId = "abc" };
        CsvLog.Append(t.Path, c, "Uploaded");
        CsvLog.Append(t.Path, c, "Failed", "boom, \"quoted\"");
        var bytes = File.ReadAllBytes(Path.Combine(t.Path, "upload_log.csv"));
        Assert.True(bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "BOM");
        var text = File.ReadAllText(Path.Combine(t.Path, "upload_log.csv"));
        Assert.Equal(1, text.Split("timestamp_ist").Length - 1);
        Assert.Contains("\"boom, \"\"quoted\"\"\"", text);
        Assert.Contains("https://www.youtube.com/watch?v=abc", text);
        Assert.Contains(",1.00,", text);
    }

    [Test]
    public static void PrivacyLockedUntilAudit()
    {
        var s = new AppSettings { Privacy = "unlisted" };
        Assert.Equal("private", s.EffectivePrivacy);
        s.AuditApproved = true;
        Assert.Equal("unlisted", s.EffectivePrivacy);
    }
}
