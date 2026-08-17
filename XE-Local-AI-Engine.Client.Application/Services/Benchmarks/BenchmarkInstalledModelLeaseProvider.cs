namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using XE_Local_AI_Engine.Client.Services.Models;

public interface IBenchmarkInstalledModelLeaseProvider
{
    /// <summary>
    ///     Acquires the VERIFIED installed-model snapshot under a read lease — every member file is re-hashed. This is
    ///     the run-freeze read; a catalog listing wants <see cref="ReadFactsAsync" /> instead.
    /// </summary>
    Task<IBenchmarkInstalledModelLease> AcquireAsync(string modelName, CancellationToken cancellationToken);

    /// <summary>
    ///     Reads the registry-recorded facts without hashing anything, or <see langword="null" /> when the model is not
    ///     installed. The default body verifies (so a test double needs no extra member); the real provider overrides
    ///     it with the cheap read.
    /// </summary>
    async Task<InstalledModelFacts?> ReadFactsAsync(string modelName, CancellationToken cancellationToken)
    {
        await using var lease = await AcquireAsync(modelName, cancellationToken).ConfigureAwait(false);
        var snapshot = lease.Snapshot;
        return new InstalledModelFacts(snapshot.ModelName,
            snapshot.ProviderName ?? string.Empty,
            snapshot.Role,
            snapshot.Origin,
            snapshot.ModelContentFingerprint);
    }
}

internal sealed class BenchmarkInstalledModelLeaseProvider(IInstalledModelSnapshotCoordinator coordinator) : IBenchmarkInstalledModelLeaseProvider
{
    private readonly IInstalledModelSnapshotCoordinator _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    public async Task<IBenchmarkInstalledModelLease> AcquireAsync(string modelName, CancellationToken cancellationToken)
    {
        var lease = await _coordinator.AcquireReadSnapshotAsync(modelName, cancellationToken).ConfigureAwait(false);
        return new Lease(lease);
    }

    public Task<InstalledModelFacts?> ReadFactsAsync(string modelName, CancellationToken cancellationToken) =>
        _coordinator.ReadFactsAsync(modelName, cancellationToken);

    private sealed class Lease(InstalledModelReadLease inner) : IBenchmarkInstalledModelLease
    {
        private readonly InstalledModelReadLease _inner = inner;
        public InstalledModelSnapshot Snapshot => _inner.Snapshot;

        public ValueTask DisposeAsync() =>
            _inner.DisposeAsync();
    }
}
