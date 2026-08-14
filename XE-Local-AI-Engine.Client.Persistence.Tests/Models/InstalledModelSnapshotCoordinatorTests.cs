namespace XE_Local_AI_Engine.Client.Persistence.Tests.Models;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Client.Services.Validation;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer;

public sealed class InstalledModelSnapshotCoordinatorTests
{
    [Test]
    public async Task ReadSnapshot_ProjectsCompleteImmutableProviderSnapshotAndCapturedMapping()
    {
        var fixture = CreateCurrentFixture();
        var map = new ReadOnlyMapStore(new ModelProviderMapRecord(fixture.Snapshot.ModelName,
            LlamaServerProviderConstants.ProviderName,
            UpdatedAtUtc: 1,
            Revision: "map-r1"));
        var coordinator = new InstalledModelSnapshotCoordinator(new KeyedCompositeLockDomain(), fixture.Store, map);

        await using var lease = await coordinator.AcquireReadSnapshotAsync(fixture.Snapshot.ModelName);

        AssertEx.Equal(fixture.Snapshot.ModelName, lease.Snapshot.ModelName);
        AssertEx.Equal(fixture.Snapshot.RegistryRevision, lease.Snapshot.RegistryRevision);
        AssertEx.Equal(fixture.Snapshot.RegistryAliasSetHash, lease.Snapshot.RegistryAliasSetHash);
        AssertEx.Equal(fixture.Snapshot.PhysicalMemberSetHash, lease.Snapshot.PhysicalMemberSetHash);
        AssertEx.Equal(fixture.Snapshot.ModelContentFingerprint, lease.Snapshot.ModelContentFingerprint);
        AssertEx.Equal(LocalModelOrigin.HuggingFace, lease.Snapshot.Origin);
        AssertEx.Equal(LlamaServerProviderConstants.ProviderName, lease.Snapshot.ProviderName);
        AssertEx.Equal("map-r1", lease.Snapshot.ProviderMappingRevision);
        AssertEx.Equal(expected: 2, lease.Snapshot.Members.Count);
        AssertEx.Equal(expected: 1, lease.Snapshot.RegistryAliases.Count);
        AssertEx.True(lease.ContainsModel(fixture.Snapshot.ModelName));

        fixture.Aliases[0] = fixture.Aliases[0] with { ModelName = "changed" };
        fixture.MemberOwners[0] = "changed";
        AssertEx.Equal("foo:Q4_K_M", lease.Snapshot.RegistryAliases[0].ModelName);
        AssertEx.Equal("foo:Q4_K_M", lease.Snapshot.Members[0].OwningAliases[0]);
    }

    [Test]
    public async Task ReadSnapshot_RetriesProviderOptimisticConflictThreeTimes()
    {
        var fixture = CreateCurrentFixture(failuresBeforeSuccess: 2);
        var coordinator = new InstalledModelSnapshotCoordinator(new KeyedCompositeLockDomain(),
            fixture.Store,
            new ReadOnlyMapStore(mapping: null));

        await using var lease = await coordinator.AcquireReadSnapshotAsync(fixture.Snapshot.ModelName);

        AssertEx.Equal(expected: 3, fixture.Store.LoadCount);
        AssertEx.Equal(fixture.Snapshot.ModelContentFingerprint, lease.Snapshot.ModelContentFingerprint);
    }

    [Test]
    public async Task StateProbe_DerivesCurrentAndLegacyInstalledOnlyFromVerifiedLeaseFacts()
    {
        var identity = CreateIdentity();
        var current = CreateCurrentFixture();
        var legacy = CreateLegacyFixture();
        var map = new ReadOnlyMapStore(new ModelProviderMapRecord(identity.CanonicalModelName,
            LlamaServerProviderConstants.ProviderName,
            UpdatedAtUtc: 1,
            Revision: "map-r1"));
        var probe = new GgufAcquisitionStateProbe();

        var currentCoordinator = new InstalledModelSnapshotCoordinator(new KeyedCompositeLockDomain(), current.Store, map);
        await using var currentLease = await currentCoordinator.AcquireMutationAsync(CreateRequest(identity));
        var currentState = await probe.ProbeAsync(identity, currentLease, CancellationToken.None);
        AssertEx.Equal(GgufAcquisitionDisposition.VerifiedInstalled, currentState.Disposition);

        var legacyCoordinator = new InstalledModelSnapshotCoordinator(new KeyedCompositeLockDomain(), legacy.Store, map);
        await using var legacyLease = await legacyCoordinator.AcquireMutationAsync(CreateRequest(identity));
        var legacyState = await probe.ProbeAsync(identity, legacyLease, CancellationToken.None);
        AssertEx.Equal(GgufAcquisitionDisposition.VerifiedLegacyInstalled, legacyState.Disposition);
    }

    private static InstalledModelMutationRequest CreateRequest(ResolvedGgufAcquisitionIdentity identity) =>
        new(identity.CanonicalModelName,
            InstalledModelMutationKind.Acquire,
            [
                new IntendedInstalledModelMember(identity.RelativeGgufPath, InstalledModelPhysicalMemberRole.Weight),
                new IntendedInstalledModelMember(identity.RelativeSidecarPath, InstalledModelPhysicalMemberRole.Sidecar)
            ]);

    private static ResolvedGgufAcquisitionIdentity CreateIdentity()
    {
        var resolver = new GgufAcquisitionIdentityResolver(new ModelNameValidator(Options.Create(new SecurityOptions())));
        return resolver.Resolve(new GgufAcquisitionIntent(GgufAcquisitionOperationKind.Download, "foo", "Q4_K_M"));
    }

