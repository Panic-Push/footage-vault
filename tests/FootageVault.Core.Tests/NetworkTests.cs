using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace FootageVault.Core.Tests;

/// <summary>
/// A tiny HTTP/1.1 server that behaves like YouTube's resumable upload endpoint. Raw sockets give full control,
/// e.g. to stop reading in the middle of a chunk the way a stalled connection does.
/// </summary>
public sealed class FakeYouTube : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    public string BaseUrl { get; }
    public long Received;                       // bytes stored for the session
    public long Total;
    public int Sessions;
    public int Puts;
    public int Queries;
    public string? LastMetadata;
    public List<string> AuthHeaders { get; } = new();
    /// <summary>Called with the PUT number (1-based); return an action to take for it.</summary>
    public Func<int, string>? Behaviour;        // "ok" | "stall" | "500" | "401" | "404" | "quota" | "channel"
    public Func<string>? StartBehaviour;        // for the session-start POST
    public string VideoId = "vid123";

    public FakeYouTube()
    {
        _listener.Start();
        BaseUrl = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
        _ = Task.Run(AcceptLoop);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch (Exception) { return; }
            _ = Task.Run(() => Handle(c));
        }
    }

    private static async Task<string?> ReadLine(Stream s)
    {
        var sb = new StringBuilder();
        var b = new byte[1];
        while (true)
        {
            var n = await s.ReadAsync(b);
            if (n == 0) return sb.Length == 0 ? null : sb.ToString();
            if (b[0] == '\n') return sb.ToString().TrimEnd('\r');
            sb.Append((char)b[0]);
        }
    }

    private async Task Handle(TcpClient client)
    {
        using var _ = client;
        var s = client.GetStream();
        try
        {
            var line = await ReadLine(s);
            if (line is null) return;
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string? h;
            while (!string.IsNullOrEmpty(h = await ReadLine(s)))
            {
                var i = h.IndexOf(':');
                headers[h[..i].Trim()] = h[(i + 1)..].Trim();
            }
            lock (AuthHeaders) AuthHeaders.Add(headers.GetValueOrDefault("Authorization", ""));
            var method = line.Split(' ')[0];
            var length = long.Parse(headers.GetValueOrDefault("Content-Length", "0"));

            if (method == "POST")
            {
                var body = await ReadBody(s, length);
                LastMetadata = Encoding.UTF8.GetString(body);
                Total = long.Parse(headers["X-Upload-Content-Length"]);
                var act = StartBehaviour?.Invoke() ?? "ok";
                if (act == "quota") { await Error(s, 403, "quotaExceeded"); return; }
                if (act == "channel") { await Error(s, 400, "uploadLimitExceeded"); return; }
                if (act == "401") { await Respond(s, 401, "", ""); return; }
                Interlocked.Increment(ref Sessions);
                Received = 0;
                await Respond(s, 200, "", $"Location: {BaseUrl}/session/{Sessions}\r\n");
                return;
            }

            // PUT: chunk or status query
            var range = headers.GetValueOrDefault("Content-Range", "");
            if (range.StartsWith("bytes */"))
            {
                Interlocked.Increment(ref Queries);
                var qact = Behaviour?.Invoke(0) ?? "ok";
                if (qact == "404") { await Respond(s, 404, "", ""); return; }
                if (Received >= Total && Total > 0) { await Done(s); return; }
                await Respond(s, 308, "", Received > 0 ? $"Range: bytes=0-{Received - 1}\r\n" : "");
                return;
            }
            var n = Interlocked.Increment(ref Puts);
            var act2 = Behaviour?.Invoke(n) ?? "ok";
            var start = long.Parse(range.Split(' ')[1].Split('-')[0]);
            if (act2 == "stall")
            {
                // Read a little, then stop reading: the client's writes back up and stall.
                var got = await ReadBody(s, Math.Min(256 * 1024, length));
                Received = Math.Max(Received, start + got.Length);
                await Task.Delay(Timeout.Infinite, _cts.Token).ContinueWith(_ => { });
                return;
            }
            var data = await ReadBody(s, length);
            if (act2 == "500") { await Error(s, 503, "backendError"); return; }
            if (act2 == "401") { await Respond(s, 401, "", ""); return; }
            if (act2 == "404") { await Respond(s, 404, "", ""); return; }
            if (start != Received) { await Respond(s, 308, "", Received > 0 ? $"Range: bytes=0-{Received - 1}\r\n" : ""); return; }
            Received += data.Length;
            if (Received >= Total) await Done(s);
            else await Respond(s, 308, "", $"Range: bytes=0-{Received - 1}\r\n");
        }
        catch (Exception) { }
    }

    private static async Task<byte[]> ReadBody(Stream s, long length)
    {
        var buf = new byte[length];
        var got = 0;
        while (got < length)
        {
            var n = await s.ReadAsync(buf.AsMemory(got));
            if (n == 0) break;
            got += n;
        }
        return got == length ? buf : buf[..got];
    }

    private Task Done(Stream s) => Respond(s, 200, JsonSerializer.Serialize(new { id = VideoId }), "Content-Type: application/json\r\n");

    private static Task Error(Stream s, int code, string reason) =>
        Respond(s, code, JsonSerializer.Serialize(new { error = new { code, message = reason, errors = new[] { new { reason } } } }),
            "Content-Type: application/json\r\n");

    private static async Task Respond(Stream s, int code, string body, string extraHeaders)
    {
        var b = Encoding.UTF8.GetBytes(body);
        var text = code switch { 200 => "OK", 308 => "Resume Incomplete", 404 => "Not Found", 401 => "Unauthorized", _ => "Error" };
        var head = $"HTTP/1.1 {code} {text}\r\n{extraHeaders}Content-Length: {b.Length}\r\nConnection: close\r\n\r\n";
        await s.WriteAsync(Encoding.ASCII.GetBytes(head));
        await s.WriteAsync(b);
        await s.FlushAsync();
    }
}

