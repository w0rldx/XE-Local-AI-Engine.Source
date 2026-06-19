namespace XE_Local_AI_Engine.Client.Hosting;

using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
///     Desktop-mode host lifecycle: routes a closed console window into a graceful application stop (so the singleton
///     <c>LlamaServerProcessSupervisor</c> disposes and tree-kills its child — no orphan), and auto-opens the default
///     browser at the bound loopback URL once the host has started.
///     <para>
///         Two OS gaps are filled (the rest — SIGINT/SIGTERM/SIGQUIT — are already handled by ConsoleLifetime):
///         <list type="bullet">
///             <item>Linux <c>SIGHUP</c> (terminal close): a <see cref="PosixSignalRegistration" /> calls
///             <see cref="IHostApplicationLifetime.StopApplication" />.</item>
///             <item>Windows <c>CTRL_CLOSE_EVENT</c> (console-window close): a <c>SetConsoleCtrlHandler</c> handler runs
///             on a separate OS thread, calls <c>StopApplication</c>, then BLOCKS until the host has stopped (or a
///             sub-5s budget elapses) — returning early would let Windows force-kill the process before the drain runs.
///             The Job Object remains the hard-kill safety net regardless.</item>
///         </list>
///     </para>
///     Only activated in desktop mode (invariant #1: off-flag installs nothing).
/// </summary>
internal sealed class DesktopLifecycle : IDisposable
{
    /// <summary>
    ///     Windows force-kills the process when a CTRL_CLOSE handler returns or after ~5s. Stay safely under that so the
    ///     drain has a chance to run before the OS pulls the plug.
    /// </summary>
    private static readonly TimeSpan ConsoleCloseDrainBudget = TimeSpan.FromMilliseconds(4000);

    private readonly IHostApplicationLifetime _lifetime;
    private readonly IServer _server;
    private readonly ILogger<DesktopLifecycle> _logger;
    private readonly Func<string?> _browserOpener;

    // Rooted on the instance (which is itself rooted via the lifetime registration in Program.cs) so the GC cannot
    // collect the native callback delegate while Windows holds the function pointer.
    private NativeConsoleCtrlHandler? _consoleCtrlHandlerDelegate;
    private PosixSignalRegistration? _sigHupRegistration;
    private CancellationTokenRegistration _startedRegistration;
    private bool _disposed;

    internal DesktopLifecycle(
        IHostApplicationLifetime lifetime,
        IServer server,
        ILogger<DesktopLifecycle> logger,
        Func<string?>? browserOpener = null)
    {
        _lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
        _server = server ?? throw new ArgumentNullException(nameof(server));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Returns the resolved URL it attempted to open, or null when no usable address was found. Overridable for tests.
        _browserOpener = browserOpener ?? OpenDefaultBrowser;
    }

    /// <summary>
    ///     Installs the OS-specific console-close triggers and the on-started browser launch. Idempotent per instance.
    /// </summary>
    internal void Activate()
    {
        RegisterConsoleCloseTriggers();
        _startedRegistration = _lifetime.ApplicationStarted.Register(OnApplicationStarted);
    }

    /// <summary>
    ///     The graceful-stop trigger invoked by the OS signal/console-ctrl handlers. Exposed as an internal seam so tests
    ///     can drive it without a real signal: it must call <see cref="IHostApplicationLifetime.StopApplication" />.
    /// </summary>
    internal void TriggerGracefulStop()
    {
        try
        {
            _logger.LogInformation("Console close detected; requesting graceful application shutdown.");
            _lifetime.StopApplication();
        }
        catch (Exception exception)
        {
            // Never throw across a native signal/console boundary.
            _logger.LogError(exception, "Failed to request graceful shutdown after console close.");
        }
    }

