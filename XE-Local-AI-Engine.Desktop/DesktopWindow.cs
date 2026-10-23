namespace XE_Local_AI_Engine.Desktop;

using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Platform;
using XE_Local_AI_Engine.Desktop.Linux;

internal sealed class DesktopWindow : Window, IAsyncDisposable
{
    /// <summary>The route the start-page retry probes; the unlock page and the engine host both answer it. A protocol
    ///     literal duplicated from the engine's LocalApiRoutes (the shell has no reference to the engine assembly).</summary>
    internal const string EngineProbePath = "/api/local/v1/auth/status";

    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(400);

    // Loopback only (DesktopLaunchOptions enforces an HTTP loopback origin), so no proxy may see it.
    private static readonly HttpClient Probe = new(new SocketsHttpHandler
    {
        UseProxy = false
    })
    {
        Timeout = TimeSpan.FromSeconds(1)
    };

    private readonly DesktopLaunchOptions _options;
    private readonly bool _devTools;
    private readonly NativeWebView _webView;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly DesktopNavigationGate _navigation = new(TimeProvider.System);
    private readonly Uri _start;
    private Task<GtkDesktopBridge>? _linuxInitialization;
    private GtkDesktopBridge? _linux;
    private bool _disposed;

    // True from a start-page load until it loads or fails for good; afterwards a failed navigation is the page's concern.
    private bool _startLoading = true;

    // Counts start-page loads, so a retry still waiting on its probe knows a Reload has replaced its load.
    private int _startGeneration;

    /// <summary>Opens <paramref name="path" /> on the engine origin under the shell's one profile, document policy and
    ///     navigation guard; <paramref name="top" />, when given, docks above the page.</summary>
    internal DesktopWindow(DesktopLaunchOptions options, bool devTools, string path = "/", Control? top = null)
    {
        _options = options;
        _devTools = devTools;
        if (OperatingSystem.IsWindows() && !WebViewAdapterInfo.GetAdapterInfo(WebViewAdapterType.WebView2).IsInstalled)
        {
            throw new PlatformNotSupportedException("Microsoft Edge WebView2 Runtime is required.");
        }

        Directory.CreateDirectory(Path.Combine(options.ProfileDirectory, "cache"));
        Title = DesktopText.Title;
        Width = 1280;
        Height = 800;
        MinWidth = 800;
        MinHeight = 600;

        _webView = new NativeWebView();
        // WPE is unsupported on Linux; never set this on Windows, where a WebKitGtk-only list leaves no adapter.
        if (OperatingSystem.IsLinux())
        {
            _webView.AdapterPreference = [WebViewAdapterType.WebKitGtk];
        }

        _webView.EnvironmentRequested += OnEnvironmentRequested;
        _webView.AdapterCreated += OnAdapterCreated;
        _webView.WebMessageReceived += (_, args) => _linux?.ReceiveMessage(args.Body);
        _webView.NavigationStarted += OnNavigationStarted;
        _webView.NavigationCompleted += OnNavigationCompleted;
        _webView.NewWindowRequested += OnNewWindowRequested;
        if (top is null)
        {
            Content = _webView;
        }
        else
        {
            DockPanel.SetDock(top, Dock.Top);
            Content = new DockPanel
            {
                Children =
                {
                    top,
                    _webView
                }
            };
        }

        _start = new Uri(options.Origin, path);
        _startGeneration++;
        _webView.Source = _start;
    }

    private void OnEnvironmentRequested(object? sender, WebViewEnvironmentRequestedEventArgs args)
    {
        // Debug mode only (--debug); the document policies stay unchanged.
        args.EnableDevTools = _devTools;
        var cacheDirectory = Path.Combine(_options.ProfileDirectory, "cache");
        switch (args)
        {
            case WindowsWebView2EnvironmentRequestedEventArgs windows:
                windows.UserDataFolder = _options.ProfileDirectory;
                break;
            case GtkWebViewEnvironmentRequestedEventArgs gtk:
                gtk.ApplicationNameForUserAgent = GtkDocumentPolicy.UserAgentMarker;
                gtk.BaseDataDirectory = _options.ProfileDirectory;
                gtk.BaseCacheDirectory = cacheDirectory;
                break;
            default:
                throw new PlatformNotSupportedException("The selected WebView adapter cannot use the required explicit profile directory.");
        }
    }