public static class UploaderTests
{
    private static ClipRecord Clip(TempDir t, int size)
    {
        var p = t.File("clip.mp4", size, 3);
        var fi = new FileInfo(p);
        return new ClipRecord
        {
            Id = 1, Title = "Collector Visit 1 (26.10.26)", Description = "d", SourcePath = p, Size = fi.Length,
            ModifiedTicks = fi.LastWriteTimeUtc.Ticks,
        };
    }

    private static YouTubeUploader Uploader(FakeYouTube yt, Func<bool, string>? token = null) =>
        new(YouTubeUploader.CreateHttpClient(), (ct, force) => Task.FromResult(token?.Invoke(force) ?? "tok"), yt.BaseUrl)
        {
            StallTimeout = TimeSpan.FromSeconds(1),
            ResponseTimeout = TimeSpan.FromSeconds(5),
            Delay = (_, _) => Task.CompletedTask,
        };

    [Test]
    public static async Task MultiChunkUploadWithSmoothProgress()
    {
        using var t = new TempDir();
        using var yt = new FakeYouTube();
        var clip = Clip(t, 20 * 1024 * 1024);      // 3 chunks
        var updates = new List<long>();
        string? saved = null;
        var r = await Uploader(yt).UploadAsync(clip, "private", (s, _) => { lock (updates) updates.Add(s); },
            (uri, _) => saved = uri, CancellationToken.None);
        Assert.Equal("vid123", r.VideoId);
        Assert.Equal(3, yt.Puts);
        Assert.Equal(clip.Size, yt.Received);
        Assert.True(updates.Count > 50, $"progress moves inside chunks ({updates.Count} updates)");
        Assert.True(updates.Any(u => u > 0 && u < YouTubeUploader.ChunkSize), "progress before the first chunk finished");
        Assert.Contains("\"privacyStatus\":\"private\"", yt.LastMetadata);
        Assert.Contains("\"categoryId\":\"25\"", yt.LastMetadata);
        Assert.Contains("\"selfDeclaredMadeForKids\":false", yt.LastMetadata);
        Assert.Contains("/session/1", saved);
        Assert.True(yt.AuthHeaders.All(h => h == "Bearer tok"));
    }

