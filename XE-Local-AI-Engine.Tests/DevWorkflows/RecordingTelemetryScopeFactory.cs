namespace XE_Local_AI_Engine.Tests.DevWorkflows;

using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;

/// <summary>
///     The scope factory the publishing store opens a cost collection on, with the two services that collection asks
///     for and a count of the scopes it took and gave back.
///     <para>
///         Hand-written rather than a real container: the point under test is that the collection gets a scope of its
///         OWN and holds it until it finishes — including past the deadline that abandoned it — and the counts are how
///         a test says that. Both services are internal to the application assembly, which rules out a substitute.
///     </para>
/// </summary>
internal sealed class RecordingTelemetryScopeFactory : IServiceScopeFactory
{
    private readonly IDevWorkflowStore _reads;
    private readonly IDevWorkflowNodeTelemetrySource _telemetry;
    private int _created;
    private int _disposed;

    public RecordingTelemetryScopeFactory(IDevWorkflowStore reads, IDevWorkflowNodeTelemetrySource telemetry)
    {
        _reads = reads;
        _telemetry = telemetry;
    }

    /// <summary>How many collections opened a scope. One per settle, one per enriched reset on a retry route.</summary>
    public int Created => Volatile.Read(ref _created);

    /// <summary>How many gave it back. A collection still running has not, which is the abandoned-work assertion.</summary>
    public int Disposed => Volatile.Read(ref _disposed);

    public IServiceScope CreateScope()
    {
        _ = Interlocked.Increment(ref _created);
        return new Scope(this);
    }

    private sealed class Scope : IServiceScope, IServiceProvider
    {
        private readonly RecordingTelemetryScopeFactory _owner;

        public Scope(RecordingTelemetryScopeFactory owner) => _owner = owner;

        public IServiceProvider ServiceProvider => this;

        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(IDevWorkflowStore))
            {
                return _owner._reads;
            }

            return serviceType == typeof(IDevWorkflowNodeTelemetrySource) ? _owner._telemetry : null;
        }

        public void Dispose() => Interlocked.Increment(ref _owner._disposed);
    }
}
