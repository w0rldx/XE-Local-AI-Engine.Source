namespace XE_Local_AI_Engine.Tray;

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;

public sealed class App : Application, IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private readonly CancellationTokenSource _shutdownTokenSource = new();

    private readonly HostAgentStatusClient _statusClient = new();
    private TrayHealthSnapshot _currentSnapshot = TrayHealthSnapshot.Unreachable;
    private bool _disposed;
    private NativeMenuItem? _openWebUiMenuItem;
    private bool _pollInProgress;
    private DispatcherTimer? _pollTimer;
    private NativeMenuItem? _restartRuntimeMenuItem;
    private NativeMenuItem? _showDiagnosticsMenuItem;
    private NativeMenuItem? _startHostAgentMenuItem;
    private NativeMenuItem? _startServicesMenuItem;
    private NativeMenuItem? _stopServicesMenuItem;
    private TrayIcon? _trayIcon;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _pollTimer?.Stop();
        _shutdownTokenSource.Cancel();
        _shutdownTokenSource.Dispose();
        _statusClient.Dispose();
        _disposed = true;
    }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        }

        _trayIcon = TrayIcon.GetIcons(this)?.FirstOrDefault();
        ResolveMenuItems();
        ApplyTrayStatus(TrayHealthSnapshot.Unreachable);
        StartPolling();
        _ = EnsureHostAgentRunningOnLaunchAsync(_shutdownTokenSource.Token);

        base.OnFrameworkInitializationCompleted();
    }

    private void StartPolling()
    {
        _pollTimer = new DispatcherTimer(PollInterval, DispatcherPriority.Background, PollTimerOnTick)
        {
            IsEnabled = true
        };

        _ = PollHostAgentStatusAsync(_shutdownTokenSource.Token);
    }

    private async void PollTimerOnTick(object? sender, EventArgs e)
    {
        await PollHostAgentStatusAsync(_shutdownTokenSource.Token).ConfigureAwait(true);
    }

    private async Task PollHostAgentStatusAsync(CancellationToken cancellationToken)
    {
        if (_pollInProgress)
        {
            return;
        }

        _pollInProgress = true;
        try
        {
            var snapshot = await _statusClient.GetStatusAsync(cancellationToken).ConfigureAwait(true);
            ApplyTrayStatus(snapshot);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // App shutdown requested.
        }
        finally
        {
            _pollInProgress = false;
        }
    }

    private async Task EnsureHostAgentRunningOnLaunchAsync(CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await _statusClient.GetStatusAsync(cancellationToken).ConfigureAwait(true);
            if (snapshot.IsReachable)
            {
                ApplyTrayStatus(snapshot);
                return;
            }

            await StartHostAgentAsync(cancellationToken).ConfigureAwait(true);
            await PollHostAgentStatusAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // App shutdown requested.
        }
        catch (Win32Exception exception)
        {
            ShowTextWindow("Start HostAgent", $"Unable to start HostAgent: {exception.Message}");
        }
        catch (IOException exception)
        {
            ShowTextWindow("Start HostAgent", $"Unable to start HostAgent: {exception.Message}");
        }
        catch (InvalidOperationException exception)
        {
            ShowTextWindow("Start HostAgent", exception.Message);
        }
    }

    private void ApplyTrayStatus(TrayHealthSnapshot snapshot)
    {
        if (_trayIcon is null)
        {
            return;
        }

        _trayIcon.Icon = LoadIcon(snapshot.IconAssetName);
        _trayIcon.ToolTipText = snapshot.ToolTipText;
        _currentSnapshot = snapshot;

        if (_openWebUiMenuItem is not null)
        {
            _openWebUiMenuItem.IsEnabled = snapshot.IsReachable && TryCreateWebUiUri(snapshot.WebUiUrl, out _);
        }

        if (_startHostAgentMenuItem is not null)
        {
            _startHostAgentMenuItem.IsVisible = !snapshot.IsReachable;
        }

        if (_stopServicesMenuItem is not null)
        {
            _stopServicesMenuItem.IsVisible = snapshot.IsReachable && snapshot.IsDesiredStateRunning;
        }

        if (_startServicesMenuItem is not null)
        {
            _startServicesMenuItem.IsVisible = snapshot.IsReachable && snapshot.IsDesiredStateStopped;
        }

        if (_restartRuntimeMenuItem is not null)
        {
            _restartRuntimeMenuItem.IsVisible = snapshot.IsReachable && snapshot.IsDesiredStateRunning;
        }

        if (_showDiagnosticsMenuItem is not null)
        {
            _showDiagnosticsMenuItem.IsEnabled = snapshot.IsReachable;
        }
    }

    private void ResolveMenuItems()
    {
        var items = _trayIcon?.Menu?.Items.OfType<NativeMenuItem>() ?? Enumerable.Empty<NativeMenuItem>();
        _openWebUiMenuItem = FindMenuItem(items, "Open Web UI");
        _startHostAgentMenuItem = FindMenuItem(items, "Start HostAgent");
        _stopServicesMenuItem = FindMenuItem(items, "Stop Services");
        _startServicesMenuItem = FindMenuItem(items, "Start Services");
        _restartRuntimeMenuItem = FindMenuItem(items, "Restart Runtime");
        _showDiagnosticsMenuItem = FindMenuItem(items, "Show Diagnostics");
    }

    private static NativeMenuItem? FindMenuItem(IEnumerable<NativeMenuItem> items, string header)
    {
        return items.FirstOrDefault(item => string.Equals(item.Header?.ToString(), header, StringComparison.Ordinal));
    }

    private static WindowIcon LoadIcon(string assetName)
    {
        using var stream = AssetLoader.Open(new Uri($"avares://XE-Local-AI-Engine.Tray/Assets/{assetName}"));
        return new WindowIcon(stream);
    }

    private async void OpenWebUiMenuItemOnClick(object? sender, EventArgs e)
    {
        if (!TryCreateWebUiUri(_currentSnapshot.WebUiUrl, out var uri))
        {
            ShowTextWindow("Open Web UI", "The Web UI URL is not available yet.");
            return;
        }

        if (!TryOpenUrl(uri))
        {
            ShowTextWindow("Open Web UI", $"Unable to launch the Web UI at {uri}.");
        }

        await Task.CompletedTask.ConfigureAwait(true);
    }

    private async void StartHostAgentMenuItemOnClick(object? sender, EventArgs e)
    {
        const string title = "Start HostAgent";

        try
        {
            await StartHostAgentAsync(_shutdownTokenSource.Token).ConfigureAwait(true);
            await PollHostAgentStatusAsync(_shutdownTokenSource.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (_shutdownTokenSource.IsCancellationRequested)
        {
            // App shutdown requested.
        }
        catch (Win32Exception exception)
        {
            ShowTextWindow(title, $"Unable to start HostAgent: {exception.Message}");
        }
        catch (IOException exception)
        {
            ShowTextWindow(title, $"Unable to start HostAgent: {exception.Message}");
        }
        catch (InvalidOperationException exception)
        {
            ShowTextWindow(title, exception.Message);
        }
    }

    private async void StopServicesMenuItemOnClick(object? sender, EventArgs e)
    {
        await ConfirmAndSendLifecycleActionAsync(title: "Stop Services",
            message: "Stop XE Local AI Engine services? Active work will be given the configured graceful shutdown window.",
            endpointName: "shutdown").ConfigureAwait(true);
    }

    private async void StartServicesMenuItemOnClick(object? sender, EventArgs e)
    {
        await ConfirmAndSendLifecycleActionAsync(title: "Start Services",
            message: "Start XE Local AI Engine services?",
            endpointName: "startup").ConfigureAwait(true);
    }

    private async void RestartRuntimeMenuItemOnClick(object? sender, EventArgs e)
    {
        await ConfirmAndSendLifecycleActionAsync(title: "Restart Runtime",
            message: "Restart the XE Local AI Engine runtime? Active work will be given the configured graceful shutdown window.",
            endpointName: "restart").ConfigureAwait(true);
    }

    private async void ShowDiagnosticsMenuItemOnClick(object? sender, EventArgs e)
    {
        try
        {
            var lines = await _statusClient.ReadDiagnosticsAsync(_shutdownTokenSource.Token).ConfigureAwait(true);
            ShowTextWindow("XE Local AI Engine Diagnostics", string.Join(Environment.NewLine, lines));
        }
        catch (OperationCanceledException) when (_shutdownTokenSource.IsCancellationRequested)
        {
            // App shutdown requested.
        }
        catch (HttpRequestException)
        {
            ShowTextWindow("XE Local AI Engine Diagnostics", "Unable to connect to the HostAgent diagnostics endpoint.");
        }
        catch (IOException)
        {
            ShowTextWindow("XE Local AI Engine Diagnostics", "Unable to read the HostAgent admin token.");
        }
        catch (UnauthorizedAccessException)
        {
            ShowTextWindow("XE Local AI Engine Diagnostics", "Access to the HostAgent admin token was denied.");
        }
        catch (InvalidOperationException exception)
        {
            ShowTextWindow("XE Local AI Engine Diagnostics", exception.Message);
        }
    }

    private async Task ConfirmAndSendLifecycleActionAsync(string title, string message, string endpointName)
    {
        if (!await ShowConfirmationAsync(title, message).ConfigureAwait(true))
        {
            return;
        }

        try
        {
            var succeeded = await _statusClient.SendLifecycleActionAsync(endpointName, _shutdownTokenSource.Token).ConfigureAwait(true);
            if (!succeeded)
            {
                ShowTextWindow(title, "The HostAgent rejected the requested action.");
                return;
            }

            await PollHostAgentStatusAsync(_shutdownTokenSource.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (_shutdownTokenSource.IsCancellationRequested)
        {
            // App shutdown requested.
        }
        catch (HttpRequestException)
        {
            ShowTextWindow(title, "Unable to connect to the HostAgent admin endpoint.");
        }
        catch (IOException)
        {
            ShowTextWindow(title, "Unable to read the HostAgent admin token.");
        }
        catch (UnauthorizedAccessException)
        {
            ShowTextWindow(title, "Access to the HostAgent admin token was denied.");
        }
        catch (InvalidOperationException exception)
        {
            ShowTextWindow(title, exception.Message);
        }
    }

    private static bool TryCreateWebUiUri(string? value, out Uri uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var parsed)
            && (string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)
                || string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)))
        {
            uri = parsed;
            return true;
        }

        uri = new Uri("about:blank");
        return false;
    }

    private static bool TryOpenUrl(Uri uri)
    {
        try
        {
            using var process = OperatingSystem.IsWindows()
                ? Process.Start("explorer.exe", uri.AbsoluteUri)
                : Process.Start("xdg-open", uri.AbsoluteUri);
            return process is not null;
        }
        catch (Win32Exception)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static async Task StartHostAgentAsync(CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            StartWindowsHostAgent();
            return;
        }

        if (OperatingSystem.IsLinux())
        {
            await StartLinuxHostAgentAsync(cancellationToken).ConfigureAwait(true);
            return;
        }

        throw new InvalidOperationException("Starting HostAgent is only supported on Windows and Linux.");
    }

    [SupportedOSPlatform("windows")]
    private static void StartWindowsHostAgent()
    {
        var hostAgentPath = Path.Combine(AppContext.BaseDirectory, "XE-Local-AI-Engine.HostAgent.Windows.exe");
        if (!File.Exists(hostAgentPath))
        {
            throw new InvalidOperationException($"HostAgent executable was not found at {hostAgentPath}.");
        }

        var workingDirectory = Path.GetDirectoryName(hostAgentPath) ?? AppContext.BaseDirectory;
        WindowsDetachedProcessLauncher.StartDetached(hostAgentPath, workingDirectory);
    }

    private static async Task StartLinuxHostAgentAsync(CancellationToken cancellationToken)
    {
        await RunProcessAsync("systemctl",
            ["--user", "daemon-reload"],
            cancellationToken).ConfigureAwait(true);

        await RunProcessAsync("systemctl",
            ["--user", "start", "xe-host-agent.service"],
            cancellationToken).ConfigureAwait(true);
    }

    private static async Task RunProcessAsync(string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process
        {
            StartInfo = startInfo
        };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Unable to start {fileName}.");
        }

        var errorOutput = new StringBuilder();
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (!string.IsNullOrWhiteSpace(eventArgs.Data))
            {
                errorOutput.AppendLine(eventArgs.Data);
            }
        };
        process.BeginErrorReadLine();

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(true);
        if (process.ExitCode != 0)
        {
            var details = errorOutput.Length == 0 ? $"exit code {process.ExitCode}" : errorOutput.ToString().Trim();
            throw new InvalidOperationException($"HostAgent start command failed: {details}");
        }
    }

    private static Task<bool> ShowConfirmationAsync(string title, string message)
    {
        var completion = new TaskCompletionSource<bool>();
        var window = CreateDialogWindow(title);
        var confirmButton = new Button
        {
            Content = "Confirm",
            MinWidth = 96
        };
        var cancelButton = new Button
        {
            Content = "Cancel",
            MinWidth = 96
        };

        confirmButton.Click += (_, _) => CloseConfirmation(window, completion, true);
        cancelButton.Click += (_, _) => CloseConfirmation(window, completion, false);
        window.Closed += (_, _) => completion.TrySetResult(false);

        window.Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 16,
            Children =
            {
                new TextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 420
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children =
                    {
                        cancelButton,
                        confirmButton
                    }
                }
            }
        };

        window.Show();
        return completion.Task;
    }

    private static void CloseConfirmation(Window window, TaskCompletionSource<bool> completion, bool confirmed)
    {
        completion.TrySetResult(confirmed);
        window.Close();
    }

    private static void ShowTextWindow(string title, string text)
    {
        var window = CreateDialogWindow(title);
        window.Width = 720;
        window.Height = 480;
        window.Content = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(12)
        };
        window.Show();
    }

    private static Window CreateDialogWindow(string title)
    {
        return new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            CanResize = true,
            Topmost = true
        };
    }

    private void QuitTrayMenuItemOnClick(object? sender, EventArgs e)
    {
        Dispose();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }
}