    [Test]
    public static async Task StalledConnectionIsDroppedAndUploadContinues()
    {
        // The bug seen on the office PC: one chunk froze for about a minute. Now: dropped after the stall timeout.
        using var t = new TempDir();
        using var yt = new FakeYouTube { Behaviour = n => n == 2 ? "stall" : "ok" };
        var clip = Clip(t, 28 * 1024 * 1024);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = await Uploader(yt).UploadAsync(clip, "private", (_, _) => { }, (_, _) => { }, CancellationToken.None);
        Assert.Equal("vid123", r.VideoId);
        Assert.Equal(clip.Size, yt.Received);
        Assert.True(yt.Queries >= 1, "asked YouTube where to continue");
        Assert.Equal(1, yt.Sessions);     // same upload, not restarted
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), $"recovered quickly ({sw.Elapsed.TotalSeconds:0.0}s)");
    }

    [Test]
    public static async Task ResumesSavedSessionAfterRestart()
    {
        using var t = new TempDir();
        using var yt = new FakeYouTube();
        var clip = Clip(t, 12 * 1024 * 1024);
        await Uploader(yt).UploadAsync(clip, "private", (_, _) => { }, (_, _) => { }, CancellationToken.None);
        // pretend the app closed after the first chunk: YouTube has 8 MB
        yt.Received = YouTubeUploader.ChunkSize;
        yt.Puts = 0;
        clip.ResumableUri = $"{yt.BaseUrl}/session/1";
        var r = await Uploader(yt).UploadAsync(clip, "private", (_, _) => { }, (_, _) => { }, CancellationToken.None);
        Assert.Equal("vid123", r.VideoId);
        Assert.False(r.Restarted);
        Assert.Equal(1, yt.Puts);         // only the rest was sent
        Assert.Equal(1, yt.Sessions);
    }

    [Test]
    public static async Task ExpiredSessionStartsAgain()
    {
        using var t = new TempDir();
        using var yt = new FakeYouTube { Behaviour = n => n == 0 ? "404" : "ok" };
        var clip = Clip(t, 1024 * 1024);
        clip.ResumableUri = $"{yt.BaseUrl}/session/old";
        var r = await Uploader(yt).UploadAsync(clip, "private", (_, _) => { }, (_, _) => { }, CancellationToken.None);
        Assert.True(r.Restarted);
        Assert.Equal(1, yt.Sessions);
    }

    [Test]
    public static async Task ServerErrorsAreRetried()
    {
        using var t = new TempDir();
        using var yt = new FakeYouTube { Behaviour = n => n is 1 or 2 ? "500" : "ok" };
        var r = await Uploader(yt).UploadAsync(Clip(t, 1024 * 1024), "private", (_, _) => { }, (_, _) => { }, CancellationToken.None);
        Assert.Equal("vid123", r.VideoId);
    }

    [Test]
    public static async Task QuotaAndChannelLimitsAreTold()
    {
        using var t = new TempDir();
        using var yt = new FakeYouTube { StartBehaviour = () => "quota" };
        await Assert.ThrowsAsync<QuotaExceededException>(() =>
            Uploader(yt).UploadAsync(Clip(t, 1000), "private", (_, _) => { }, (_, _) => { }, CancellationToken.None));
        yt.StartBehaviour = () => "channel";
        await Assert.ThrowsAsync<ChannelLimitException>(() =>
            Uploader(yt).UploadAsync(Clip(t, 1000), "private", (_, _) => { }, (_, _) => { }, CancellationToken.None));
    }

    [Test]
    public static async Task ExpiredTokenIsRefreshedOnce()
    {
        using var t = new TempDir();
        var first = true;
        using var yt = new FakeYouTube { StartBehaviour = () => { if (first) { first = false; return "401"; } return "ok"; } };
        var forced = 0;
        var r = await Uploader(yt, force => { if (force) forced++; return forced > 0 ? "new" : "old"; })
            .UploadAsync(Clip(t, 1000), "private", (_, _) => { }, (_, _) => { }, CancellationToken.None);
        Assert.Equal("vid123", r.VideoId);
        Assert.Equal(1, forced);
        Assert.Equal("Bearer new", yt.AuthHeaders[^1]);
    }

    [Test]
    public static async Task ChangedOrMissingSourceIsRefused()
    {
        using var t = new TempDir();
        using var yt = new FakeYouTube();
        var clip = Clip(t, 1000);
        File.AppendAllText(clip.SourcePath, "x");
        await Assert.ThrowsAsync<FileProblemException>(() =>
            Uploader(yt).UploadAsync(clip, "private", (_, _) => { }, (_, _) => { }, CancellationToken.None));
        File.Delete(clip.SourcePath);
        await Assert.ThrowsAsync<FileProblemException>(() =>
            Uploader(yt).UploadAsync(clip, "private", (_, _) => { }, (_, _) => { }, CancellationToken.None));
        Assert.Equal(0, yt.Sessions);
    }
}