    private void RegisterConsoleCloseTriggers()
    {
        if (OperatingSystem.IsLinux())
        {
            // SIGHUP fires on controlling-terminal close. Cancel=true suppresses the default terminate so our graceful
            // StopApplication runs first and the supervisor disposes its child.
            _sigHupRegistration = PosixSignalRegistration.Create(PosixSignal.SIGHUP, context =>
            {
                context.Cancel = true;
                TriggerGracefulStop();
            });
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            RegisterWindowsConsoleCtrlHandler();
        }
    }

    private void RegisterWindowsConsoleCtrlHandler()
    {
        // Keep the delegate rooted on this instance; SetConsoleCtrlHandler stores the raw function pointer and the GC
        // must not reclaim the managed callback for the life of the process.
        _consoleCtrlHandlerDelegate = HandleConsoleCtrl;
        if (!SetConsoleCtrlHandler(_consoleCtrlHandlerDelegate, add: true))
        {
            _logger.LogWarning("SetConsoleCtrlHandler registration failed; console-window close may hard-kill the host.");
        }
    }

    private bool HandleConsoleCtrl(uint ctrlType)
    {
        // Handle the events that mean "the console window is going away". Other events (CTRL_C / CTRL_BREAK) are left to
        // ConsoleLifetime, which already drives graceful shutdown for them.
        if (ctrlType is not (CtrlCloseEvent or CtrlLogoffEvent or CtrlShutdownEvent))
        {
            return false;
        }

        // This handler runs on a separate OS thread; Windows force-kills the process when it returns (or after ~5s).
        // So: request the stop, then BLOCK until the host has stopped or the budget elapses. Returning early aborts the
        // drain — the Job Object then hard-kills the child tree (still no orphan), but we prefer the graceful path.
        try
        {
            TriggerGracefulStop();

            using var stopped = new ManualResetEventSlim(initialState: false, spinCount: 0);
            using var registration = _lifetime.ApplicationStopped.Register(stopped.Set);
            stopped.Wait(ConsoleCloseDrainBudget);
        }
        catch (Exception exception)
        {
            // Never let an exception cross the native boundary.
            _logger.LogError(exception, "Console-close handler failed during graceful drain.");
        }

        // Returning true marks the event handled; the process is terminated by the OS afterwards regardless.
        return true;
    }

    private void OnApplicationStarted()
    {
        try
        {
            var url = _browserOpener();
            if (url is null)
            {
                _logger.LogWarning("Desktop mode started but no loopback HTTP address was resolved; not opening a browser.");
            }
        }
        catch (Exception exception)
        {
            // Browser launch is strictly non-fatal — never abort startup.
            _logger.LogError(exception, "Unexpected failure while attempting to open the desktop browser.");
        }
    }

    private string? OpenDefaultBrowser()
    {
        var addresses = _server.Features.Get<IServerAddressesFeature>()?.Addresses;
        if (addresses is null)
        {
            return null;
        }

        var url = LoopbackUrlResolver.Resolve(addresses);
        if (url is null)
        {
            return null;
        }

        BrowserLauncher.OpenBrowser(url, OperatingSystem.IsWindows(), _logger, BrowserLauncher.StartProcess);
        return url;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _sigHupRegistration?.Dispose();
        _startedRegistration.Dispose();

        if (OperatingSystem.IsWindows() && _consoleCtrlHandlerDelegate is not null)
        {
            // Best-effort unregister; ignore the result during teardown.
            _ = SetConsoleCtrlHandler(_consoleCtrlHandlerDelegate, add: false);
            _consoleCtrlHandlerDelegate = null;
        }
    }

    // Win32 console control event codes (see learn.microsoft.com/windows/console/handlerroutine).
    private const uint CtrlCloseEvent = 2;
    private const uint CtrlLogoffEvent = 5;
    private const uint CtrlShutdownEvent = 6;

    private delegate bool NativeConsoleCtrlHandler(uint ctrlType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleCtrlHandler(NativeConsoleCtrlHandler? handlerRoutine, [MarshalAs(UnmanagedType.Bool)] bool add);
}