    private static SnapshotFixture CreateCurrentFixture(int failuresBeforeSuccess = 0)
    {
        var identity = CreateIdentity();
        return CreateFixture(identity.RelativeGgufPath,
            identity.RelativeSidecarPath,
            LocalModelOrigin.HuggingFace,
            failuresBeforeSuccess);
    }

    private static SnapshotFixture CreateLegacyFixture() => CreateFixture("legacy/foo.gguf", sidecarPath: null, origin: null);

    private static SnapshotFixture CreateFixture(string weightPath,
        string? sidecarPath,
        LocalModelOrigin? origin,
        int failuresBeforeSuccess = 0)
    {
        const string modelName = "foo:Q4_K_M";
        var owners = new[] { modelName };
        var registryValue = new InstalledGgufRegistryValue("org/repo",
            Path.GetFileName(weightPath),
            "Q4_K_M",
            weightPath,
            SizeBytes: 10,
            Sha256: new string('a', 64),
            SourceRevision: "source-r1",
            DownloadedAtUtc: DateTimeOffset.UnixEpoch,
            Role: GgufRole.Chat,
            ProjectorFileName: null,
            ProjectorRelativePath: null,
            ProjectorSizeBytes: null,
            ProjectorSha256: null,
            Origin: origin,
            SourceDisplayName: "model.gguf",
            MetadataSchemaVersion: origin is null ? null : 1,
            ModelContentFingerprint: null);
        var aliases = new[]
        {
            new InstalledModelRegistryAliasSnapshot(modelName,
                registryValue,
                "registry-r1",
                weightPath,
                ProjectorRelativePath: null,
                SidecarRelativePath: sidecarPath)
        };
        var members = new List<InstalledModelPhysicalMember>
        {
            new(weightPath,
                InstalledModelPhysicalMemberRole.Weight,
                SizeBytes: 10,
                Sha256: new string('a', 64),
                MemberFingerprint: GgufMemberFingerprint.Compute(new string('a', 64), sizeBytes: 10),
                OwningAliases: owners,
                Required: true,
                MetadataSchemaVersion: null)
        };
        if (sidecarPath is not null)
        {
            members.Add(new InstalledModelPhysicalMember(sidecarPath,
                InstalledModelPhysicalMemberRole.Sidecar,
                SizeBytes: 5,
                Sha256: new string('b', 64),
                MemberFingerprint: null,
                OwningAliases: owners,
                Required: true,
                MetadataSchemaVersion: 1));
        }

        var memberArray = members.ToArray();
        var contentFingerprint = GgufModelContentFingerprint.ComputeV1(memberArray
            .Where(static member => member.Role != InstalledModelPhysicalMemberRole.Sidecar)
            .Select(static member => new GgufModelContentMember(member.RelativePath,
                member.Role,
                member.SizeBytes,
                member.Sha256,
                member.OwningAliases)));
        var snapshot = new InstalledGgufSnapshot(modelName,
            "registry-r1",
            aliases,
            GgufRegistryAliasSetHash.ComputeV1(aliases),
            memberArray,
            GgufPhysicalMemberSetHash.ComputeV1(memberArray),
            origin,
            "org/repo",
            "source-r1",
            "Q4_K_M",
            GgufRole.Chat,
            contentFingerprint);
        var candidate = new InstalledGgufCandidate(modelName,
            aliases,
            memberArray.Select(static member => member.RelativePath).ToArray());
        return new SnapshotFixture(snapshot,
            aliases,
            owners,
            new FakeSnapshotStore(candidate, snapshot, failuresBeforeSuccess));
    }

    private sealed record SnapshotFixture(InstalledGgufSnapshot Snapshot,
        InstalledModelRegistryAliasSnapshot[] Aliases,
        string[] MemberOwners,
        FakeSnapshotStore Store);

    private sealed class FakeSnapshotStore(InstalledGgufCandidate candidate,
        InstalledGgufSnapshot snapshot,
        int failuresBeforeSuccess) : IInstalledGgufSnapshotStore
    {
        public int LoadCount { get; private set; }

        public Task<InstalledGgufCandidate?> DiscoverCandidateAsync(string modelName, CancellationToken cancellationToken) =>
            Task.FromResult<InstalledGgufCandidate?>(candidate);

        public Task<InstalledGgufSnapshot> LoadVerifiedAsync(string modelName,
            InstalledGgufCandidate expectedCandidate,
            CancellationToken cancellationToken)
        {
            LoadCount++;
            if (LoadCount <= failuresBeforeSuccess)
            {
                throw new InstalledGgufSnapshotException("InstalledModelSnapshotUnstable", "The installed model changed.");
            }

            return Task.FromResult(snapshot);
        }
    }

    private sealed class ReadOnlyMapStore(ModelProviderMapRecord? mapping) : ICoordinatedModelProviderMapStore
    {
        public Task<ModelProviderMapRecord?> ReadWithRevisionAsync(IModelProviderMapReadLease lease,
            string modelName,
            CancellationToken cancellationToken = default)
        {
            AssertEx.True(lease.ContainsModel(modelName));
            return Task.FromResult(mapping);
        }

        public Task<ProviderMapClaimResult> TryClaimLlamaCppAsync(IModelProviderMapMutationLease lease,
            string modelName,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ProviderMapMutationResult> TryUpsertAsync(IModelProviderMapMutationLease lease,
            string modelName,
            string providerName,
            string? expectedRevision = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ProviderMapRestoreResult> TryRestoreAsync(IModelProviderMapMutationLease lease,
            ProviderMapMutationReceipt receipt,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ProviderMapRemovalResult> TryRemoveIfMatchAsync(IModelProviderMapMutationLease lease,
            string modelName,
            string expectedProvider,
            string expectedRevision,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