public static class AuthTests
{
    [Test]
    public static async Task BrowserSignInWithPkceAndRefresh()
    {
        using var tokenServer = new HttpListenerHost();
        var bodies = new List<string>();
        tokenServer.Handler = body =>
        {
            bodies.Add(body);
            return body.Contains("grant_type=authorization_code")
                ? "{\"access_token\":\"a1\",\"expires_in\":3600,\"refresh_token\":\"r1\"}"
                : "{\"access_token\":\"a2\",\"expires_in\":3600}";
        };
        var secret = ClientSecret.Parse("{\"installed\":{\"client_id\":\"cid\",\"client_secret\":\"cs\",\"token_uri\":\"" + tokenServer.Url + "\"}}");
        var store = new MemoryTokenStore();
        var auth = new GoogleAuth(secret, store, new HttpClient());
        using var http = new HttpClient();
        await auth.SignInAsync(url =>
        {
            // Act as the browser: Google redirects back to the app with a code and the same state.
            var q = GoogleAuth.ParseQuery(new Uri(url).Query.TrimStart('?'));
            Assert.Equal("S256", q["code_challenge_method"]);
            Assert.Contains("youtube.upload", q["scope"]);
            _ = Task.Run(async () =>
            {
                await http.GetAsync($"{q["redirect_uri"]}favicon.ico");      // stray request is ignored
                var page = await http.GetStringAsync($"{q["redirect_uri"]}?state={q["state"]}&code=abc");
                Assert.Contains("signed in", page);
            });
        }, CancellationToken.None);
        Assert.Equal("r1", store.Load());
        Assert.Contains("code_verifier=", bodies[0]);
        Assert.Equal("a1", await auth.GetAccessTokenAsync(CancellationToken.None));
        Assert.Equal("a2", await auth.GetAccessTokenAsync(CancellationToken.None, forceRefresh: true));
    }

    [Test]
    public static async Task WrongStateIsRejected()
    {
        var secret = ClientSecret.Parse("{\"installed\":{\"client_id\":\"cid\",\"client_secret\":\"cs\"}}");
        var auth = new GoogleAuth(secret, new MemoryTokenStore(), new HttpClient());
        using var http = new HttpClient();
        await Assert.ThrowsAsync<AuthException>(() => auth.SignInAsync(url =>
        {
            var q = GoogleAuth.ParseQuery(new Uri(url).Query.TrimStart('?'));
            _ = http.GetStringAsync($"{q["redirect_uri"]}?state=forged&code=abc");
        }, CancellationToken.None));
    }

