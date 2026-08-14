namespace XE_Local_AI_Engine.Client.Services.Models;

using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

public enum InstalledModelMutationKind
{
    Acquire,
    Delete,
    Replace
}

public sealed record IntendedInstalledModelMember(string RelativePath, InstalledModelPhysicalMemberRole Role);

public sealed record InstalledModelMutationRequest(
    string ModelName,
    InstalledModelMutationKind Kind,
    IReadOnlyList<IntendedInstalledModelMember>? IntendedMembers = null,
    IReadOnlyList<string>? IntendedModelNames = null);

public sealed record InstalledModelSnapshot(
    string ModelName,
    string RegistryRevision,
    IReadOnlyList<InstalledModelRegistryAliasSnapshot> RegistryAliases,
    string RegistryAliasSetHash,
    IReadOnlyList<InstalledModelPhysicalMember> Members,
    string PhysicalMemberSetHash,
    LocalModelOrigin? Origin,
    string? ProviderName,
    string? ProviderMappingRevision,
    string RepoId,
    string SourceRevision,
    string Quantization,
    GgufRole Role,
    string ModelContentFingerprint);

public interface IInstalledModelSnapshotCoordinator
{
    Task<InstalledModelReadLease> AcquireReadSnapshotAsync(string modelName, CancellationToken cancellationToken = default);
    Task<InstalledModelMutationLease> AcquireMutationAsync(InstalledModelMutationRequest request, CancellationToken cancellationToken = default);
}

public sealed class InstalledModelSnapshotCoordinator(
    KeyedCompositeLockDomain lockDomain,
    IInstalledGgufSnapshotStore snapshotStore,
    ICoordinatedModelProviderMapStore providerMapStore) : IInstalledModelSnapshotCoordinator
{
    private const int MaxAttempts = 3;
    private readonly KeyedCompositeLockDomain _lockDomain = lockDomain ?? throw new ArgumentNullException(nameof(lockDomain));
    private readonly IInstalledGgufSnapshotStore _snapshotStore = snapshotStore ?? throw new ArgumentNullException(nameof(snapshotStore));
    private readonly ICoordinatedModelProviderMapStore _providerMapStore = providerMapStore ?? throw new ArgumentNullException(nameof(providerMapStore));

    public async Task<InstalledModelReadLease> AcquireReadSnapshotAsync(string modelName, CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var candidate = await _snapshotStore.DiscoverCandidateAsync(modelName, cancellationToken).ConfigureAwait(false)
                            ?? throw new KeyNotFoundException("The installed model was not found.");
            var keys = BuildExistingKeys(candidate);
            var inner = await _lockDomain.AcquireReadAsync(keys, cancellationToken).ConfigureAwait(false);
            try
            {
                var verified = await _snapshotStore.LoadVerifiedAsync(modelName, candidate, cancellationToken).ConfigureAwait(false);
                if (KeysMatch(keys, BuildExistingKeys(verified)))
                {
                    var mapping = await ReadMappingAsync(inner, isMutation: false, verified.ModelName, cancellationToken).ConfigureAwait(false);
                    var snapshot = FreezeSnapshot(verified, mapping);
                    return new InstalledModelReadLease(snapshot, inner);
                }
            }
            catch (InstalledGgufSnapshotException exception) when (IsOptimisticConflict(exception))
            {
                await inner.DisposeAsync().ConfigureAwait(false);
                continue;
            }
            catch
            {
                await inner.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            await inner.DisposeAsync().ConfigureAwait(false);
        }

        throw new InvalidOperationException("InstalledModelSnapshotUnstable");
    }

    public async Task<InstalledModelMutationLease> AcquireMutationAsync(InstalledModelMutationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var candidate = await _snapshotStore.DiscoverCandidateAsync(request.ModelName, cancellationToken).ConfigureAwait(false);
            var keys = candidate is null ? BuildAcquisitionKeys(request) : BuildExistingKeys(candidate, request.IntendedMembers);
            var inner = await _lockDomain.AcquireMutationAsync(keys, cancellationToken).ConfigureAwait(false);
            try
            {
                var verified = candidate is null
                    ? null
                    : await _snapshotStore.LoadVerifiedAsync(request.ModelName, candidate, cancellationToken).ConfigureAwait(false);
                var currentCandidate = candidate is null
                    ? await _snapshotStore.DiscoverCandidateAsync(request.ModelName, cancellationToken).ConfigureAwait(false)
                    : candidate;
                if (candidate is null && currentCandidate is not null)
                {
                    await inner.DisposeAsync().ConfigureAwait(false);
                    continue;
                }

                var verifiedKeys = verified is null ? BuildAcquisitionKeys(request) : BuildExistingKeys(verified, request.IntendedMembers);
                if (KeysMatch(keys, verifiedKeys))
                {
                    var mapping = await ReadMappingAsync(inner, isMutation: true, verified?.ModelName ?? request.ModelName, cancellationToken)
                        .ConfigureAwait(false);
                    var snapshot = verified is null ? null : FreezeSnapshot(verified, mapping);
                    return new InstalledModelMutationLease(request, snapshot, mapping, inner);
                }
            }
            catch (InstalledGgufSnapshotException exception) when (IsOptimisticConflict(exception))
            {
                await inner.DisposeAsync().ConfigureAwait(false);
                continue;
            }
            catch
            {
                await inner.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            await inner.DisposeAsync().ConfigureAwait(false);
        }

        throw new InvalidOperationException("InstalledModelSnapshotUnstable");
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
        return await _providerMapStore.ReadWithRevisionAsync(view, modelName, cancellationToken).ConfigureAwait(false);
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
            mapping?.ProviderName,
            mapping?.Revision,
            snapshot.RepoId,
            snapshot.SourceRevision,
            snapshot.Quantization,
            snapshot.Role,
            snapshot.ModelContentFingerprint);
    }

    private static bool IsOptimisticConflict(InstalledGgufSnapshotException exception) =>
        exception.Code is "InstalledModelSnapshotUnstable" or "InstalledModelNotFound";

    private sealed class InstalledModelMapLeaseView(ModelCoordinationLockLease inner, bool isMutation) : IModelProviderMapMutationLease
    {
        private readonly ModelCoordinationLockLease _inner = inner;

        public IReadOnlyList<string> MapKeys { get; } = inner.Keys.Where(static key => key.StartsWith("2:provider-map:", StringComparison.Ordinal)).ToArray();

        public IReadOnlyList<string> ModelKeys { get; } = inner.Keys.Where(static key => key.StartsWith("2:provider-map:", StringComparison.Ordinal))
                                                               .Select(static key => key["2:provider-map:".Length..])
                                                               .ToArray();

        public bool IsDisposed => _inner.IsDisposed;
        public bool IsMutation { get; } = isMutation;

        public bool ContainsModel(string modelName) =>
            MapKeys.Contains(ModelCoordinationKeys.ProviderMap(modelName), StringComparer.Ordinal);

        public ValueTask DisposeAsync()
        {
            GC.SuppressFinalize(this);
            return ValueTask.CompletedTask;
        }
    }
}

