namespace XE_Local_AI_Engine.Client.Services.Training.Runs;

using System.Collections.Concurrent;

/// <summary>
///     The live-run cancellation registry. A singleton because the executor registers into it from a background service
///     scope while the cancel endpoint reads it from a request scope — the benchmark cancellation-registry shape.
/// </summary>
public sealed class TrainingRunCancellationRegistry
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _inFlight = new();

    public IDisposable Register(Guid runId, CancellationTokenSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _inFlight[runId] = source;
        return new Registration(this, runId);
    }

    public bool Cancel(Guid runId)
    {
        if (!_inFlight.TryGetValue(runId, out var source))
        {
            return false;
        }

        try
        {
            source.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            // The run terminalized between the lookup and the signal; nothing left to cancel.
            return false;
        }
    }

    public bool IsRunning(Guid runId) =>
        _inFlight.ContainsKey(runId);

    private sealed class Registration(TrainingRunCancellationRegistry owner, Guid runId) : IDisposable
    {
        public void Dispose() =>
            owner._inFlight.TryRemove(runId, out _);
    }
}
