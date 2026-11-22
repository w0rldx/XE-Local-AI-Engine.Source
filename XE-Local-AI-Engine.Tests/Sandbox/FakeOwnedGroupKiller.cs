namespace XE_Local_AI_Engine.Tests.Sandbox;

using System.Globalization;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Reaping;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     The owned half of <see cref="ISandboxProcessGroupKiller" />, scripted: it hands out made-up pins, reports scripted start ticks and a
///     scripted outcome, and records every call into a log it may share with a <see cref="FakeSandboxChildProcess" />.
/// </summary>
internal sealed class FakeOwnedGroupKiller : ISandboxProcessGroupKiller
{
    private int _ownedKills;

    public FakeOwnedGroupKiller(List<string>? calls = null)
    {
        Calls = calls ?? [];
    }

    public List<string> Calls { get; }

    public LinuxPidFdOpenStatus PinStatus { get; init; } = LinuxPidFdOpenStatus.Opened;

    public long? StartTicks { get; init; } = 99;

    public SandboxGroupKillOutcome Outcome { get; init; } = SandboxGroupKillOutcome.Killed;

    /// <summary>Runs after the start ticks were read: a test makes the child exit there.</summary>
    public Action? AfterTicksRead { get; init; }

    /// <summary>Runs inside the owned kill before the tree kill: a test parks a caller there.</summary>
    public Action? DuringOwnedKill { get; init; }

    public List<LinuxPidFd> Pins { get; } = [];

    public int OwnedKills => Volatile.Read(ref _ownedKills);

    /// <summary>The pin the last owned kill was handed, null when none.</summary>
    public LinuxPidFd? LastPin { get; private set; }

    public long? GetProcessStartTicks(int processId)
    {
        Calls.Add(string.Create(CultureInfo.InvariantCulture, $"ticks {processId}"));
        AfterTicksRead?.Invoke();
        return StartTicks;
    }

    public bool IsProcessAlive(int processId) =>
        false;

    public Task<SandboxGroupKillOutcome> KillProcessGroupAsync(int processGroupId, long expectedStartTicks, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("An owned child is never killed through the foreign path.");

    public LinuxPidFd PinProcess(int processId)
    {
        Calls.Add(string.Create(CultureInfo.InvariantCulture, $"pin {processId}"));
        // A made-up fd the pin never owns, so disposing it closes nothing real.
        var pin = new LinuxPidFd(PinStatus == LinuxPidFdOpenStatus.Opened ? 4096 : -1, ownsHandle: false, PinStatus);
        Pins.Add(pin);
        return pin;
    }

    public SandboxGroupKillOutcome KillOwnedProcessGroup(LinuxPidFd? leaderPin,
        LinuxPidFdOpenStatus pinStatus,
        int processGroupId,
        Func<bool> leaderExited,
        Func<bool> killTree)
    {
        _ = Interlocked.Increment(ref _ownedKills);
        LastPin = leaderPin;
        Calls.Add(string.Create(CultureInfo.InvariantCulture, $"group {processGroupId} {pinStatus}{(leaderPin is null ? " unpinned" : "")}"));
        DuringOwnedKill?.Invoke();
        _ = killTree();
        return Outcome;
    }
}