public class InstalledModelReadLease : IModelProviderMapReadLease
{
    private ModelCoordinationLockLease? _inner;

    internal InstalledModelReadLease(InstalledModelSnapshot snapshot, ModelCoordinationLockLease inner)
    {
        Snapshot = snapshot;
        _inner = inner;
        MapKeys = inner.Keys.Where(static key => key.StartsWith("2:provider-map:", StringComparison.Ordinal)).ToArray();
        ModelKeys = MapKeys.Select(static key => key["2:provider-map:".Length..]).ToArray();
    }

    public InstalledModelSnapshot Snapshot { get; }
    public IReadOnlyList<string> ModelKeys { get; }
    public IReadOnlyList<string> MapKeys { get; }
    public bool IsDisposed => _inner is null;
    public virtual bool IsMutation => false;

    public bool ContainsModel(string modelName) =>
        MapKeys.Contains(ModelCoordinationKeys.ProviderMap(modelName), StringComparer.Ordinal);

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        var inner = Interlocked.Exchange(ref _inner, null);
        if (inner is not null)
        {
            await inner.DisposeAsync().ConfigureAwait(false);
        }
    }
}

public sealed class InstalledModelMutationLease : IModelProviderMapMutationLease
{
    private ModelCoordinationLockLease? _inner;

    internal InstalledModelMutationLease(InstalledModelMutationRequest request,
        InstalledModelSnapshot? snapshot,
        ModelProviderMapRecord? providerMapping,
        ModelCoordinationLockLease inner)
    {
        Request = request;
        Snapshot = snapshot;
        ProviderMapping = providerMapping;
        _inner = inner;
        MapKeys = inner.Keys.Where(static key => key.StartsWith("2:provider-map:", StringComparison.Ordinal)).ToArray();
        ModelKeys = MapKeys.Select(static key => key["2:provider-map:".Length..]).ToArray();
        ReservedKeys = inner.Keys;
    }

    public InstalledModelMutationRequest Request { get; }
    public InstalledModelSnapshot? Snapshot { get; }
    public ModelProviderMapRecord? ProviderMapping { get; }
    public IReadOnlyList<string> ReservedKeys { get; }
    public IReadOnlyList<string> ModelKeys { get; }
    public IReadOnlyList<string> MapKeys { get; }
    public bool IsDisposed => _inner is null;
    public bool IsMutation => true;

    public bool ContainsModel(string modelName) =>
        MapKeys.Contains(ModelCoordinationKeys.ProviderMap(modelName), StringComparer.Ordinal);

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        var inner = Interlocked.Exchange(ref _inner, null);
        if (inner is not null)
        {
            await inner.DisposeAsync().ConfigureAwait(false);
        }
    }
}
