namespace XE_Local_AI_Engine.Tests.DevWorkflows;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;

/// <summary>
///     A telemetry source a test drives directly: it answers what it is told to, throws what it is told to, or takes
///     longer than the decorator will wait. Hand-written rather than substituted because the interface is internal to
///     the application assembly, which the dynamic-proxy generator cannot see.
/// </summary>
internal sealed class StubDevWorkflowNodeTelemetrySource : IDevWorkflowNodeTelemetrySource
{
    private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _allEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _calls;

    /// <summary>What every collection answers. Null is the "nothing to report" arm, which is also the no-collector arm.</summary>
    public DevWorkflowNodeTelemetry? Answer { get; set; }

    /// <summary>Thrown instead of answering, for the containment assertion.</summary>
    public Exception? Fault { get; set; }

    /// <summary>How long the collection takes. Longer than the decorator's deadline is the hang case.</summary>
    public TimeSpan Delay { get; set; }

    /// <summary>
    ///     A collection that IGNORES its cancellation token: it waits on this source, and nothing but the test
    ///     completing it lets it return. The deadline the decorator hands down never releases it, so a decorator that
    ///     merely REQUESTS cancellation would wait here forever — which is the whole point of the gate.
    /// </summary>
    public TaskCompletionSource? IgnoresCancellationUntil { get; set; }

    public int Calls => Volatile.Read(ref _calls);

    /// <summary>Completes when the LAST collection returned, so a test can wait for abandoned work to land.</summary>
    public Task Finished => _finished.Task;

    /// <summary>
    ///     How many collections a test expects to ENTER the collector. The decorator starts each one on the thread
    ///     pool, so a reset is offered eventually rather than by the time the route's own call returns — set this
    ///     before the run and await <see cref="AllEntered" /> instead of racing it.
    /// </summary>
    public int ExpectedEntries { get; set; }

    /// <summary>Completes once <see cref="ExpectedEntries" /> collections have entered.</summary>
    public Task AllEntered => _allEntered.Task;

    /// <summary>
    ///     The deadline token each collection was handed. Two tokens compare equal when they come from the same source,
    ///     so this is how a test tells one budget shared across a route from one budget per command.
    /// </summary>
    public System.Collections.Concurrent.ConcurrentQueue<CancellationToken> Deadlines { get; } = new();

    public async Task<DevWorkflowNodeTelemetry?> CollectAsync(DevWorkflowNodeRunSnapshot nodeRun,
        DevWorkflowNodeRunStatus targetStatus,
        CancellationToken cancellationToken)
    {
        var entered = Interlocked.Increment(ref _calls);
        Deadlines.Enqueue(cancellationToken);
        if (ExpectedEntries > 0 && entered >= ExpectedEntries)
        {
            _ = _allEntered.TrySetResult();
        }

        try
        {
            if (IgnoresCancellationUntil is { } gate)
            {
                // Deliberately not passed the token: this is the collector the decorator cannot ask to stop.
                await gate.Task.ConfigureAwait(false);
            }

            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, cancellationToken).ConfigureAwait(false);
            }

            return Fault is null ? Answer : throw Fault;
        }
        finally
        {
            _ = _finished.TrySetResult();
        }
    }
}
