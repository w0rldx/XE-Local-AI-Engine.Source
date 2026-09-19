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
        await using var lease = await AcquireAsync(modelName, cancellationToken);
        var snapshot = lease.Snapshot;
        return new InstalledModelFacts
        {
            ModelName = snapshot.ModelName,
            ProviderName = snapshot.ProviderName ?? string.Empty,
            Role = snapshot.Role,
            Origin = snapshot.Origin,
            ModelContentFingerprint = snapshot.ModelContentFingerprint
        };
    }
}

internal sealed class BenchmarkInstalledModelLeaseProvider : IBenchmarkInstalledModelLeaseProvider
{
    private readonly IInstalledModelSnapshotCoordinator _coordinator;

    public BenchmarkInstalledModelLeaseProvider(IInstalledModelSnapshotCoordinator coordinator)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        _coordinator = coordinator;
    }

    public async Task<IBenchmarkInstalledModelLease> AcquireAsync(string modelName, CancellationToken cancellationToken)
    {
        var lease = await _coordinator.AcquireReadSnapshotAsync(modelName, cancellationToken);
        return new Lease(lease);
    }

    public Task<InstalledModelFacts?> ReadFactsAsync(string modelName, CancellationToken cancellationToken) =>
        _coordinator.ReadFactsAsync(modelName, cancellationToken);

    private sealed class Lease : IBenchmarkInstalledModelLease
    {
        private readonly InstalledModelReadLease _inner;

        public Lease(InstalledModelReadLease inner)
        {
            _inner = inner;
        }

        public InstalledModelSnapshot Snapshot => _inner.Snapshot;

        public ValueTask DisposeAsync() =>
            _inner.DisposeAsync();
    }
}
