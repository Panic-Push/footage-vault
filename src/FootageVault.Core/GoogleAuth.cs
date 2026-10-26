using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FootageVault.Core;

/// <summary>Stores the long-lived refresh token (Windows Credential Manager on Windows).</summary>
public interface ITokenStore
{
    string? Load();
    void Save(string refreshToken);
    void Clear();
}

public sealed class MemoryTokenStore : ITokenStore
{
    private string? _t;
    public string? Load() => _t;
    public void Save(string refreshToken) => _t = refreshToken;
    public void Clear() => _t = null;
}

public sealed record ClientSecret(string ClientId, string Secret, string AuthUri, string TokenUri)
{
    public static ClientSecret Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var node = root.TryGetProperty("installed", out var i) ? i : root.TryGetProperty("web", out var w) ? w : root;
        string Get(string name, string fallback = "") =>
            node.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : fallback;
        var id = Get("client_id");
        if (id.Length == 0) throw new FormatException("This file has no client_id. Download the OAuth client JSON from Google Cloud again.");
        return new ClientSecret(id, Get("client_secret"), Get("auth_uri", "https://accounts.google.com/o/oauth2/auth"),
            Get("token_uri", "https://oauth2.googleapis.com/token"));
    }
}

public class AuthException : Exception
{
    public AuthException(string message) : base(message) { }
}

/// <summary>
/// Google sign-in for installed apps: opens the system browser, receives the answer on a one-time
/// http://127.0.0.1:port/ address, and exchanges it with PKCE. The password never passes through the app.
/// </summary>
public sealed class GoogleAuth
{
    public const string ScopeUpload = "https://www.googleapis.com/auth/youtube.upload";
    public const string ScopeReadonly = "https://www.googleapis.com/auth/youtube.readonly";
    public static readonly string[] Scopes = { ScopeUpload, ScopeReadonly };

    private readonly ClientSecret _client;
    private readonly ITokenStore _tokens;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private string? _accessToken;
    private DateTimeOffset _expires;

    public GoogleAuth(ClientSecret client, ITokenStore tokens, HttpClient http)
    {
        _client = client;
        _tokens = tokens;
        _http = http;
    }

    public bool HasSavedSignIn => !string.IsNullOrEmpty(_tokens.Load());

    public void SignOut()
    {
        _tokens.Clear();
        _accessToken = null;
    }

    /// <summary>Interactive sign-in. <paramref name="openBrowser"/> opens the URL in the default browser.</summary>
    public async Task SignInAsync(Action<string> openBrowser, CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var redirect = $"http://127.0.0.1:{port}/";
            var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
            var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            var state = Base64Url(RandomNumberGenerator.GetBytes(16));
            var url = $"{_client.AuthUri}?response_type=code&client_id={Uri.EscapeDataString(_client.ClientId)}" +
                      $"&redirect_uri={Uri.EscapeDataString(redirect)}&scope={Uri.EscapeDataString(string.Join(' ', Scopes))}" +
                      $"&state={state}&code_challenge={challenge}&code_challenge_method=S256&access_type=offline&prompt=consent";
            openBrowser(url);

            var query = await WaitForRedirectAsync(listener, ct);
            if (query.TryGetValue("error", out var err))
                throw new AuthException(err == "access_denied" ? "Sign-in was cancelled in the browser." : $"Google said: {err}");
            if (!query.TryGetValue("state", out var gotState) || gotState != state)
                throw new AuthException("The sign-in answer didn't match. Please try again.");
            if (!query.TryGetValue("code", out var code)) throw new AuthException("Google didn't send a sign-in code.");

            var body = new Dictionary<string, string>
            {
                ["code"] = code, ["client_id"] = _client.ClientId, ["client_secret"] = _client.Secret,
                ["redirect_uri"] = redirect, ["grant_type"] = "authorization_code", ["code_verifier"] = verifier,
            };
            var token = await PostTokenAsync(body, ct);
            if (!token.TryGetProperty("refresh_token", out var rt))
                throw new AuthException("Google didn't return a long-lived sign-in. Sign in again.");
            _tokens.Save(rt.GetString()!);
            Accept(token);
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task<Dictionary<string, string>> WaitForRedirectAsync(TcpListener listener, CancellationToken ct)
    {
        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync(ct);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync(ct) ?? "";
            string? line;
            while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(ct))) { }   // skip headers

            var target = requestLine.Split(' ').ElementAtOrDefault(1) ?? "/";
            var q = ParseQuery(target.Contains('?') ? target[(target.IndexOf('?') + 1)..] : "");
            var isAnswer = q.ContainsKey("code") || q.ContainsKey("error");
            var html = isAnswer
                ? "<html><head><meta charset='utf-8'><title>Footage Vault</title></head><body style=\"font-family:'Segoe UI',sans-serif;padding:48px\">" +
                  "<h2>You're signed in</h2><p>You can close this tab and go back to Footage Vault.</p></body></html>"
                : "<html><body></body></html>";
            var bytes = Encoding.UTF8.GetBytes(html);
            var header = $"HTTP/1.1 {(isAnswer ? "200 OK" : "404 Not Found")}\r\nContent-Type: text/html; charset=utf-8\r\n" +
                         $"Content-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(header), ct);
            await stream.WriteAsync(bytes, ct);
            if (isAnswer) return q;     // ignore e.g. favicon requests
        }
    }

    public static Dictionary<string, string> ParseQuery(string query)
    {
        var d = new Dictionary<string, string>();
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            d[Uri.UnescapeDataString(kv[0].Replace('+', ' '))] = kv.Length > 1 ? Uri.UnescapeDataString(kv[1].Replace('+', ' ')) : "";
        }
        return d;
    }

    /// <summary>A valid access token, refreshing it when it is about to expire.</summary>
    public async Task<string> GetAccessTokenAsync(CancellationToken ct, bool forceRefresh = false)
    {
        if (!forceRefresh && _accessToken is not null && DateTimeOffset.UtcNow < _expires - TimeSpan.FromMinutes(2))
            return _accessToken;
        await _refreshLock.WaitAsync(ct);
        try
        {
            if (!forceRefresh && _accessToken is not null && DateTimeOffset.UtcNow < _expires - TimeSpan.FromMinutes(2))
                return _accessToken;
            var refresh = _tokens.Load() ?? throw new AuthException("Not signed in.");
            var body = new Dictionary<string, string>
            {
                ["client_id"] = _client.ClientId, ["client_secret"] = _client.Secret,
                ["refresh_token"] = refresh, ["grant_type"] = "refresh_token",
            };
            JsonElement token;
            try { token = await PostTokenAsync(body, ct); }
            catch (AuthException ex) when (ex.Message.Contains("invalid_grant"))
            {
                _tokens.Clear();
                throw new AuthException("Your Google sign-in expired or was removed. Please sign in again.");
            }
            Accept(token);
            return _accessToken!;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private void Accept(JsonElement token)
    {
        _accessToken = token.GetProperty("access_token").GetString();
        var secs = token.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 3600;
        _expires = DateTimeOffset.UtcNow.AddSeconds(secs);
    }

    private async Task<JsonElement> PostTokenAsync(Dictionary<string, string> body, CancellationToken ct)
    {
        using var resp = await _http.PostAsync(_client.TokenUri, new FormUrlEncodedContent(body), ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            string reason = ((int)resp.StatusCode).ToString();
            try
            {
                using var d = JsonDocument.Parse(text);
                reason = d.RootElement.GetProperty("error").GetString() ?? reason;
            }
            catch (Exception) { }
            throw new AuthException($"Google sign-in failed ({reason}).");
        }
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    private static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
