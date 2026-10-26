using CommunityToolkit.Mvvm.ComponentModel;
using FootageVault.Core;
using Microsoft.UI.Dispatching;

namespace FootageVault.Services;

/// <summary>
/// The one place that owns settings, the queue, the upload engine and the Google sign-in.
/// Pages talk to this; everything heavy lives in FootageVault.Core.
/// </summary>
public sealed class AppHost : ObservableObject
{
    public static AppHost Current { get; } = new();

    public string DataDir { get; }
    public string SettingsPath => Path.Combine(DataDir, "settings.json");
    public string ClientSecretPath => Path.Combine(DataDir, "client_secret.json");

    public AppSettings Settings { get; private set; } = new();
    public Store Store { get; private set; } = null!;
    public QueueEngine Engine { get; private set; } = null!;
    public HttpClient Http { get; } = YouTubeUploader.CreateHttpClient();
    public DispatcherQueue Dispatcher { get; private set; } = null!;

    /// <summary>The folders being previewed (scanned but not queued yet).</summary>
    public Plan? Plan { get; set; }
    public event Action? PlanChanged;

    private GoogleAuth? _auth;
    private readonly ITokenStore _tokens = new WindowsTokenStore();

    private AppHost()
    {
        DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FootageVault");
        Directory.CreateDirectory(DataDir);
    }

    public void Init(DispatcherQueue dispatcher)
    {
        Dispatcher = dispatcher;
        Log.Init(Path.Combine(DataDir, "logs"));
        Log.Info("Footage Vault starting");
        Settings = AppSettings.Load(SettingsPath);
        Store = new Store(Path.Combine(DataDir, "state.json"));
        Engine = new QueueEngine(Store, () => Settings);
        Engine.Changed += () => Ui(() => SignedIn = Engine.SignedIn && _auth is not null);
        Engine.Start();
        _ = TrySilentSignInAsync();
    }

    public void Shutdown() => Engine.Dispose();

    public void Ui(Action a)
    {
        if (Dispatcher.HasThreadAccess) a();
        else Dispatcher.TryEnqueue(() => a());
    }

    public void SaveSettings(Action<AppSettings> change)
    {
        var copy = Settings.Clone();
        change(copy);
        copy.Save(SettingsPath);
        Settings = copy;
        OnPropertyChanged(nameof(Settings));
    }

    public void NotifyPlanChanged() => PlanChanged?.Invoke();

    // ------------------------------------------------------------------ Google sign-in
    private bool _signedIn;
    public bool SignedIn { get => _signedIn; private set => SetProperty(ref _signedIn, value); }

    private bool _authBusy;
    public bool AuthBusy { get => _authBusy; private set => SetProperty(ref _authBusy, value); }

    private string _channelName = "";
    public string ChannelName { get => _channelName; private set => SetProperty(ref _channelName, value); }

    private string _authError = "";
    public string AuthError { get => _authError; private set => SetProperty(ref _authError, value); }

    public bool HasClientSecret => File.Exists(ClientSecretPath);

    private CancellationTokenSource? _signInCts;

    /// <returns>null on success, else a message.</returns>
    public string? ImportClientSecret(string path)
    {
        try
        {
            ClientSecret.Parse(File.ReadAllText(path));
            File.Copy(path, ClientSecretPath, overwrite: true);
            _auth = null;
            OnPropertyChanged(nameof(HasClientSecret));
            return null;
        }
        catch (Exception ex)
        {
            return ex is FormatException ? ex.Message : $"That doesn't look like the Google client file: {ex.Message}";
        }
    }

    private GoogleAuth? Auth()
    {
        if (_auth is not null) return _auth;
        if (!HasClientSecret) return null;
        try
        {
            _auth = new GoogleAuth(ClientSecret.Parse(File.ReadAllText(ClientSecretPath)), _tokens, Http);
        }
        catch (Exception ex)
        {
            AuthError = ex.Message;
        }
        return _auth;
    }

    public async Task TrySilentSignInAsync()
    {
        var auth = Auth();
        if (auth is null || !auth.HasSavedSignIn) return;
        AuthBusy = true;
        try { await ConnectAsync(auth, CancellationToken.None); }
        catch (Exception ex)
        {
            Log.Warn($"Saved sign-in not usable: {ex.Message}");
            AuthError = ex is AuthException ? ex.Message : "Couldn't reach Google. Check the internet connection.";
        }
        finally { AuthBusy = false; }
    }

    public async Task SignInAsync()
    {
        var auth = Auth();
        if (auth is null) return;
        _signInCts?.Cancel();
        _signInCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        AuthBusy = true;
        AuthError = "";
        try
        {
            await auth.SignInAsync(Shell.Open, _signInCts.Token);
            await ConnectAsync(auth, _signInCts.Token);
        }
        catch (OperationCanceledException)
        {
            AuthError = "Sign-in wasn't finished in the browser. Try again.";
        }
        catch (Exception ex)
        {
            Log.Warn($"Sign-in failed: {ex.Message}");
            AuthError = ex.Message;
        }
        finally { AuthBusy = false; }
    }

    public void CancelSignIn() => _signInCts?.Cancel();

    private async Task ConnectAsync(GoogleAuth auth, CancellationToken ct)
    {
        var token = await auth.GetAccessTokenAsync(ct);
        string name;
        try { name = await YouTubeUploader.GetChannelNameAsync(Http, token, ct); }
        catch (Exception) { name = ""; }
        ChannelName = name;
        Engine.SetUploader(new YouTubeUploader(Http, (c, force) => auth.GetAccessTokenAsync(c, force)));
        SignedIn = true;
        AuthError = "";
        Log.Info($"Signed in{(name.Length > 0 ? " to " + name : "")}");
    }

    public void SignOut()
    {
        _auth?.SignOut();
        _tokens.Clear();
        Engine.SetUploader(null);
        SignedIn = false;
        ChannelName = "";
        Log.Info("Signed out");
    }
}
