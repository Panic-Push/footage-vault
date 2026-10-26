namespace FootageVault.Core;

public enum PauseReason { None, Quota, ChannelLimit, SignIn }

public sealed record ProgressInfo(long Sent, long Total, double BytesPerSecond);

/// <summary>
/// Runs the upload queue in the background: up to 4 uploads at once, pauses at the daily limit and resumes
/// by itself after the Pacific-midnight reset, pauses on sign-in problems, resumes interrupted uploads first.
/// All events fire on background threads.
/// </summary>
public sealed class QueueEngine : IDisposable
{
    public const int MaxWorkers = 4;

    private readonly Store _store;
    private readonly Func<AppSettings> _settings;
    private readonly IClock _clock;
    private readonly object _lock = new();
    private readonly HashSet<long> _active = new();
    private readonly Dictionary<long, ProgressInfo> _progress = new();
    private readonly CancellationTokenSource _shutdown = new();
    private CancellationTokenSource _pauseCts = new();
    private readonly List<Task> _workers = new();
    private IUploader? _uploader;

    public event Action? Changed;
    public event Action<long, ProgressInfo>? Progress;
    public event Action<string>? Notice;

    public bool UserPaused { get; private set; }
    public PauseReason Reason { get; private set; }
    public DateTimeOffset? ResumeAt { get; private set; }
    public string PauseText { get; private set; } = "";
    public bool SignedIn => _uploader is not null;
    public TimeSpan IdlePoll { get; init; } = TimeSpan.FromMilliseconds(500);

    public QueueEngine(Store store, Func<AppSettings> settings, IClock? clock = null)
    {
        _store = store;
        _settings = settings;
        _clock = clock ?? SystemClock.Instance;
    }

    public void Start()
    {
        for (var i = 0; i < MaxWorkers; i++)
        {
            var index = i;
            _workers.Add(Task.Run(() => WorkerLoop(index)));
        }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        lock (_lock) _pauseCts.Cancel();
        try { Task.WaitAll(_workers.ToArray(), TimeSpan.FromSeconds(3)); }
        catch (AggregateException) { }
    }

    public void SetUploader(IUploader? uploader)
    {
        lock (_lock)
        {
            _uploader = uploader;
            if (uploader is not null && Reason == PauseReason.SignIn) ClearPause();
        }
        Changed?.Invoke();
    }

    public void PauseAll()
    {
        lock (_lock)
        {
            UserPaused = true;
            _pauseCts.Cancel();             // running uploads stop at once and keep their place
        }
        Changed?.Invoke();
    }

    public void Resume()
    {
        lock (_lock)
        {
            UserPaused = false;
            if (Reason is PauseReason.Quota or PauseReason.ChannelLimit) ClearPause();
            if (_pauseCts.IsCancellationRequested) _pauseCts = new CancellationTokenSource();
        }
        Changed?.Invoke();
    }

    public IReadOnlyCollection<long> ActiveIds()
    {
        lock (_lock) return _active.ToArray();
    }

    public ProgressInfo? ProgressOf(long id)
    {
        lock (_lock) return _progress.TryGetValue(id, out var p) ? p : null;
    }

    public int Remove(IEnumerable<long> ids)
    {
        var n = 0;
        lock (_lock)
        {
            foreach (var id in ids)
            {
                var r = _store.Get(id);
                if (r is null || _active.Contains(id) || r.State == ClipState.Uploaded) continue;
                if (_store.Delete(id)) n++;
            }
        }
        Changed?.Invoke();
        return n;
    }

    public int UploadsToday() => _store.UploadsOn(Quota.Day(_clock.UtcNow));

    public string BannerText()
    {
        if (UserPaused) return "Uploads are paused. Select Resume to continue.";
        if (!SignedIn && Reason != PauseReason.SignIn) return "Sign in with Google to start uploading.";
        return Reason == PauseReason.None ? "" : PauseText;
    }

    private void ClearPause()
    {
        Reason = PauseReason.None;
        ResumeAt = null;
        PauseText = "";
    }

    private void SetPause(PauseReason reason, string text, DateTimeOffset? until = null)
    {
        lock (_lock)
        {
            Reason = reason;
            PauseText = text;
            ResumeAt = until;
        }
        Notice?.Invoke(text);
        Changed?.Invoke();
    }

    private bool CanRun()
    {
        lock (_lock)
        {
            if (UserPaused || _uploader is null) return false;
            if (Reason != PauseReason.None && ResumeAt is { } at && _clock.UtcNow >= at)
            {
                ClearPause();
                Task.Run(() => Changed?.Invoke());
            }
            return Reason == PauseReason.None;
        }
    }

    private ClipRecord? Claim()
    {
        lock (_lock)
        {
            var now = _clock.UtcNow;
            var limit = Math.Max(1, _settings().DailyLimit);
            if (_store.UploadsOn(Quota.Day(now)) + _active.Count >= limit)
            {
                if (_active.Count == 0 && Reason == PauseReason.None && _store.HasWaiting(_active))
                {
                    var at = Quota.ResumeTime(now);
                    Reason = PauseReason.Quota;
                    ResumeAt = at;
                    PauseText = $"Daily upload limit reached. Uploads resume automatically at {TimeZones.FormatIst(at)}.";
                    var text = PauseText;
                    Task.Run(() => { Notice?.Invoke(text); Changed?.Invoke(); });
                }
                return null;
            }
            var r = _store.ClaimNext(_active);
            if (r is not null) _active.Add(r.Id);
            return r;
        }
    }