    private async void OnAdapterCreated(object? sender, WebViewAdapterEventArgs args)
    {
        if (!OperatingSystem.IsLinux() || _disposed) { return; }

        try
        {
            _linuxInitialization = GtkDesktopBridge.CreateAsync(args.TryGetPlatformHandle(), _options.Origin, _webView, this);
            _linux = await _linuxInitialization;
        }
        catch (Exception exception) { _ready.TrySetException(exception); }
    }

    /// <summary>The single close and dispose entry point: DesktopApplication cancels the window's Closing event and
    ///     disposes this window itself before the real close, so there is no second teardown path here.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) { return; }

        _disposed = true;
        var linux = _linux;
        if (linux is null && _linuxInitialization is not null)
        {
            try { linux = await _linuxInitialization; }
            catch (Exception) { return; } // Failed creation already disconnected and released its native resources.
        }

        if (linux is not null) { await linux.DisposeAsync(); }
    }

    /// <summary>Loads the start page again, after the engine behind the same origin was relaunched. UI thread only.</summary>
    internal void Reload()
    {
        if (_disposed) { return; }

        _startLoading = true;
        _startGeneration++;
        _navigation.Restart();
        try { _webView.Navigate(_start); }
        catch (Exception)
        {
            // The caller (the engine relaunch) reports the failure; only the loading state is this window's to settle.
            _startLoading = false;
            throw;
        }
    }

    internal Task WaitUntilReadyAsync(CancellationToken cancellationToken) =>
        _ready.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);

    /// <summary>Reads the SPA's chosen UI language raw (WebView returns JSON: <c>"de"</c> quoted, a missing key as
    ///     <c>null</c>); null when the page cannot answer or the shell is stopping. UI thread only.</summary>
    // Never throws: every caller is a fire-and-forget UI handler that keeps the last-known language.
    internal Task<string?> ReadLanguageAsync(CancellationToken cancellationToken) =>
        EvaluateAsync("localStorage.getItem('i18nextLng')", cancellationToken);

    /// <summary>The page's own address, client-side route changes included; null when the page cannot answer. UI thread only.</summary>
    internal async Task<Uri?> ReadLocationAsync(CancellationToken cancellationToken)
    {
        if (await EvaluateAsync("location.href", cancellationToken) is not { } raw) { return null; }

        try { return Uri.TryCreate(JsonSerializer.Deserialize<string>(raw), UriKind.Absolute, out var page) ? page : null; }
        catch (JsonException) { return null; }
    }

    private async Task<string?> EvaluateAsync(string script, CancellationToken cancellationToken)
    {
        if (_disposed) { return null; }

        try { return await _webView.InvokeScript(script).WaitAsync(TimeSpan.FromSeconds(3), cancellationToken); }
        catch (Exception) { return null; }
    }

    private async void OnNavigationStarted(object? sender, WebViewNavigationStartingEventArgs args)
    {
        _navigation.Started();
        var disposition = DesktopLaunchOptions.ClassifyNavigation(_options.Origin, args.Request);
        if (disposition == NavigationDisposition.SameOrigin)
        {
            _linux?.Invalidate();
            return;
        }

        args.Cancel = true;
        if (_linux is not null)
        {
            try { await _linux.RevokeForBlockedNavigationAsync(); }
            catch (Exception exception)
            {
                // Once the window is ready TrySetException is a no-op, so the desktop log is the only record left.
                if (!_ready.TrySetException(exception))
                {
                    DesktopStartupDiagnostics.Record($"Revoking the bridge for a blocked navigation failed ({exception.GetType().Name}). {exception.Message}");
                }
            }
        }

        if (disposition == NavigationDisposition.External)
        {
            await OpenExternalAsync(args.Request!);
        }
    }

    private async void OnNavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs args)
    {
        var outcome = _navigation.Completed(args.IsSuccess);
        if (outcome != DesktopNavigationGate.Outcome.Loaded)
        {
            if (!_startLoading || _disposed || outcome == DesktopNavigationGate.Outcome.Superseded) { return; }

            if (outcome == DesktopNavigationGate.Outcome.Retry)
            {
                await RetryStartAsync();
                return;
            }

            FailStart(new DesktopPageLoadException($"The desktop page could not load from the engine at {_options.Origin} within {DesktopNavigationGate.RetryWindow.TotalSeconds:0} seconds."));
            return;
        }

        if (DesktopLaunchOptions.ClassifyNavigation(_options.Origin, args.Request) != NavigationDisposition.SameOrigin || _disposed) { return; }

        _startLoading = false;

        try
        {
            if (OperatingSystem.IsLinux())
            {
                var linux = _linux ?? (_linuxInitialization is null ? null : await _linuxInitialization);
                if (linux is null) { throw new PlatformNotSupportedException("The GTK adapter is required."); }

                await linux.ArmAsync();
            }

            _ready.TrySetResult();
        }
        catch (Exception exception) { _ready.TrySetException(exception); }
    }

    /// <summary>Waits until the engine origin answers any HTTP request, or the retry window ends, then loads the start
    ///     page again; a retry that lands while the port is being rebound after a vault unlock would only fail again.</summary>
    private async Task RetryStartAsync()
    {
        DesktopStartupDiagnostics.Record("The desktop page did not load; retrying the start page once the engine answers.");
        var generation = _startGeneration;
        do
        {
            // Continues on the UI thread (the caller is a UI event handler), so the WebView is safe to touch; a disposed
            // window is caught by the check below, so the delay needs no token whose cancellation would throw here.
            await Task.Delay(RetryDelay, CancellationToken.None);
            if (IsStale(generation)) { return; }
        } while (!await EngineAnswersAsync() && _navigation.CanRetry());

        // The probe can take a second: a disposal or a Reload during it owns the window now.
        if (IsStale(generation)) { return; }

        try { _webView.Navigate(_start); }
        catch (Exception exception) { FailStart(exception); }
    }

    private bool IsStale(int generation) =>
        _disposed || !_startLoading || generation != _startGeneration;

    /// <summary>Ends the start-page load as failed; a reload after an engine relaunch finds the window already ready, so
    ///     the desktop log is then the only record left.</summary>
    private void FailStart(Exception failure)
    {
        _startLoading = false;
        if (!_ready.TrySetException(failure))
        {
            DesktopStartupDiagnostics.Record($"The desktop page could not load ({failure.GetType().Name}). {failure.Message}");
        }
    }

    /// <summary>True on any HTTP answer from the engine origin, and when the probe itself cannot be made (the plain
    ///     delayed retry is then the fallback); false while nothing listens.</summary>
    private async Task<bool> EngineAnswersAsync()
    {
        try
        {
            using var response = await Probe.GetAsync(new Uri(_options.Origin, EngineProbePath), HttpCompletionOption.ResponseHeadersRead, CancellationToken.None);
            return true;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException) { return false; }
        catch (Exception) { return true; }
    }

    private async void OnNewWindowRequested(object? sender, WebViewNewWindowRequestedEventArgs args)
    {
        args.Handled = true;
        var disposition = DesktopLaunchOptions.ClassifyNavigation(_options.Origin, args.Request);
        if (disposition == NavigationDisposition.SameOrigin)
        {
            _webView.Navigate(args.Request!);
        }
        else if (disposition == NavigationDisposition.External)
        {
            await OpenExternalAsync(args.Request!);
        }
    }

    private async Task OpenExternalAsync(Uri uri)
    {
        try
        {
            if (!await Launcher.LaunchUriAsync(uri))
            {
                await Console.Error.WriteLineAsync("The external HTTP(S) navigation could not be opened by the platform launcher.");
            }
        }
        catch (Exception)
        {
            await Console.Error.WriteLineAsync("The external HTTP(S) navigation could not be opened by the platform launcher.");
        }
    }
}