    [Test]
    public static void ClientFileWithoutIdIsExplained()
    {
        Assert.Throws<FormatException>(() => ClientSecret.Parse("{\"installed\":{}}"));
    }
}

/// <summary>Minimal token endpoint.</summary>
public sealed class HttpListenerHost : IDisposable
{
    private readonly TcpListener _l = new(IPAddress.Loopback, 0);
    public string Url { get; }
    public Func<string, string> Handler { get; set; } = _ => "{}";

    public HttpListenerHost()
    {
        _l.Start();
        Url = $"http://127.0.0.1:{((IPEndPoint)_l.LocalEndpoint).Port}/token";
        _ = Task.Run(async () =>
        {
            while (true)
            {
                TcpClient c;
                try { c = await _l.AcceptTcpClientAsync(); } catch { return; }
                _ = Task.Run(async () =>
                {
                    using var _ = c;
                    var s = c.GetStream();
                    var r = new StreamReader(s);
                    await r.ReadLineAsync();
                    var len = 0;
                    string? h;
                    while (!string.IsNullOrEmpty(h = await r.ReadLineAsync()))
                        if (h.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) len = int.Parse(h[15..].Trim());
                    var buf = new char[len];
                    var got = 0;
                    while (got < len) got += await r.ReadAsync(buf, got, len - got);
                    var body = Encoding.UTF8.GetBytes(Handler(new string(buf)));
                    var head = $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
                    await s.WriteAsync(Encoding.ASCII.GetBytes(head));
                    await s.WriteAsync(body);
                });
            }
        });
    }

    public void Dispose() => _l.Stop();
}

public sealed class FakeUploader : IUploader
{
    public Func<ClipRecord, Exception?> Outcome { get; set; } = _ => null;
    public TimeSpan Duration { get; set; } = TimeSpan.FromMilliseconds(50);
    public int Calls;

    public async Task<UploadResult> UploadAsync(ClipRecord clip, string privacy, Action<long, long> progress,
        Action<string?, long> saveSession, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        progress(clip.Size / 2, clip.Size);
        await Task.Delay(Duration, ct);
        if (Outcome(clip) is { } ex) throw ex;
        return new UploadResult("v" + clip.Id, false);
    }
}

public static class EngineTests
{
    private static (Store, QueueEngine, FakeClock, TempDir, AppSettings) Setup(int clips, int limit = 100, int parallel = 2)
    {
        var t = new TempDir();
        var store = new Store(t.Sub("state.json"));
        var settings = new AppSettings { DailyLimit = limit, ParallelUploads = parallel, LogFolder = t.Sub("logs") };
        for (var i = 0; i < clips; i++)
        {
            var p = t.File($"c{i}.mp4", 100);
            var fi = new FileInfo(p);
            store.Add(new ClipRecord { Title = $"S {i + 1}", Story = "S", Seq = i + 1, SourcePath = p, Size = fi.Length,
                ModifiedTicks = fi.LastWriteTimeUtc.Ticks, Fingerprint = "f" + i, RecDate = "2026-10-26" });
        }
        var clock = new FakeClock();
        var engine = new QueueEngine(store, () => settings, clock) { IdlePoll = TimeSpan.FromMilliseconds(20) };
        return (store, engine, clock, t, settings);
    }

    [Test]
    public static async Task UploadsEverythingAndLogs()
    {
        var (store, engine, _, t, _) = Setup(4);
        using var _t = t;
        using var _e = engine;
        engine.SetUploader(new FakeUploader());
        engine.Start();
        await Assert.Eventually(() => store.Counts()[ClipState.Uploaded] == 4, "all uploaded");
        Assert.Equal(4, engine.UploadsToday());
        Assert.Equal(5, File.ReadAllLines(Path.Combine(t.Sub("logs"), "upload_log.csv")).Length);
    }