    private async Task WorkerLoop(int index)
    {
        var ct = _shutdown.Token;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (index >= Math.Clamp(_settings().ParallelUploads, 1, MaxWorkers) || !CanRun())
                {
                    await Task.Delay(IdlePoll, ct);
                    continue;
                }
                var clip = Claim();
                if (clip is null)
                {
                    await Task.Delay(IdlePoll * 2, ct);
                    continue;
                }
                Changed?.Invoke();
                try { await RunClip(clip); }
                finally
                {
                    lock (_lock)
                    {
                        _active.Remove(clip.Id);
                        _progress.Remove(clip.Id);
                    }
                    Changed?.Invoke();
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                Log.Error("Unexpected error in upload worker", ex);
                try { await Task.Delay(2000, ct); } catch (OperationCanceledException) { return; }
            }
        }
    }

    private async Task RunClip(ClipRecord clip)
    {
        IUploader uploader;
        CancellationToken stop;
        lock (_lock)
        {
            uploader = _uploader!;
            stop = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token, _pauseCts.Token).Token;
        }
        var settings = _settings();
        var privacy = settings.EffectivePrivacy;
        var logFolder = LogFolder(settings);
        _store.Update(clip.Id, r => r.Privacy = privacy);

        var lastTime = DateTime.UtcNow;
        var lastBytes = clip.BytesSent;
        double speed = 0;
        void OnProgress(long sent, long total)
        {
            var now = DateTime.UtcNow;
            var dt = (now - lastTime).TotalSeconds;
            if (dt >= 1)
            {
                var inst = Math.Max(0, (sent - lastBytes) / dt);
                speed = speed == 0 ? inst : 0.6 * speed + 0.4 * inst;
                lastTime = now;
                lastBytes = sent;
            }
            var info = new ProgressInfo(sent, total, speed);
            lock (_lock) _progress[clip.Id] = info;
            Progress?.Invoke(clip.Id, info);
        }

        void OnSession(string? uri, long sent) => _store.Update(clip.Id, r =>
        {
            r.ResumableUri = uri;
            r.BytesSent = sent;
        });

        UploadResult result;
        try
        {
            result = await uploader.UploadAsync(clip, privacy, OnProgress, OnSession, stop);
        }
        catch (OperationCanceledException)
        {
            return;     // paused or closing: stays "Uploading" with its saved session and resumes later
        }
        catch (QuotaExceededException ex)
        {
            Requeue(clip);
            var at = Quota.ResumeTime(_clock.UtcNow);
            Log.Warn($"quotaExceeded: {ex.Message}");
            SetPause(PauseReason.Quota, $"YouTube says today's upload quota is used up. Uploads resume automatically at {TimeZones.FormatIst(at)}.", at);
            return;
        }
        catch (ChannelLimitException ex)
        {
            Requeue(clip);
            Log.Warn($"uploadLimitExceeded: {ex.Message}");
            SetPause(PauseReason.ChannelLimit, "This YouTube channel has reached its own daily upload limit. A higher API quota " +
                     "won't fix this. Wait (usually up to 24 hours), then select Resume.");
            return;
        }
        catch (AuthExpiredException ex)
        {
            Requeue(clip);
            Log.Warn($"Sign-in problem: {ex.Message}");
            lock (_lock) _uploader = null;
            SetPause(PauseReason.SignIn, "Your Google sign-in expired or was removed. Sign in again on the Home page.");
            return;
        }
        catch (UploadException ex)
        {
            Fail(clip.Id, ex.Message, logFolder);
            return;
        }
        catch (Exception ex)
        {
            Log.Error("Unexpected upload error", ex);
            Fail(clip.Id, $"Unexpected error: {ex.Message}", logFolder);
            return;
        }

        var note = result.Restarted ? "The upload session had expired, so the file was sent again from the start" : null;
        _store.AddUpload(Quota.Day(_clock.UtcNow));
        _store.Update(clip.Id, r =>
        {
            r.State = ClipState.Uploaded;
            r.VideoId = result.VideoId;
            r.Error = note;
            r.ResumableUri = null;
            r.BytesSent = r.Size;
            r.Restarted = result.Restarted;
            r.FinishedAt = _clock.UtcNow;
        });
        var done = _store.Get(clip.Id)!;
        CsvLog.Append(logFolder, done, "Uploaded", note ?? "");
        Log.Info($"Uploaded {done.Title} as {done.VideoId}");
    }

    private void Requeue(ClipRecord clip) =>
        _store.Update(clip.Id, r =>
        {
            r.State = ClipState.Queued;
            r.Attempts = Math.Max(0, r.Attempts - 1);
        });

    private void Fail(long id, string message, string logFolder)
    {
        _store.Update(id, r =>
        {
            r.State = ClipState.Failed;
            r.Error = message;
            r.FinishedAt = _clock.UtcNow;
        });
        var rec = _store.Get(id);
        if (rec is not null) CsvLog.Append(logFolder, rec, "Failed", message);
        Log.Warn($"Upload failed for {rec?.Title}: {message}");
    }

    public static string DefaultLogFolder() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "FootageVault");

    public static string LogFolder(AppSettings s) => string.IsNullOrWhiteSpace(s.LogFolder) ? DefaultLogFolder() : s.LogFolder;
}
