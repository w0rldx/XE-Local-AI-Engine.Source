namespace XE_Local_AI_Engine.Client.Services.Models;

using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer;

public interface IInstalledModelSnapshotCoordinator
{
    Task<InstalledModelReadLease> AcquireReadSnapshotAsync(string modelName, CancellationToken cancellationToken = default);
    Task<InstalledModelMutationLease> AcquireMutationAsync(InstalledModelMutationRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Reads the recorded facts for one installed model WITHOUT verifying its content, or <see langword="null" />
    ///     when the model is not installed.
    /// </summary>
    Task<InstalledModelFacts?> ReadFactsAsync(string modelName, CancellationToken cancellationToken = default);
}

public sealed class InstalledModelSnapshotCoordinator : IInstalledModelSnapshotCoordinator
{
    private const int MaxAttempts = 3;
    private readonly KeyedCompositeLockDomain _lockDomain;
    private readonly IInstalledGgufSnapshotStore _snapshotStore;
    private readonly ICoordinatedModelProviderMapStore _providerMapStore;

    public InstalledModelSnapshotCoordinator(KeyedCompositeLockDomain lockDomain,
        IInstalledGgufSnapshotStore snapshotStore,
        ICoordinatedModelProviderMapStore providerMapStore)
    {
        ArgumentNullException.ThrowIfNull(lockDomain);
        ArgumentNullException.ThrowIfNull(snapshotStore);
        ArgumentNullException.ThrowIfNull(providerMapStore);
        _lockDomain = lockDomain;
        _snapshotStore = snapshotStore;
        _providerMapStore = providerMapStore;
    }

    public async Task<InstalledModelReadLease> AcquireReadSnapshotAsync(string modelName, CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var candidate = await _snapshotStore.DiscoverCandidateAsync(modelName, cancellationToken)
                            ?? throw new KeyNotFoundException("The installed model was not found.");
            var keys = BuildExistingKeys(candidate);
            var inner = await _lockDomain.AcquireReadAsync(keys, cancellationToken);
            try
            {
                var verified = await _snapshotStore.LoadVerifiedAsync(modelName, candidate, cancellationToken);
                if (KeysMatch(keys, BuildExistingKeys(verified)))
                {
                    var mapping = await ReadMappingAsync(inner, isMutation: false, verified.ModelName, cancellationToken);
                    var snapshot = FreezeSnapshot(verified, mapping);
                    return new InstalledModelReadLease(snapshot, inner);
                }
            }
            catch (InstalledGgufSnapshotException exception) when (IsOptimisticConflict(exception))
            {
                await inner.DisposeAsync();
                continue;
            }
            catch
            {
                await inner.DisposeAsync();
                throw;
            }

            await inner.DisposeAsync();
        }

        throw new InvalidOperationException("InstalledModelSnapshotUnstable");
    }

    public async Task<InstalledModelMutationLease> AcquireMutationAsync(InstalledModelMutationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var candidate = await _snapshotStore.DiscoverCandidateAsync(request.ModelName, cancellationToken);
            var keys = candidate is null ? BuildAcquisitionKeys(request) : BuildExistingKeys(candidate, request.IntendedMembers);
            var inner = await _lockDomain.AcquireMutationAsync(keys, cancellationToken);
            try
            {
                var verified = candidate is null
                    ? null
                    : await _snapshotStore.LoadVerifiedAsync(request.ModelName, candidate, cancellationToken);
                var currentCandidate = candidate is null
                    ? await _snapshotStore.DiscoverCandidateAsync(request.ModelName, cancellationToken)
                    : candidate;
                if (candidate is null && currentCandidate is not null)
                {
                    await inner.DisposeAsync();
                    continue;
                }

                var verifiedKeys = verified is null ? BuildAcquisitionKeys(request) : BuildExistingKeys(verified, request.IntendedMembers);
                if (KeysMatch(keys, verifiedKeys))
                {
                    var mapping = await ReadMappingAsync(inner, isMutation: true, verified?.ModelName ?? request.ModelName, cancellationToken);
                    var snapshot = verified is null ? null : FreezeSnapshot(verified, mapping);
                    return new InstalledModelMutationLease(request, snapshot, mapping, inner);
                }
            }
            catch (InstalledGgufSnapshotException exception) when (IsOptimisticConflict(exception))
            {
                await inner.DisposeAsync();
                continue;
            }
            catch
            {
                await inner.DisposeAsync();
                throw;
            }

            await inner.DisposeAsync();
        }

