namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation;

using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Reaping;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     The one kill of a launched sandbox child's process group and leader, shared by every path that ends it: the command's own exits,
///     interactive disposal and the registry's sandbox kill. It runs once; every later caller reads the same outcome.
/// </summary>
/// <remarks>
///     The leader is pinned by pidfd right after the launch, so the group kill still reaches members after the leader died. The lock is a
///     leaf: <c>TerminateState</c> holds the registry lock around it. The group kill is KILL-only with no grace, but the tree walk waits up
///     to 25 ms per stop and 250 ms per child (<c>LinuxProcessTreeKillCalls.TotalBound</c>) under that lock, an accepted residual. Nothing
///     under it awaits, takes another lock or kills a scope.
/// </remarks>
internal sealed class SandboxLaunchTeardown : IDisposable
{
    private readonly ISandboxProcessGroupKiller _killer;
    private readonly LinuxPidFd? _pin;
    private readonly LinuxPidFdOpenStatus _pinStatus;
    private readonly int? _processGroupId;
    private readonly Lock _sync = new();
    private bool _disposed;
    private SandboxGroupKillOutcome? _outcome;

    private SandboxLaunchTeardown(ISandboxChildProcess process,
        ISandboxProcessGroupKiller killer,
        int? processGroupId,
        LinuxPidFd? pin,
        LinuxPidFdOpenStatus pinStatus,
        long? markerStartTicks)
    {
        Process = process;
        _killer = killer;
        _processGroupId = processGroupId;
        _pin = pin;
        _pinStatus = pinStatus;
        MarkerStartTicks = markerStartTicks;
    }

    public ISandboxChildProcess Process { get; }

    /// <summary>The group id a marker may record: set only with <see cref="MarkerStartTicks" />, both read while the child was proven unreaped.</summary>
    public int? MarkerProcessGroupId => MarkerStartTicks is null ? null : _processGroupId;

    /// <summary>The leader's start ticks, read after the pin and before re-checking that the child had not exited, else <see langword="null" />.</summary>
    public long? MarkerStartTicks { get; }

    /// <summary>Pins a just-started child that leads its own group (<paramref name="appliedProcessGroup" />, Linux only) and snapshots its identity.</summary>
    /// <remarks>
    ///     Pin, then start ticks, then the exit check: a child seen unexited after both reads was still this host's unreaped child during
    ///     them, so neither can describe a process that reused its pid. One that already exited keeps its group id for a probe, but no pin
    ///     and no identity a marker could record. Where that proof fails (<paramref name="hostVerifiable" /> false: the host is PID 1 or
    ///     <c>/proc</c> is another namespace's) no pin is taken and no identity recorded; the kill takes the numeric fallback.
    /// </remarks>
    /// <param name="hostVerifiable">Whether this host may trust an owned-child pin; <see cref="LinuxProcessTreeKill.IsHostVerifiable" /> by default.</param>
    public static SandboxLaunchTeardown Create(ISandboxChildProcess process,
        bool appliedProcessGroup,
        ISandboxProcessGroupKiller killer,
        Func<bool>? hostVerifiable = null)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(killer);
        if (!appliedProcessGroup || !OperatingSystem.IsLinux())
        {
            return new SandboxLaunchTeardown(process, killer, processGroupId: null, pin: null, LinuxPidFdOpenStatus.Unsupported, markerStartTicks: null);
        }

        int processId;
        try
        {
            processId = process.Id;
        }
        catch (InvalidOperationException)
        {
            // The process exited before its pid could be read: nothing to signal.
            return new SandboxLaunchTeardown(process, killer, processGroupId: null, pin: null, LinuxPidFdOpenStatus.Gone, markerStartTicks: null);
        }

        if (!(hostVerifiable ?? LinuxProcessTreeKill.IsHostVerifiable)())
        {
            return new SandboxLaunchTeardown(process, killer, processId, pin: null, LinuxPidFdOpenStatus.Unsupported, markerStartTicks: null);
        }

        var pin = killer.PinProcess(processId);
        var startTicks = killer.GetProcessStartTicks(processId);
        if (process.ExitCode is not null)
        {
            pin.Dispose();
            return new SandboxLaunchTeardown(process, killer, processId, pin: null, LinuxPidFdOpenStatus.Gone, markerStartTicks: null);
        }

        if (pin.OpenStatus == LinuxPidFdOpenStatus.Opened)
        {
            return new SandboxLaunchTeardown(process, killer, processId, pin, LinuxPidFdOpenStatus.Opened, startTicks);
        }

        pin.Dispose();
        return new SandboxLaunchTeardown(process, killer, processId, pin: null, pin.OpenStatus, startTicks);
    }

    /// <summary>Kills the group and the leader once and returns that outcome to every caller; never throws.</summary>
    public SandboxGroupKillOutcome Terminate()
    {
        lock (_sync)
        {
            // After disposal the pin is closed and a send would read a synthetic Gone, which must never justify deleting a marker.
            _outcome ??= _disposed ? SandboxGroupKillOutcome.Deferred : Run();
            return _outcome.Value;
        }
    }

    /// <summary>Terminates first, so the outcome is decided while the pin is open, then closes the pin.</summary>
    public void Dispose()
    {
        _ = Terminate();
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _pin?.Dispose();
        }
    }

    private SandboxGroupKillOutcome Run()
    {
        if (_processGroupId is not { } processGroupId)
        {
            _ = Process.Kill();
            return SandboxGroupKillOutcome.Gone;
        }

        try
        {
            return _killer.KillOwnedProcessGroup(_pin, _pinStatus, processGroupId, () => Process.ExitCode is not null, Process.Kill);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // An unverified kill keeps the marker; the leader still gets its own best-effort tree kill, which never throws.
            _ = Process.Kill();
            return SandboxGroupKillOutcome.Deferred;
        }
    }
}