    [Test]
    public static async Task DailyLimitPausesThenResumesByItself()
    {
        var (store, engine, clock, t, _) = Setup(3, limit: 2);
        using var _t = t;
        using var _e = engine;
        engine.SetUploader(new FakeUploader());
        engine.Start();
        await Assert.Eventually(() => engine.Reason == PauseReason.Quota, "paused at limit");
        Assert.Equal(2, store.Counts()[ClipState.Uploaded]);
        Assert.Contains("resume automatically", engine.BannerText());
        clock.UtcNow = engine.ResumeAt!.Value.AddSeconds(1);      // the Pacific-midnight reset passes
        await Assert.Eventually(() => store.Counts()[ClipState.Uploaded] == 3, "resumed after reset");
        Assert.Equal(PauseReason.None, engine.Reason);
    }

    [Test]
    public static async Task QuotaErrorFromYouTubeDoesNotLoop()
    {
        var (store, engine, _, t, _) = Setup(1);
        using var _t = t;
        using var _e = engine;
        var up = new FakeUploader { Outcome = _ => new QuotaExceededException("quota") };
        engine.SetUploader(up);
        engine.Start();
        await Assert.Eventually(() => engine.Reason == PauseReason.Quota, "paused");
        await Task.Delay(300);
        Assert.Equal(1, up.Calls);
        Assert.Equal(ClipState.Queued, store.All()[0].State);
    }

    [Test]
    public static async Task SignInAndChannelProblemsPauseWithTheirOwnMessage()
    {
        var (_, engine, _, t, _) = Setup(1);
        using var _t = t;
        using var _e = engine;
        engine.SetUploader(new FakeUploader { Outcome = _ => new AuthExpiredException("x") });
        engine.Start();
        await Assert.Eventually(() => engine.Reason == PauseReason.SignIn, "sign-in pause");
        Assert.False(engine.SignedIn);
        Assert.Contains("sign in again", engine.BannerText());
        engine.SetUploader(new FakeUploader { Outcome = _ => new ChannelLimitException("x") });
        await Assert.Eventually(() => engine.Reason == PauseReason.ChannelLimit, "channel pause");
        Assert.Contains("channel", engine.BannerText());
    }

    [Test]
    public static async Task FailedClipDoesNotStopTheRest()
    {
        var (store, engine, _, t, _) = Setup(3, parallel: 1);
        using var _t = t;
        using var _e = engine;
        engine.SetUploader(new FakeUploader { Outcome = c => c.Seq == 1 ? new UploadFailedException("rejected") : null });
        engine.Start();
        await Assert.Eventually(() => store.Counts()[ClipState.Uploaded] == 2, "others uploaded");
        Assert.Equal("rejected", store.All().Single(c => c.Seq == 1).Error);
        Assert.Equal(1, store.RetryFailed());
    }

    [Test]
    public static async Task PauseStopsRunningUploadsAndResumeContinues()
    {
        var (store, engine, _, t, _) = Setup(2, parallel: 1);
        using var _t = t;
        using var _e = engine;
        engine.SetUploader(new FakeUploader { Duration = TimeSpan.FromSeconds(30) });
        engine.Start();
        await Assert.Eventually(() => engine.ActiveIds().Count == 1, "one uploading");
        engine.PauseAll();
        await Assert.Eventually(() => engine.ActiveIds().Count == 0, "stopped at once", 3000);
        Assert.Equal(ClipState.Uploading, store.All()[0].State);   // keeps its place
        engine.SetUploader(new FakeUploader());
        engine.Resume();
        await Assert.Eventually(() => store.Counts()[ClipState.Uploaded] == 2, "finished after resume");
    }

    [Test]
    public static async Task NothingUploadsUntilSignedIn()
    {
        var (store, engine, _, t, _) = Setup(1);
        using var _t = t;
        using var _e = engine;
        engine.Start();
        await Task.Delay(200);
        Assert.Equal(1, store.Counts()[ClipState.Queued]);
        Assert.Contains("Sign in", engine.BannerText());
    }
}