        throw new InvalidOperationException("InstalledModelSnapshotUnstable");
    }

    /// <inheritdoc />
    public async Task<InstalledModelFacts?> ReadFactsAsync(string modelName, CancellationToken cancellationToken = default)
    {
        // Discovery reads the registry (and its sidecars) only; nothing here opens a weight file, because a listing must not pay the
        // verification cost that belongs to a run freeze. No retry loop either: nothing to race, and a mid-delete listing is simply stale.
        var candidate = await _snapshotStore.DiscoverCandidateAsync(modelName, cancellationToken);
        if (candidate is null)
        {
            return null;
        }

        var alias = candidate.RegistryAliases.FirstOrDefault(entry =>
            string.Equals(entry.ModelName, candidate.ModelName, StringComparison.OrdinalIgnoreCase));
        if (alias is null)
        {
            return null;
        }

        await using var inner = await _lockDomain.AcquireReadAsync(BuildExistingKeys(candidate), cancellationToken);
        var mapping = await ReadMappingAsync(inner, isMutation: false, candidate.ModelName, cancellationToken);
        return new InstalledModelFacts
        {
            ModelName = candidate.ModelName,
            ProviderName = ResolveProviderName(mapping),
            Role = alias.RegistryValue.Role,
            Origin = alias.RegistryValue.Origin,
            ModelContentFingerprint = alias.RegistryValue.ModelContentFingerprint
        };
    }

    private static IReadOnlyList<string> BuildAcquisitionKeys(InstalledModelMutationRequest request)
    {
        var keys = new List<string>
        {
            ModelCoordinationKeys.Model(request.ModelName),
            ModelCoordinationKeys.ProviderMap(request.ModelName)
        };
        if (request.IntendedModelNames is not null)
        {
            keys.AddRange(request.IntendedModelNames.SelectMany(static modelName => new[]
            {
                ModelCoordinationKeys.Model(modelName),
                ModelCoordinationKeys.ProviderMap(modelName)
            }));
        }

        if (request.IntendedMembers is not null)
        {
            keys.AddRange(request.IntendedMembers.Select(static member => ModelCoordinationKeys.Path(member.RelativePath)));
        }

        return ModelCoordinationKeys.NormalizeSet(keys);
    }

    private static IReadOnlyList<string> BuildExistingKeys(InstalledGgufCandidate candidate,
        IReadOnlyList<IntendedInstalledModelMember>? intendedMembers = null) =>
        BuildKeys(candidate.RegistryAliases.Select(static alias => alias.ModelName).Prepend(candidate.ModelName),
            candidate.MemberRelativePaths,
            intendedMembers);

    private static IReadOnlyList<string> BuildExistingKeys(InstalledGgufSnapshot snapshot,
        IReadOnlyList<IntendedInstalledModelMember>? intendedMembers = null) =>
        BuildKeys(snapshot.RegistryAliases.Select(static alias => alias.ModelName).Prepend(snapshot.ModelName),
            snapshot.Members.Select(static member => member.RelativePath),
            intendedMembers);

    private static IReadOnlyList<string> BuildKeys(IEnumerable<string> aliases,
        IEnumerable<string> memberPaths,
        IReadOnlyList<IntendedInstalledModelMember>? intendedMembers)
    {
        var aliasArray = aliases.Where(static alias => !string.IsNullOrWhiteSpace(alias)).ToArray();
        var keys = aliasArray.SelectMany(static alias => new[]
        {
            ModelCoordinationKeys.Model(alias),
            ModelCoordinationKeys.ProviderMap(alias)
        }).Concat(memberPaths.Select(ModelCoordinationKeys.Path)).ToList();
        if (intendedMembers is not null)
        {
            keys.AddRange(intendedMembers.Select(static member => ModelCoordinationKeys.Path(member.RelativePath)));
        }

        return ModelCoordinationKeys.NormalizeSet(keys);
    }

    private static bool KeysMatch(IReadOnlyList<string> left, IReadOnlyList<string> right) =>
        left.SequenceEqual(right, StringComparer.Ordinal);

    private async Task<ModelProviderMapRecord?> ReadMappingAsync(ModelCoordinationLockLease inner,
        bool isMutation,
        string modelName,
        CancellationToken cancellationToken)
    {
        await using var view = new InstalledModelMapLeaseView(inner, isMutation);
        return await _providerMapStore.ReadWithRevisionAsync(view, modelName, cancellationToken);
    }

    private static InstalledModelSnapshot FreezeSnapshot(InstalledGgufSnapshot snapshot, ModelProviderMapRecord? mapping)
    {
        var aliases = snapshot.RegistryAliases.Select(static alias => alias with
        {
        }).ToArray();
        var members = snapshot.Members.Select(static member => member with
        {
            OwningAliases = Array.AsReadOnly(member.OwningAliases.ToArray())
        }).ToArray();
        return new InstalledModelSnapshot(snapshot.ModelName,
            snapshot.RegistryRevision,
            Array.AsReadOnly(aliases),
            snapshot.RegistryAliasSetHash,
            Array.AsReadOnly(members),
            snapshot.PhysicalMemberSetHash,
            snapshot.Origin,
            ResolveProviderName(mapping),
            mapping?.Revision,
            snapshot.RepoId,
            snapshot.SourceRevision,
            snapshot.Quantization,
            snapshot.Role,
            snapshot.ModelContentFingerprint);
    }

    /// <summary>The provider serving an installed GGUF.</summary>
    /// <remarks>
    ///     The <c>model_provider_map</c> is an OVERRIDE map, not a census: only a model acquired THROUGH this node (or an Ollama name
    ///     repaired by the startup backfill) ever gets a row, so a GGUF this node merely found on disk — after a reinstall, a node reset, a
    ///     moved data directory or a restored models folder — has none. Chat reads that as llama.cpp (the resolver's documented
    ///     "unmapped → default provider = llamacpp" rule), so returning null here would make every such model permanently
    ///     benchmark-ineligible while it stays perfectly chattable. A row naming another provider is still returned verbatim.
    /// </remarks>
    private static string ResolveProviderName(ModelProviderMapRecord? mapping) =>
        mapping?.ProviderName ?? LlamaServerProviderConstants.ProviderName;

    private static bool IsOptimisticConflict(InstalledGgufSnapshotException exception) =>
        exception.Code is "InstalledModelSnapshotUnstable" or "InstalledModelNotFound";

    private sealed class InstalledModelMapLeaseView : IModelProviderMapMutationLease
    {
        private readonly ModelCoordinationLockLease _inner;

        public InstalledModelMapLeaseView(ModelCoordinationLockLease inner, bool isMutation)
        {
            _inner = inner;
            MapKeys = inner.Keys.Where(static key => key.StartsWith("2:provider-map:", StringComparison.Ordinal)).ToArray();
            ModelKeys = inner.Keys.Where(static key => key.StartsWith("2:provider-map:", StringComparison.Ordinal))
                             .Select(static key => key["2:provider-map:".Length..])
                             .ToArray();
            IsMutation = isMutation;
        }

        public IReadOnlyList<string> MapKeys { get; }

        public IReadOnlyList<string> ModelKeys { get; }

        public bool IsDisposed => _inner.IsDisposed;
        public bool IsMutation { get; }

        public bool ContainsModel(string modelName) =>
            MapKeys.Contains(ModelCoordinationKeys.ProviderMap(modelName), StringComparer.Ordinal);

        public ValueTask DisposeAsync()
        {
            GC.SuppressFinalize(this);
            return ValueTask.CompletedTask;
        }
    }
}
