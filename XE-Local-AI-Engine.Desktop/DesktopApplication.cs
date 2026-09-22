namespace XE_Local_AI_Engine.Desktop;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

internal sealed class DesktopApplication : Application, IAsyncDisposable
{
    private readonly DesktopStartupOptions _options;
    private readonly CancellationTokenSource _stopping = new();
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private DesktopEngineSession? _engine;
    private DesktopInstance? _instance;
    private TrayIcon? _tray;
    private Window? _window;
    private DesktopCloseDialog? _dialog;
    private Task _initialization = Task.CompletedTask;
    private DesktopCloseAction _preference;
    private bool _exiting;
    private bool _asking;
    private bool _failed;
    private bool _disposed;

    internal DesktopApplication(DesktopStartupOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _window = new Window { Title = "XE AI-Engine", Width = 560, Height = 220 };
            _window.Content = new TextBlock { Text = DesktopText.Starting, Margin = new Thickness(24), TextWrapping = TextWrapping.Wrap };
            _window.Closing += OnClosing;
            _window.Opened += (_, _) => _initialization = InitializeDesktopAsync();
            desktop.MainWindow = _window;
            desktop.ShutdownRequested += (sender, args) =>
            {
                if (!_exiting)
                {
                    args.Cancel = true;
                    _ = QuitAsync();
                }
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    // Windows-only by operator decision (docs/roadmaps/native-desktop-1.0.md): Linux tray hiding stays unavailable
    // rather than hiding the window behind a tray host that may offer no way to restore it.
    private bool TrayAvailable => OperatingSystem.IsWindows() && _tray?.NativeMenuExporter is not null;

    private async Task InitializeDesktopAsync()
    {
        try
        {
            Directory.CreateDirectory(_options.DataDirectory);
            Directory.CreateDirectory(_options.ProfileDirectory);
            _instance = DesktopInstance.TryAcquire(_options.DataDirectory);
            if (_instance is null)
            {
                if (_options.Port is not null)
                {
                    var existing = await DesktopEngineSession.DiscoverAsync(_options.DataDirectory, _stopping.Token)
                        ?? throw new InvalidOperationException("The existing engine is not ready.");
                    DesktopEngineSession.ValidateRequestedPort(existing, _options.Port);
                }

                await DesktopInstance.ActivateAsync(_options.DataDirectory, _stopping.Token);
                Dispatcher.UIThread.Post(() => _ = QuitAsync());
                return;
            }

            DesktopInstance.RetainUntilProcessExit(_instance);
            _instance.Listen(() => Dispatcher.UIThread.Post(ShowWindow));
            _preference = await DesktopPreferences.ReadAsync(_options.DataDirectory, _stopping.Token);
            _engine = await DesktopEngineSession.StartAsync(_options, _stopping.Token);
            _stopping.Token.ThrowIfCancellationRequested();
            var startupWindow = _window!;
            var mainWindow = new DesktopWindow(new DesktopLaunchOptions(_engine.Origin, _options.ProfileDirectory));
            mainWindow.Closing += OnClosing;
            mainWindow.AddDesktopSettings(OpenSettings);
            _window = mainWindow;
            _desktop!.MainWindow = mainWindow;
            await CreateTrayAsync();
            mainWindow.Show();
            startupWindow.Closing -= OnClosing;
            startupWindow.Close();
            await mainWindow.WaitUntilReadyAsync(_stopping.Token);
            if (_engine.WaitForEngineExitAsync() is { } exit)
            {
                _ = ExitWithEngineAsync(exit);
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            // Quit waits for initialization before disposing its owned resources.
        }
        catch (Exception exception)
        {
            DesktopStartupDiagnostics.Record($"Desktop startup failed ({exception.GetType().Name}). {exception.Message}");
            await Console.Error.WriteLineAsync("Desktop startup failed; inspect the engine log and installed prerequisites.");
            if (_window is DesktopWindow failedWindow) { await failedWindow.DisposeAsync(); }
            var reason = OperatingSystem.IsLinux() && _window is DesktopWindow ? Linux.GtkDesktopBridge.FailureText : DesktopText.StartupFailed;
            // The engine's own last words (a missing shared runtime, a port conflict) are the only actionable part.
            ShowFailure($"{reason}{Environment.NewLine}{Environment.NewLine}{exception.Message}",
                includeWebViewLink: OperatingSystem.IsWindows());
        }
    }

    private async Task CreateTrayAsync()
    {
        await using var iconStream = typeof(DesktopApplication).Assembly.GetManifestResourceStream("DesktopIcon.png");
        if (iconStream is null)
        {
            return;
        }

        var icon = new WindowIcon(iconStream);
        _window!.Icon = icon;
        _tray = new TrayIcon { Icon = icon, ToolTipText = "XE AI-Engine", IsVisible = true };
        var menu = new NativeMenu();
        var open = new NativeMenuItem(DesktopText.Open);
        open.Click += (_, _) => ShowWindow();
        var settings = new NativeMenuItem(DesktopText.Settings);
        settings.Click += (_, _) => OpenSettings();
        var quit = new NativeMenuItem(DesktopText.Quit);
        quit.Click += (_, _) => _ = RequestQuitAsync();
        menu.Items.Add(open);
        menu.Items.Add(settings);
        menu.Items.Add(quit);
        _tray.Menu = menu;
        _tray.Clicked += (_, _) => ShowWindow();
    }

    private void ShowWindow()
    {
        if (_window is null || _exiting)
        {
            return;
        }

        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Activate();
    }

    private void OnClosing(object? sender, WindowClosingEventArgs args)
    {
        if (_exiting)
        {
            return;
        }

        args.Cancel = true;
        _ = HandleCloseAsync(settings: false);
    }

    private void OpenSettings()
    {
        ShowWindow();
        _ = HandleCloseAsync(settings: true);
    }

    private async Task HandleCloseAsync(bool settings)
    {
        if (_asking || _exiting || _window is null)
        {
            return;
        }

        if (_engine is null || _failed)
        {
            await QuitAsync();
            return;
        }

        _asking = true;
        try
        {
            var action = DesktopPreferences.Resolve(_preference, TrayAvailable);
            if (settings || action == DesktopCloseAction.Ask)
            {
                _dialog = new DesktopCloseDialog(TrayAvailable, _engine.OwnsEngine, settings);
                var choice = await _dialog.ShowDialog<DesktopCloseChoice?>(_window);
                _dialog = null;
                if (choice is null || _exiting)
                {
                    return;
                }

                action = choice.Action;
                if (choice.Remember)
                {
                    _preference = action;
                    try
                    {
                        await DesktopPreferences.WriteAsync(_options.DataDirectory, action, _stopping.Token);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        await ShowNoticeAsync(DesktopText.SaveFailed);
                    }
                }
            }

            if (settings || _exiting)
            {
                return;
            }

            if (action == DesktopCloseAction.Tray && TrayAvailable)
            {
                _window.Hide();
            }
            else if (action == DesktopCloseAction.Quit)
            {
                await QuitAsync();
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            // Application exit cancels a pending preference write.
        }
        finally
        {
            _asking = false;
        }
    }

    private async Task RequestQuitAsync()
    {
        if (_asking || _exiting)
        {
            return;
        }

        if (_engine?.OwnsEngine == true && !_failed)
        {
            _asking = true;
            try
            {
                ShowWindow();
                _dialog = new DesktopCloseDialog(false, true, false, quitting: true);
                var choice = await _dialog.ShowDialog<DesktopCloseChoice?>(_window!);
                _dialog = null;
                if (choice is null)
                {
                    return;
                }
            }
            finally
            {
                _asking = false;
            }
        }

        await QuitAsync();
    }

    private async Task ExitWithEngineAsync(Task exit)
    {
        await exit;
        Dispatcher.UIThread.Post(() => _ = HandleEngineExitAsync());
    }

    private async Task HandleEngineExitAsync()
    {
        if (_exiting)
        {
            return;
        }

        if (_engine?.EngineExitCode is not (null or 0))
        {
            ShowWindow();
            await ShowNoticeAsync(DesktopText.EngineStopped);
        }

        await QuitAsync();
    }

    private async Task QuitAsync()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        _dialog?.Close(null);
        try
        {
            await _stopping.CancelAsync();
            if (_window is DesktopWindow nativeWindow) { await nativeWindow.DisposeAsync(); }
            if (_window is not null)
            {
                _window.Content = new TextBlock { Text = DesktopText.Closing, Margin = new Thickness(24) };
            }

            await DisposeAsync();
            _desktop?.Shutdown();
        }
        catch (Exception)
        {
            await Console.Error.WriteLineAsync("Desktop shutdown could not complete.");
            _exiting = false;
            ShowFailure(DesktopText.StopFailed, includeWebViewLink: false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _stopping.CancelAsync();
        await _initialization;
        if (_engine is not null)
        {
            await _engine.DisposeAsync();
            _engine = null;
        }

        if (_instance is not null)
        {
            // The updater must still see a loaded shell until this process exits.
            await _instance.StopListeningAsync();
        }

        _tray?.Dispose();
        _stopping.Dispose();
        _disposed = true;
    }

    private void ShowFailure(string text, bool includeWebViewLink)
    {
        _failed = true;
        if (_window is null)
        {
            return;
        }

        var panel = new StackPanel { Margin = new Thickness(24), Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });
        if (includeWebViewLink)
        {
            var link = new Button { Content = DesktopText.WebViewDownload };
            link.Click += async (_, _) =>
            {
                try
                {
                    await _window.Launcher.LaunchUriAsync(new UriBuilder(Uri.UriSchemeHttps, "developer.microsoft.com") { Path = "microsoft-edge/webview2/" }.Uri);
                }
                catch (Exception)
                {
                    await Console.Error.WriteLineAsync("The WebView2 download page could not be opened.");
                }
            };
            panel.Children.Add(link);
        }

        var quit = new Button { Content = DesktopText.Quit, HorizontalAlignment = HorizontalAlignment.Left };
        quit.Click += (_, _) => _ = QuitAsync();
        panel.Children.Add(quit);
        // The engine's error tail can outgrow the startup window, and an unreachable Quit button is a hang.
        _window.Content = new ScrollViewer { Content = panel };
        _window.Show();
        _window.Activate();
    }

    private async Task ShowNoticeAsync(string message)
    {
        var dialog = new Window { Title = "XE AI-Engine", Width = 440, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new Thickness(24), Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        var close = new Button { Content = "OK", IsDefault = true, IsCancel = true };
        close.Click += (_, _) => dialog.Close();
        panel.Children.Add(close);
        dialog.Content = panel;
        await dialog.ShowDialog(_window!);
    }
}
