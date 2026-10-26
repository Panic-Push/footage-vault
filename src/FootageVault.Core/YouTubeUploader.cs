using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace FootageVault.Core;

public abstract class UploadException : Exception
{
    protected UploadException(string message) : base(message) { }
}

public sealed class QuotaExceededException : UploadException { public QuotaExceededException(string m) : base(m) { } }
public sealed class ChannelLimitException : UploadException { public ChannelLimitException(string m) : base(m) { } }
public sealed class AuthExpiredException : UploadException { public AuthExpiredException(string m) : base(m) { } }
public sealed class FileProblemException : UploadException { public FileProblemException(string m) : base(m) { } }
public sealed class UploadFailedException : UploadException { public UploadFailedException(string m) : base(m) { } }
public sealed class PausedException : UploadException { public PausedException() : base("Paused") { } }

public sealed record UploadResult(string VideoId, bool Restarted);

public interface IUploader
{
    /// <param name="progress">(bytes confirmed or in flight, total)</param>
    /// <param name="saveSession">stores the resumable session address after each chunk</param>
    Task<UploadResult> UploadAsync(ClipRecord clip, string privacy, Action<long, long> progress,
        Action<string?, long> saveSession, CancellationToken ct);
}

/// <summary>
/// YouTube resumable upload over plain HTTPS (videos.insert, uploadType=resumable).
/// Sends 8 MB chunks; reports progress continuously inside each chunk; a connection that moves no data for
/// <see cref="StallTimeout"/> is dropped and the upload carries on from what YouTube has confirmed.
/// </summary>
public sealed class YouTubeUploader : IUploader
{
    public const int ChunkSize = 8 * 1024 * 1024;          // multiple of 256 KB, as YouTube requires
    public const string CategoryNews = "25";

    private readonly HttpClient _http;
    private readonly Func<CancellationToken, bool, Task<string>> _token;
    private readonly string _baseUrl;

    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan ResponseTimeout { get; init; } = TimeSpan.FromSeconds(90);
    public TimeSpan MaxNetworkWait { get; init; } = TimeSpan.FromMinutes(30);
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    /// <param name="token">returns an access token; bool = force refresh</param>
    public YouTubeUploader(HttpClient http, Func<CancellationToken, bool, Task<string>> token,
        string baseUrl = "https://www.googleapis.com")
    {
        _http = http;
        _token = token;
        _baseUrl = baseUrl.TrimEnd('/');
    }

