namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using XE_Local_AI_Engine.Client.Services.Models;

public interface IBenchmarkInstalledModelLease : IAsyncDisposable
{
    InstalledModelSnapshot Snapshot { get; }
}

public interface IBenchmarkInstalledModelLeaseProvider
{
    Task<IBenchmarkInstalledModelLease> AcquireAsync(string modelName, CancellationToken cancellationToken);
}

internal sealed class BenchmarkInstalledModelLeaseProvider(IInstalledModelSnapshotCoordinator coordinator) : IBenchmarkInstalledModelLeaseProvider
{
    private readonly IInstalledModelSnapshotCoordinator _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    public async Task<IBenchmarkInstalledModelLease> AcquireAsync(string modelName, CancellationToken cancellationToken)
    {
        var lease = await _coordinator.AcquireReadSnapshotAsync(modelName, cancellationToken).ConfigureAwait(false);
        return new Lease(lease);
    }

    private sealed class Lease(InstalledModelReadLease inner) : IBenchmarkInstalledModelLease
    {
        private readonly InstalledModelReadLease _inner = inner;
        public InstalledModelSnapshot Snapshot => _inner.Snapshot;

        public ValueTask DisposeAsync() =>
            _inner.DisposeAsync();
    }
}
