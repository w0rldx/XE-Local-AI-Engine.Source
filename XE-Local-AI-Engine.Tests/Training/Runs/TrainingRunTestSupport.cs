namespace XE_Local_AI_Engine.Tests.Training.Runs;

using System.Runtime.CompilerServices;
using System.Threading.Channels;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Training.Contracts;

/// <summary>Shared fakes for the run-executor suite. No GPU, no venv, no real subprocess.</summary>
internal sealed class FixedNodeDataDirectory(string root) : INodeDataDirectory
{
    public string Root { get; } = root;
}

/// <summary>A key holder with fixed material, so the frozen-copy round trip exercises the real AES-GCM path.</summary>
internal sealed class FixedNodeSqliteKeyHolder(byte[] key) : INodeSqliteKeyHolder
{
    private byte[]? _key = key;

    public ReadOnlyMemory<byte> Key
    {
        get
        {
            ObjectDisposedException.ThrowIf(_key is null, this);
            return _key;
        }
    }

    public void Dispose() =>
        _key = null;
}

/// <summary>
///     A scripted trainer. Lines are handed to the reader in order; the process "exits" with the scripted status once
///     the caller stops reading, and a stop or kill is recorded rather than signalled.
///     <para>
///         With <paramref name="exitsOnStreamClose" /> false the exit task is never settled, which models the one
///         shape a scripted handle otherwise cannot: a trainer that closes its output and then wedges. Nothing —
///         not the closed stream, not a kill, not disposal — reaps it, so a caller that waits on the status without a
///         bound waits forever.
///     </para>
/// </summary>
internal sealed class FakeTrainingProcessHandle(
    TrainingLaunchReceipt receipt,
    IReadOnlyList<string> lines,
    int exitCode,
    bool exitsOnStreamClose = true)
    : ITrainingProcessHandle
{
    private readonly Channel<string> _output = CreateChannel(lines);
    private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _exitWaitEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TrainingLaunchReceipt Receipt { get; } = receipt;

    public bool StopRequested { get; private set; }

    public bool Killed { get; private set; }

    public bool Disposed { get; private set; }

    /// <summary>When set, the scripted lines are withheld so the run looks silent to the watchdog.</summary>
    public static FakeTrainingProcessHandle Silent(TrainingLaunchReceipt receipt) =>
        new(receipt, [], exitCode: 0);

    public IAsyncEnumerable<string> ReadOutputAsync(CancellationToken cancellationToken) =>
        ReadAsync(cancellationToken);

    /// <summary>
    ///     Completes the moment the executor starts waiting for the process to exit. That is the gate a test needs
    ///     before it moves the clock onto the exit bound: by then the executor's <c>finally</c> has already joined the
    ///     watchdog, so the only timer that can be armed is the exit grace, and an advance cannot land on a watchdog
    ///     poll that is still pending.
    /// </summary>
    public Task ExitWaitEntered => _exitWaitEntered.Task;

    /// <summary>Settles the exit status by hand, for a test that decides when the process goes away.</summary>
    public void SignalExit() =>
        _ = _exit.TrySetResult(exitCode);

    public Task<int> WaitForExitAsync(CancellationToken cancellationToken)
    {
        _ = _exitWaitEntered.TrySetResult();
        return _exit.Task.WaitAsync(cancellationToken);
    }

    public void RequestStop()
    {
        StopRequested = true;
        Complete();
    }

    public void KillGroup()
    {
        Killed = true;
        Complete();
    }

    public void Dispose()
    {
        Disposed = true;
        Complete();
    }

    private void Complete()
    {
        _ = _output.Writer.TryComplete();
        if (exitsOnStreamClose)
        {
            _ = _exit.TrySetResult(exitCode);
        }
    }

    private async IAsyncEnumerable<string> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var line in _output.Reader.ReadAllAsync(cancellationToken))
        {
            yield return line;
        }

        // The real handle's stream closes when the child closes both pipes, which is what settles the exit task —
        // unless the child is wedged and never reaches its own exit, which is what the flag models.
        if (exitsOnStreamClose)
        {
            _ = _exit.TrySetResult(exitCode);
        }
    }

    private static Channel<string> CreateChannel(IReadOnlyList<string> lines)
    {
        var channel = Channel.CreateUnbounded<string>();
        foreach (var line in lines)
        {
            _ = channel.Writer.TryWrite(line);
        }

        if (lines.Count > 0)
        {
            // A scripted trainer that has something to say says it and exits, closing both pipes. One with NO lines
            // stays open instead — that is how a wedged or cancellable run is modelled, and what the watchdog and the
            // cooperative-stop tests need.
            _ = channel.Writer.TryComplete();
        }

        return channel;
    }
}

internal sealed class FakeTrainingProcessSpawner(FakeTrainingProcessHandle handle) : ITrainingProcessSpawner
{
    public TrainingSpawnRequest? LastRequest { get; private set; }

    public ITrainingProcessHandle Spawn(TrainingSpawnRequest request)
    {
        LastRequest = request;
        return handle;
    }
}

/// <summary>A /proc reader with scripted facts, so receipt validation is testable without a live process.</summary>
internal sealed class FakeTrainingProcessInspector(TrainingProcessFacts? facts) : ITrainingProcessInspector
{
    public List<int> SignalledGroups { get; } = [];

    public TrainingProcessFacts? Inspect(int processId) =>
        facts;

    public Task KillProcessGroupAsync(int processGroupId, CancellationToken cancellationToken = default)
    {
        SignalledGroups.Add(processGroupId);
        return Task.CompletedTask;
    }
}
