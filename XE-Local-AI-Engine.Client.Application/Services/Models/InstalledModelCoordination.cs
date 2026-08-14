namespace XE_Local_AI_Engine.Client.Services.Models;

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
    IReadOnlyList<IntendedInstalledModelMember>? IntendedMembers = null);

public sealed record InstalledModelSnapshot(
    string ModelName,
    IReadOnlyList<string> AliasModelNames,
    IReadOnlyList<string> MemberRelativePaths,
    string SnapshotRevision,
    string? ProviderName,
    string? ProviderMappingRevision);

public sealed record InstalledModelDiscovery(
    string ModelName,
    IReadOnlyList<string> AliasModelNames,
    IReadOnlyList<string> MemberRelativePaths,
    string DiscoveryRevision);

/// <summary>
///     Application-facing bridge until the provider-owned installed-GGUF snapshot contract is available. An adapter over
///     that provider contract must implement discovery and verified reload without leaking absolute paths.
/// </summary>
public interface IInstalledModelSnapshotSource
{
    Task<InstalledModelDiscovery?> DiscoverAsync(string modelName, CancellationToken cancellationToken);
    Task<InstalledModelSnapshot?> LoadVerifiedAsync(string modelName,
        InstalledModelDiscovery? expectedDiscovery,
        CancellationToken cancellationToken);
}

public interface IInstalledModelSnapshotCoordinator
{
    Task<InstalledModelReadLease> AcquireReadSnapshotAsync(string modelName, CancellationToken cancellationToken = default);
    Task<InstalledModelMutationLease> AcquireMutationAsync(InstalledModelMutationRequest request, CancellationToken cancellationToken = default);
}

public sealed class InstalledModelSnapshotCoordinator(
    KeyedCompositeLockDomain lockDomain,
    IInstalledModelSnapshotSource snapshotSource) : IInstalledModelSnapshotCoordinator
{
    private const int MaxAttempts = 3;
    private readonly KeyedCompositeLockDomain _lockDomain = lockDomain ?? throw new ArgumentNullException(nameof(lockDomain));
    private readonly IInstalledModelSnapshotSource _snapshotSource = snapshotSource ?? throw new ArgumentNullException(nameof(snapshotSource));

    public async Task<InstalledModelReadLease> AcquireReadSnapshotAsync(string modelName, CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var discovery = await _snapshotSource.DiscoverAsync(modelName, cancellationToken).ConfigureAwait(false)
                            ?? throw new KeyNotFoundException("The installed model was not found.");
            var keys = BuildExistingKeys(discovery);
            var inner = await _lockDomain.AcquireReadAsync(keys, cancellationToken).ConfigureAwait(false);
            try
            {
                var snapshot = await _snapshotSource.LoadVerifiedAsync(modelName, discovery, cancellationToken).ConfigureAwait(false);
                if (snapshot is not null && KeysMatch(keys, BuildExistingKeys(snapshot)))
                {
                    return new InstalledModelReadLease(snapshot, inner);
                }
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
            var discovery = await _snapshotSource.DiscoverAsync(request.ModelName, cancellationToken).ConfigureAwait(false);
            var keys = discovery is null ? BuildAcquisitionKeys(request) : BuildExistingKeys(discovery, request.IntendedMembers);
            var inner = await _lockDomain.AcquireMutationAsync(keys, cancellationToken).ConfigureAwait(false);
            try
            {
                var snapshot = await _snapshotSource.LoadVerifiedAsync(request.ModelName, discovery, cancellationToken).ConfigureAwait(false);
                var verifiedKeys = snapshot is null ? BuildAcquisitionKeys(request) : BuildExistingKeys(snapshot, request.IntendedMembers);
                if (KeysMatch(keys, verifiedKeys))
                {
                    return new InstalledModelMutationLease(request, snapshot, inner);
                }
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
        if (request.IntendedMembers is not null)
        {
            keys.AddRange(request.IntendedMembers.Select(static member => ModelCoordinationKeys.Path(member.RelativePath)));
        }

        return ModelCoordinationKeys.NormalizeSet(keys);
    }

    private static IReadOnlyList<string> BuildExistingKeys(InstalledModelDiscovery discovery,
        IReadOnlyList<IntendedInstalledModelMember>? intendedMembers = null) =>
        BuildKeys(discovery.AliasModelNames.Prepend(discovery.ModelName), discovery.MemberRelativePaths, intendedMembers);

    private static IReadOnlyList<string> BuildExistingKeys(InstalledModelSnapshot snapshot,
        IReadOnlyList<IntendedInstalledModelMember>? intendedMembers = null) =>
        BuildKeys(snapshot.AliasModelNames.Prepend(snapshot.ModelName), snapshot.MemberRelativePaths, intendedMembers);

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

    private static bool KeysMatch(IReadOnlyList<string> left, IReadOnlyList<string> right) => left.SequenceEqual(right, StringComparer.Ordinal);
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
    public bool ContainsModel(string modelName) => MapKeys.Contains(ModelCoordinationKeys.ProviderMap(modelName), StringComparer.Ordinal);

    public async ValueTask DisposeAsync()
    {
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
        ModelCoordinationLockLease inner)
    {
        Request = request;
        Snapshot = snapshot;
        _inner = inner;
        MapKeys = inner.Keys.Where(static key => key.StartsWith("2:provider-map:", StringComparison.Ordinal)).ToArray();
        ModelKeys = MapKeys.Select(static key => key["2:provider-map:".Length..]).ToArray();
        ReservedKeys = inner.Keys;
    }

    public InstalledModelMutationRequest Request { get; }
    public InstalledModelSnapshot? Snapshot { get; }
    public IReadOnlyList<string> ReservedKeys { get; }
    public IReadOnlyList<string> ModelKeys { get; }
    public IReadOnlyList<string> MapKeys { get; }
    public bool IsDisposed => _inner is null;
    public bool IsMutation => true;
    public bool ContainsModel(string modelName) => MapKeys.Contains(ModelCoordinationKeys.ProviderMap(modelName), StringComparer.Ordinal);

    public async ValueTask DisposeAsync()
    {
        var inner = Interlocked.Exchange(ref _inner, null);
        if (inner is not null)
        {
            await inner.DisposeAsync().ConfigureAwait(false);
        }
    }
}
