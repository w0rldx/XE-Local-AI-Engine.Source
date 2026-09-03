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
    private int _calls;

    /// <summary>What every collection answers. Null is the "nothing to report" arm, which is also the no-collector arm.</summary>
    public DevWorkflowNodeTelemetry? Answer { get; set; }

    /// <summary>Thrown instead of answering, for the containment assertion.</summary>
    public Exception? Fault { get; set; }

    /// <summary>How long the collection takes. Longer than the decorator's deadline is the hang case.</summary>
    public TimeSpan Delay { get; set; }

    public int Calls => Volatile.Read(ref _calls);

    /// <summary>
    ///     The deadline token each collection was handed. Two tokens compare equal when they come from the same source,
    ///     so this is how a test tells one budget shared across a route from one budget per command.
    /// </summary>
    public System.Collections.Concurrent.ConcurrentQueue<CancellationToken> Deadlines { get; } = new();

    public async Task<DevWorkflowNodeTelemetry?> CollectAsync(DevWorkflowNodeRunSnapshot nodeRun,
        DevWorkflowNodeRunStatus targetStatus,
        CancellationToken cancellationToken)
    {
        _ = Interlocked.Increment(ref _calls);
        Deadlines.Enqueue(cancellationToken);
        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay, cancellationToken).ConfigureAwait(false);
        }

        return Fault is null ? Answer : throw Fault;
    }
}
