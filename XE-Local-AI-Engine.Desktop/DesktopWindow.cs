namespace XE_Local_AI_Engine.Desktop;

using Avalonia.Controls;
using Avalonia.Platform;
using XE_Local_AI_Engine.Desktop.Linux;

internal sealed class DesktopWindow : Window, IAsyncDisposable
{
    private readonly DesktopLaunchOptions _options;
    private readonly NativeWebView _webView;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task<GtkDesktopBridge>? _linuxInitialization;
    private GtkDesktopBridge? _linux;
    private bool _disposed;

    internal DesktopWindow(DesktopLaunchOptions options)
    {
        _options = options;
        if (OperatingSystem.IsWindows() && !WebViewAdapterInfo.GetAdapterInfo(WebViewAdapterType.WebView2).IsInstalled)
        {
            throw new PlatformNotSupportedException("Microsoft Edge WebView2 Runtime is required.");
        }

        Directory.CreateDirectory(Path.Combine(options.ProfileDirectory, "cache"));
        Title = "XE Local AI Engine";
        Width = 1280;
        Height = 800;
        MinWidth = 800;
        MinHeight = 600;

        _webView = new NativeWebView();
        _webView.EnvironmentRequested += OnEnvironmentRequested;
        _webView.AdapterCreated += OnAdapterCreated;
        _webView.WebMessageReceived += (_, args) => _linux?.ReceiveMessage(args.Body);
        _webView.NavigationStarted += OnNavigationStarted;
        _webView.NavigationCompleted += OnNavigationCompleted;
        _webView.NewWindowRequested += OnNewWindowRequested;
        Content = _webView;
        _webView.Source = options.Origin;
    }

    private void OnEnvironmentRequested(object? sender, WebViewEnvironmentRequestedEventArgs args)
    {
        args.EnableDevTools = false;
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
            case LinuxWpeWebViewEnvironmentRequestedEventArgs wpe:
                wpe.PreferWebKitGtkInstead = true;
                wpe.DataDirectory = _options.ProfileDirectory;
                wpe.CacheDirectory = cacheDirectory;
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

    internal Task WaitUntilReadyAsync(CancellationToken cancellationToken) =>
        _ready.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);

    /// <summary>Reads the SPA's chosen UI language raw (WebView returns JSON: <c>"de"</c> quoted, a missing key as
    ///     <c>null</c>); null when the page cannot answer or the shell is stopping. UI thread only.</summary>
    // Never throws: every caller is a fire-and-forget UI handler that keeps the last-known language.
    internal async Task<string?> ReadLanguageAsync(CancellationToken cancellationToken)
    {
        if (_disposed) { return null; }

        try
        {
            return await _webView.InvokeScript("localStorage.getItem('i18nextLng')")
                                 .WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
        }
        catch (Exception) { return null; }
    }

    private async void OnNavigationStarted(object? sender, WebViewNavigationStartingEventArgs args)
    {
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
            catch (Exception exception) { _ready.TrySetException(exception); }
        }

        if (disposition == NavigationDisposition.External)
        {
            await OpenExternalAsync(args.Request!);
        }
    }

    private async void OnNavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs args)
    {
        if (!args.IsSuccess)
        {
            _ready.TrySetException(new InvalidOperationException("The desktop page could not load."));
            return;
        }

        if (DesktopLaunchOptions.ClassifyNavigation(_options.Origin, args.Request) != NavigationDisposition.SameOrigin || _disposed) { return; }

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