    /// <summary>An HttpClient set up for uploads: never follows redirects (YouTube's 308 means "send more").</summary>
    public static HttpClient CreateHttpClient() =>
        new(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            ConnectTimeout = TimeSpan.FromSeconds(20),
        })
        { Timeout = Timeout.InfiniteTimeSpan };

    public static void VerifyUnchanged(ClipRecord clip)
    {
        FileInfo fi;
        try { fi = new FileInfo(clip.SourcePath); }
        catch (Exception ex) { throw new FileProblemException($"Cannot read the source file: {ex.Message}"); }
        if (!fi.Exists) throw new FileProblemException("The source file is missing (moved, renamed or deleted since it was queued).");
        if (fi.Length != clip.Size || fi.LastWriteTimeUtc.Ticks != clip.ModifiedTicks)
            throw new FileProblemException("The source file changed since it was queued, so it was not uploaded.");
    }

    public async Task<UploadResult> UploadAsync(ClipRecord clip, string privacy, Action<long, long> progress,
        Action<string?, long> saveSession, CancellationToken ct)
    {
        VerifyUnchanged(clip);
        var total = clip.Size;
        var restarted = false;
        string? session = clip.ResumableUri;
        var needQuery = session is not null;      // resuming: ask YouTube how much it already has
        long offset = 0;
        var networkWaited = TimeSpan.Zero;
        var delay = TimeSpan.FromSeconds(2);
        var serverErrors = 0;
        var tokenRetries = 0;
        progress(0, total);

        await using var file = OpenSource(clip.SourcePath);

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (session is null)
                {
                    session = await StartSessionAsync(clip, privacy, total, ct);
                    offset = 0;
                    needQuery = false;
                    saveSession(session, 0);
                }
                else if (needQuery)
                {
                    var status = await QueryAsync(session, total, ct);
                    needQuery = false;
                    if (status.VideoId is not null) return new UploadResult(status.VideoId, restarted);
                    if (status.Expired)
                    {
                        Log.Info($"Upload session expired for {clip.SourcePath}; starting this file again");
                        session = null;
                        restarted = true;
                        saveSession(null, 0);
                        progress(0, total);
                        continue;
                    }
                    offset = status.Received;
                    progress(offset, total);
                }

                var length = (int)Math.Min(ChunkSize, total - offset);
                var result = await SendChunkAsync(session, file, offset, length, total, progress, ct);
                networkWaited = TimeSpan.Zero;
                delay = TimeSpan.FromSeconds(2);
                serverErrors = 0;
                tokenRetries = 0;
                if (result.VideoId is not null)
                {
                    progress(total, total);
                    return new UploadResult(result.VideoId, restarted);
                }
                if (result.Expired)
                {
                    session = null;
                    restarted = true;
                    saveSession(null, 0);
                    progress(0, total);
                    continue;
                }
                offset = result.Received;
                saveSession(session, offset);
                progress(offset, total);
            }
            catch (RetryableException ex)
            {
                if (ex.ServerError && ++serverErrors > 5)
                    throw new UploadFailedException($"YouTube kept returning server errors: {ex.Message}");
                if (networkWaited >= MaxNetworkWait)
                    throw new UploadFailedException($"No working connection for {MaxNetworkWait.TotalMinutes:0} minutes ({ex.Message}).");
                Log.Warn($"{ex.Message}; retrying {Path.GetFileName(clip.SourcePath)} in {delay.TotalSeconds:0}s");
                await Delay(delay, ct);
                networkWaited += delay;
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 60));
                needQuery = session is not null;
            }
            catch (TokenRejectedException)
            {
                if (++tokenRetries > 1) throw new AuthExpiredException("Google rejected the sign-in. Please sign in again.");
                try { await _token(ct, true); }
                catch (AuthException a) { throw new AuthExpiredException(a.Message); }
                needQuery = session is not null;
            }
            catch (AuthException a)
            {
                throw new AuthExpiredException(a.Message);
            }
        }
    }

    private static FileStream OpenSource(string path)
    {
        try { return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, useAsync: true); }
        catch (FileNotFoundException) { throw new FileProblemException("The source file is missing."); }
        catch (DirectoryNotFoundException) { throw new FileProblemException("The source folder is missing."); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new FileProblemException($"Cannot read the source file: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ HTTP steps
    private sealed record ChunkStatus(long Received, string? VideoId, bool Expired);

    private sealed class RetryableException : Exception
    {
        public bool ServerError { get; }
        public RetryableException(string m, bool serverError = false) : base(m) => ServerError = serverError;
    }

    private sealed class TokenRejectedException : Exception { }

    private async Task<string> StartSessionAsync(ClipRecord clip, string privacy, long total, CancellationToken ct)
    {
        var meta = new
        {
            snippet = new { title = clip.Title, description = clip.Description, categoryId = CategoryNews },
            status = new { privacyStatus = privacy, selfDeclaredMadeForKids = false },
        };
        var url = $"{_baseUrl}/upload/youtube/v3/videos?uploadType=resumable&part=snippet,status";
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(meta), Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("X-Upload-Content-Length", total.ToString());
        req.Headers.Add("X-Upload-Content-Type", MimeType(clip.SourcePath));
        using var resp = await SendAsync(req, ct);
        if (resp.StatusCode == HttpStatusCode.OK && resp.Headers.Location is { } loc) return loc.ToString();
        throw await Classify(resp, ct);
    }

    private async Task<ChunkStatus> QueryAsync(string session, long total, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Put, session) { Content = new ByteArrayContent(Array.Empty<byte>()) };
        req.Content.Headers.TryAddWithoutValidation("Content-Range", $"bytes */{total}");
        using var resp = await SendAsync(req, ct);
        return await Interpret(resp, ct);
    }

    private async Task<ChunkStatus> SendChunkAsync(string session, FileStream file, long offset, int length, long total,
        Action<long, long> progress, CancellationToken ct)
    {
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var content = new ChunkContent(file, offset, length, sent => progress(offset + sent, total), watchdog, StallTimeout);
        using var req = new HttpRequestMessage(HttpMethod.Put, session) { Content = content };
        req.Content.Headers.TryAddWithoutValidation("Content-Range",
            length == 0 ? $"bytes */{total}" : $"bytes {offset}-{offset + length - 1}/{total}");
        try
        {
            using var resp = await SendAsync(req, watchdog.Token, content);
            return await Interpret(resp, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new RetryableException(content.Stalled
                ? $"Connection stalled (no data moved for {StallTimeout.TotalSeconds:0}s)"
                : "No reply from YouTube");
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct, ChunkContent? chunk = null)
    {
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _token(ct, false));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (chunk is null) timeout.CancelAfter(ResponseTimeout);
        else chunk.OnBodySent = () => timeout.CancelAfter(ResponseTimeout);   // after the body: wait for the reply
        try
        {
            var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (resp.StatusCode == HttpStatusCode.Unauthorized)
            {
                resp.Dispose();
                throw new TokenRejectedException();
            }
            return resp;
        }
        catch (HttpRequestException ex)
        {
            if (chunk?.ReadError is { } readError)
                throw new FileProblemException($"Cannot read the source file: {readError.Message}");
            throw new RetryableException($"Network problem ({ex.Message})");
        }
        catch (IOException ex)
        {
            throw new RetryableException($"Network problem ({ex.Message})");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new RetryableException("No reply from YouTube");
        }
    }

    private async Task<ChunkStatus> Interpret(HttpResponseMessage resp, CancellationToken ct)
    {
        var code = (int)resp.StatusCode;
        if (code is 200 or 201)
        {
            var text = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(text);
            var id = doc.RootElement.TryGetProperty("id", out var v) ? v.GetString() : null;
            if (string.IsNullOrEmpty(id)) throw new UploadFailedException($"YouTube did not return a video id: {Short(text)}");
            return new ChunkStatus(0, id, false);
        }
        if (code == 308)
        {
            long received = 0;
            if (resp.Headers.TryGetValues("Range", out var ranges))
            {
                var r = ranges.First();                       // "bytes=0-12345"
                var dash = r.LastIndexOf('-');
                if (dash > 0 && long.TryParse(r[(dash + 1)..], out var last)) received = last + 1;
            }
            return new ChunkStatus(received, null, false);
        }
        if (code is 404 or 410) return new ChunkStatus(0, null, true);
        throw await Classify(resp, ct);
    }

    private static async Task<Exception> Classify(HttpResponseMessage resp, CancellationToken ct)
    {
        var code = (int)resp.StatusCode;
        var text = await resp.Content.ReadAsStringAsync(ct);
        var (reason, message) = ParseError(text);
        if (reason is "quotaExceeded" or "dailyLimitExceeded") return new QuotaExceededException(message);
        if (reason == "uploadLimitExceeded") return new ChannelLimitException(message);
        if (code == 401 || reason is "authError" or "invalidCredentials" or "insufficientPermissions")
            return new AuthExpiredException(message.Length > 0 ? message : "Google rejected the sign-in.");
        if (code >= 500 || code == 429 || reason == "rateLimitExceeded")
            return new RetryableException($"YouTube error {code}", serverError: true);
        return new UploadFailedException($"YouTube error {code} {reason}: {message}".TrimEnd(' ', ':'));
    }

    public static (string Reason, string Message) ParseError(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var err = doc.RootElement.GetProperty("error");
            var message = err.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
            var reason = "";
            if (err.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0
                && errors[0].TryGetProperty("reason", out var r))
                reason = r.GetString() ?? "";
            return (reason, message);
        }
        catch (Exception) { return ("", Short(text)); }
    }

    private static string Short(string s) => s.Length > 300 ? s[..300] : s;

    public static string MimeType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp4" or ".m4v" => "video/mp4",
        ".mov" => "video/quicktime",
        ".mts" or ".m2ts" => "video/mp2t",
        ".avi" => "video/x-msvideo",
        ".mkv" => "video/x-matroska",
        ".3gp" => "video/3gpp",
        ".wmv" => "video/x-ms-wmv",
        ".mxf" => "application/mxf",
        _ => "application/octet-stream",
    };

    /// <summary>Request body for one chunk that reports progress and trips a watchdog when the connection stalls.</summary>
    private sealed class ChunkContent : HttpContent
    {
        private const int Slice = 64 * 1024;
        private readonly FileStream _file;
        private readonly long _offset;
        private readonly int _length;
        private readonly Action<long> _progress;
        private readonly CancellationTokenSource _watchdog;
        private readonly TimeSpan _stall;

        public bool Stalled { get; private set; }
        public Exception? ReadError { get; private set; }
        public Action? OnBodySent { get; set; }

        public ChunkContent(FileStream file, long offset, int length, Action<long> progress, CancellationTokenSource watchdog, TimeSpan stall)
        {
            _file = file;
            _offset = offset;
            _length = length;
            _progress = progress;
            _watchdog = watchdog;
            _stall = stall;
            Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _length;
            return true;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct)
        {
            var buffer = new byte[Slice];
            long sent = 0;
            var lastMove = Environment.TickCount64;
            // Watchdog: if no bytes leave for the stall timeout, abort the whole request (closes the connection).
            using var timer = new Timer(_ =>
            {
                if (Environment.TickCount64 - Interlocked.Read(ref lastMove) > _stall.TotalMilliseconds && !_watchdog.IsCancellationRequested)
                {
                    Stalled = true;
                    try { _watchdog.Cancel(); } catch (ObjectDisposedException) { }
                }
            }, null, 250, 250);
            while (sent < _length)
            {
                var want = (int)Math.Min(Slice, _length - sent);
                int read;
                try
                {
                    _file.Position = _offset + sent;
                    read = await _file.ReadAsync(buffer.AsMemory(0, want), ct);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    ReadError = ex;
                    throw;
                }
                if (read == 0)
                {
                    ReadError = new IOException("The file got shorter while uploading.");
                    throw ReadError;
                }
                await stream.WriteAsync(buffer.AsMemory(0, read), ct);
                Interlocked.Exchange(ref lastMove, Environment.TickCount64);
                sent += read;
                _progress(sent);
            }
            await stream.FlushAsync(ct);
            OnBodySent?.Invoke();
        }
    }

    // ------------------------------------------------------------------ channel name
    public static async Task<string> GetChannelNameAsync(HttpClient http, string accessToken, CancellationToken ct,
        string baseUrl = "https://www.googleapis.com")
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl.TrimEnd('/')}/youtube/v3/channels?part=snippet&mine=true");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        using var resp = await http.SendAsync(req, cts.Token);
        if (!resp.IsSuccessStatusCode) return "";
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
        if (doc.RootElement.TryGetProperty("items", out var items) && items.GetArrayLength() > 0)
            return items[0].GetProperty("snippet").GetProperty("title").GetString() ?? "";
        return "";
    }
}
