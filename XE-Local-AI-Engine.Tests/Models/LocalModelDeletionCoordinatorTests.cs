namespace XE_Local_AI_Engine.Tests.Models;

using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.HuggingFace.Implementation;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Tests.Providers.HuggingFace;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class LocalModelDeletionCoordinatorTests
{
    [Test]
    public async Task CommitThenPurge_RemovesRegistryMapAndOperationArtifacts()
    {
        await using var context = await CreateContextAsync().ConfigureAwait(false);

        var committed = await context.Coordinator.CommitDeleteAsync(context.ModelName, CancellationToken.None).ConfigureAwait(false);

        AssertEx.False(File.Exists(context.WeightPath));
        AssertEx.Null(await context.Registry.FindAsync(context.ModelName, CancellationToken.None).ConfigureAwait(false));
        AssertEx.False(context.MapStore.HasMapping(context.ModelName));
        var journal = Path.Combine(context.Directory.Path, ".operations", "delete", committed.OperationId.ToString("N"), "journal.json");
        AssertEx.True(File.Exists(journal));

        await context.Coordinator.PurgeAfterSuccessAsync(committed, CancellationToken.None).ConfigureAwait(false);

        AssertEx.False(File.Exists(journal));
        AssertEx.False(File.Exists(Path.Combine(context.Directory.Path, committed.StageReceipt.StagedMembers.Single().QuarantineRelativePath)));
        context.ProviderResolver.Received().InvalidateModelProviderMap();
    }

    [Test]
    public async Task CacheInvalidationFailure_RestoresRegistryMapAndMember()
    {
        await using var context = await CreateContextAsync().ConfigureAwait(false);
        var calls = 0;
        context.ProviderResolver.When(static resolver => resolver.InvalidateModelProviderMap()).Do(_ =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                throw new InvalidOperationException("injected cache failure");
            }
        });

        _ = await AssertEx.ThrowsAsync<InvalidOperationException>(() =>
                              context.Coordinator.CommitDeleteAsync(context.ModelName, CancellationToken.None))
                          .ConfigureAwait(false);

        AssertEx.True(File.Exists(context.WeightPath));
        AssertEx.NotNull(await context.Registry.FindAsync(context.ModelName, CancellationToken.None).ConfigureAwait(false));
        AssertEx.True(context.MapStore.HasMapping(context.ModelName));
        var deleteRoot = Path.Combine(context.Directory.Path, ".operations", "delete");
        AssertEx.False(Directory.Exists(deleteRoot) && Directory.EnumerateFiles(deleteRoot, "journal.json", SearchOption.AllDirectories).Any());
    }

    [Test]
    public async Task ReconcileCommittedJournal_PurgesAfterSimulatedResponseCrash()
    {
        await using var context = await CreateContextAsync().ConfigureAwait(false);
        var committed = await context.Coordinator.CommitDeleteAsync(context.ModelName, CancellationToken.None).ConfigureAwait(false);
        var quarantine = Path.Combine(context.Directory.Path, committed.StageReceipt.StagedMembers.Single().QuarantineRelativePath);
        AssertEx.True(File.Exists(quarantine));

        await ((ILocalModelDeletionJournalReconciler)context.Coordinator).ReconcileAsync(CancellationToken.None).ConfigureAwait(false);
        await ((ILocalModelDeletionJournalReconciler)context.Coordinator).ReconcileAsync(CancellationToken.None).ConfigureAwait(false);

        AssertEx.False(File.Exists(quarantine));
        var deleteRoot = Path.Combine(context.Directory.Path, ".operations", "delete");
        AssertEx.False(Directory.Exists(deleteRoot) && Directory.EnumerateFiles(deleteRoot, "journal.json", SearchOption.AllDirectories).Any());
    }

    private static async Task<TestContext> CreateContextAsync()
    {
#pragma warning disable CA2000 // Ownership of both fixtures is transferred to the returned async-disposable TestContext.
        var directory = new GgufStoreTestInfrastructure.TempModelsDir();
#pragma warning restore CA2000
        try
        {
            var options = GgufStoreTestInfrastructure.Options(directory.Path);
#pragma warning disable CA2000 // Ownership is transferred to the returned async-disposable TestContext.
            var registry = GgufStoreTestInfrastructure.Registry(options);
#pragma warning restore CA2000
            var weightPath = Path.Combine(directory.Path, "coordinator-Q4_K_M.gguf");
            var bytes = new byte[]
            {
                1,
                3,
                3,
                7
            };
            await File.WriteAllBytesAsync(weightPath, bytes).ConfigureAwait(false);
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            const string modelName = "local/coordinator:Q4_K_M";
            await registry.UpsertAsync(new GgufModelRegistryEntry
            {
                ModelName = modelName,
                RepoId = "local/coordinator",
                FileName = Path.GetFileName(weightPath),
                Quant = "Q4_K_M",
                LocalPath = weightPath,
                SizeBytes = bytes.Length,
                Sha256 = hash,
                SourceRevision = "revision",
                DownloadedAtUtc = DateTimeOffset.UnixEpoch,
                Role = GgufRole.Chat
            }, CancellationToken.None).ConfigureAwait(false);
            var mapStore = new TestProviderMapStore();
            mapStore.Seed(modelName);
            var snapshotStore = new InstalledGgufSnapshotStore(registry, options);
            var snapshotCoordinator = new InstalledModelSnapshotCoordinator(new KeyedCompositeLockDomain(), snapshotStore, mapStore);
            var providerResolver = Substitute.For<ILocalModelProviderResolver>();
            var coordinator = new LocalModelDeletionCoordinator(snapshotCoordinator,
                new InstalledGgufDeletionStore(registry, options),
                mapStore,
                providerResolver,
                options,
                NullLogger<LocalModelDeletionCoordinator>.Instance);
            return new TestContext(directory, registry, mapStore, providerResolver, coordinator, modelName, weightPath);
        }
        catch
        {
            directory.Dispose();
            throw;
        }
    }

    private sealed class TestProviderMapStore : ICoordinatedModelProviderMapStore
    {
        private readonly Dictionary<string, ModelProviderMapRecord> _rows = new(StringComparer.OrdinalIgnoreCase);

        public void Seed(string modelName) =>
            _rows[modelName] = new ModelProviderMapRecord(modelName, LlamaServerProviderConstants.ProviderName, 1, "revision-1");

        public bool HasMapping(string modelName) =>
            _rows.ContainsKey(modelName);

        public Task<ModelProviderMapRecord?> ReadWithRevisionAsync(IModelProviderMapReadLease lease,
            string modelName,
            CancellationToken cancellationToken = default)
        {
            Validate(lease, modelName, mutation: false);
            return Task.FromResult(_rows.GetValueOrDefault(modelName));
        }

        public Task<ProviderMapClaimResult> TryClaimLlamaCppAsync(IModelProviderMapMutationLease lease,
            string modelName,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ProviderMapMutationResult> TryUpsertAsync(IModelProviderMapMutationLease lease,
            string modelName,
            string providerName,
            string? expectedRevision = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ProviderMapRestoreResult> TryRestoreAsync(IModelProviderMapMutationLease lease,
            ProviderMapMutationReceipt receipt,
            CancellationToken cancellationToken = default)
        {
            Validate(lease, receipt.ModelName, mutation: true);
            if (_rows.ContainsKey(receipt.ModelName) || receipt.Prior is null)
            {
                return Task.FromResult(ProviderMapRestoreResult.Superseded);
            }

            _rows[receipt.ModelName] = receipt.Prior;
            return Task.FromResult(ProviderMapRestoreResult.Restored);
        }

        public Task<ProviderMapRemovalResult> TryRemoveIfMatchAsync(IModelProviderMapMutationLease lease,
            string modelName,
            string expectedProvider,
            string expectedRevision,
            CancellationToken cancellationToken = default)
        {
            Validate(lease, modelName, mutation: true);
            if (!_rows.TryGetValue(modelName, out var current))
            {
                return Task.FromResult<ProviderMapRemovalResult>(new ProviderMapRemovalResult.Absent());
            }

            if (!string.Equals(current.ProviderName, expectedProvider, StringComparison.Ordinal)
                || !string.Equals(current.Revision, expectedRevision, StringComparison.Ordinal))
            {
                return Task.FromResult<ProviderMapRemovalResult>(new ProviderMapRemovalResult.Superseded(current));
            }

            _rows.Remove(modelName);
            return Task.FromResult<ProviderMapRemovalResult>(new ProviderMapRemovalResult.Removed(new ProviderMapMutationReceipt(modelName, current, Mutation: null, WasRemoval: true)));
        }

        private static void Validate(IModelProviderMapReadLease lease, string modelName, bool mutation)
        {
            if (lease.IsDisposed || !lease.ContainsModel(modelName) || mutation && !lease.IsMutation)
            {
                throw new InvalidOperationException("A matching live coordination lease is required.");
            }
        }
    }

    private sealed record TestContext(
        GgufStoreTestInfrastructure.TempModelsDir Directory,
        GgufModelRegistry Registry,
        TestProviderMapStore MapStore,
        ILocalModelProviderResolver ProviderResolver,
        LocalModelDeletionCoordinator Coordinator,
        string ModelName,
        string WeightPath) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            Registry.Dispose();
            Directory.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
