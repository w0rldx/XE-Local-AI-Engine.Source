namespace XE_Local_AI_Engine.Tests.CloudProviders;

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.ModelFit;
using XE_Local_AI_Engine.Client.Services.ModelFit.Implementation;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Client.Services.Validation;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.HuggingFace.Contracts;
using XE_Local_AI_Engine.Providers.HuggingFace.Options;
using XE_Local_AI_Engine.Tests.Testing;
using GgufAcquisitionOperationKind = XE_Local_AI_Engine.Client.Services.Models.GgufAcquisitionOperationKind;

public sealed class GgufImportTransactionCoordinatorTests
{
    [Test]
    public async Task Preview_ReturnsSafeMetadataWithoutAbsoluteSourcePath()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), "private", "example-Q4_K_M.gguf");
        var coordinator = BuildCoordinator(sourcePath);

        var result = await coordinator.PreviewAsync(sourcePath);
        var serialized = JsonSerializer.Serialize(result);

        AssertEx.Equal("example", result.ModelBaseName);
        AssertEx.Equal("example:Q4_K_M", result.CanonicalModelName);
        AssertEx.Equal("example-Q4_K_M.gguf", result.SourceDisplayName);
        AssertEx.False(serialized.Contains(sourcePath, StringComparison.Ordinal));
        AssertEx.True(result.PreviewToken.Length >= 32);
        AssertEx.True(result.HasSufficientStorage is true);
    }

    [Test]
    public async Task Start_PreviewTokenUsedWithDifferentSource_IsRejectedBeforeReservation()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), "private", "example-Q4_K_M.gguf");
        var coordinator = BuildCoordinator(sourcePath);
        var preview = await coordinator.PreviewAsync(sourcePath);

        var exception = await Assert.ThrowsAsync<GgufImportApplicationException>(() => coordinator.StartAsync(new StartGgufImportCommand(sourcePath + ".replacement",
            preview.PreviewToken,
            preview.ModelBaseName,
            "Q4_K_M")));

        AssertEx.Equal("InvalidPreviewToken", exception!.ErrorCode);
        AssertEx.False(exception.Message.Contains(sourcePath, StringComparison.Ordinal));
    }

    [Test]
    public async Task Start_SamePathAndMetadataButDifferentSourceIdentity_IsRejectedAsStale()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), "private", "example-Q4_K_M.gguf");
        var first = AcceptedInspection(Path.GetFileName(sourcePath));
        var second = first with
        {
            SourceIdentityToken = $"v1:{new string('b', 64)}"
        };
        var coordinator = BuildCoordinator(sourcePath, inspector: new SequenceInspector(first, second));
        var preview = await coordinator.PreviewAsync(sourcePath);

        var exception = await Assert.ThrowsAsync<GgufImportApplicationException>(() => coordinator.StartAsync(new StartGgufImportCommand(sourcePath,
            preview.PreviewToken,
            preview.ModelBaseName,
            "Q4_K_M")));

        AssertEx.Equal("StalePreview", exception!.ErrorCode);
    }

    [Test]
    public async Task Preview_WhenQuantizationMustBeSelected_ReturnsRepositoryCanonicalChoices()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), "private", "example.gguf");
        var inspection = AcceptedInspection(Path.GetFileName(sourcePath)) with
        {
            DetectedQuantization = null,
            Rejections = new[]
            {
                GgufImportRejectionCode.QuantizationRequired
            }
        };
        var coordinator = BuildCoordinator(sourcePath, inspection: inspection);

        var result = await coordinator.PreviewAsync(sourcePath);

        AssertEx.True(result.CanonicalQuantizationChoices.Count > 0);
        AssertEx.True(result.CanonicalQuantizationChoices.Contains("Q4_K_M", StringComparer.Ordinal));
        AssertEx.True(result.CanonicalQuantizationChoices.SequenceEqual(GgufAcquisitionIdentityResolver.CanonicalQuantizationChoices,
            StringComparer.Ordinal));
    }

    [Test]
    public async Task Preview_WhenFreeSpaceIsBelowFilePlusMargin_ReturnsFalse()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), "private", "example-Q4_K_M.gguf");
        var coordinator = BuildCoordinator(sourcePath, availableBytes: 50, diskMarginBytes: 9);

        var result = await coordinator.PreviewAsync(sourcePath);

        AssertEx.True(result.HasSufficientStorage is false);
    }

    [Test]
    public async Task Start_QuantizationNotOfferedByPreview_IsRejectedBeforeReservation()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), "private", "example-Q4_K_M.gguf");
        var coordinator = BuildCoordinator(sourcePath);
        var preview = await coordinator.PreviewAsync(sourcePath);

        var exception = await Assert.ThrowsAsync<GgufImportApplicationException>(() => coordinator.StartAsync(new StartGgufImportCommand(sourcePath,
            preview.PreviewToken,
            preview.ModelBaseName,
            "Q5_K_M")));

        AssertEx.Equal("UnsupportedQuantization", exception!.ErrorCode);
    }

    [Test]
    public async Task Start_WhenSameModelAlreadyActive_ReturnsAcquisitionAlreadyActiveWithoutBlockingOnPreflight()
    {
        // Regression: a second import of the same model used to fall through to preflight, which blocks on the
        // composite mutation lease held by the first (still-running) import for the entire copy. The active-operation
        // check must reject the second Start before preflight is even reached, so SecondCallHangsPreflight makes any
        // second ResolveAndReserveAsync call hang forever — proving the fix short-circuits ahead of it.
        var sourcePath = Path.Combine(Path.GetTempPath(), "private", "example-Q4_K_M.gguf");
        var resolver = new GgufAcquisitionIdentityResolver(new ModelNameValidator(Options.Create(new SecurityOptions())));
        var identity = resolver.Resolve(new GgufAcquisitionIntent(GgufAcquisitionOperationKind.Import,
            "example",
            "Q4_K_M"));
        var importer = new BlockingCommitImporter(identity);
        var services = new ServiceCollection();
        services.AddSingleton<IGgufAcquisitionPreflight>(new SecondCallHangsPreflight(identity));
        var coordinator = BuildCoordinator(sourcePath,
            importer: importer,
            resolver: resolver,
            services: services.BuildServiceProvider());
        var firstPreview = await coordinator.PreviewAsync(sourcePath);

        var ticket = await coordinator.StartAsync(new StartGgufImportCommand(sourcePath,
            firstPreview.PreviewToken,
            firstPreview.ModelBaseName,
            "Q4_K_M"));
        await importer.CommitEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var secondPreview = await coordinator.PreviewAsync(sourcePath);
        var exception = await Assert.ThrowsAsync<GgufImportApplicationException>(() => coordinator.StartAsync(new StartGgufImportCommand(sourcePath,
                                                                                              secondPreview.PreviewToken,
                                                                                              secondPreview.ModelBaseName,
                                                                                              "Q4_K_M"))
            .WaitAsync(TimeSpan.FromSeconds(2)));

        AssertEx.Equal("AcquisitionAlreadyActive", exception!.ErrorCode);

        importer.ReleaseCommit.SetResult();
        await WaitForPhaseAsync(coordinator, ticket.OperationId, GgufAcquisitionPhase.Failed);
    }

    [Test]
    public async Task Cancel_DuringCommit_RollsBackAndNeverPublishesCompleted()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), "private", "example-Q4_K_M.gguf");
        var security = Options.Create(new SecurityOptions());
        var resolver = new GgufAcquisitionIdentityResolver(new ModelNameValidator(security));
        var identity = resolver.Resolve(new GgufAcquisitionIntent(GgufAcquisitionOperationKind.Import,
            "example",
            "Q4_K_M"));
        var importer = new BlockingCommitImporter(identity);
        var services = new ServiceCollection();
        services.AddSingleton<IGgufAcquisitionPreflight>(new AvailablePreflight(identity));
        var coordinator = BuildCoordinator(sourcePath,
            importer: importer,
            resolver: resolver,
            services: services.BuildServiceProvider());
        var preview = await coordinator.PreviewAsync(sourcePath);

        var ticket = await coordinator.StartAsync(new StartGgufImportCommand(sourcePath,
            preview.PreviewToken,
            preview.ModelBaseName,
            "Q4_K_M"));
        await importer.CommitEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        AssertEx.True(coordinator.Cancel(ticket.OperationId));
        importer.ReleaseCommit.SetResult();
        await WaitForPhaseAsync(coordinator, ticket.OperationId, GgufAcquisitionPhase.Cancelled);

        AssertEx.Equal(expected: 1, importer.RollbackCount);
        AssertEx.Equal(GgufAcquisitionPhase.Cancelled, coordinator.GetStatus(ticket.OperationId)!.Phase);
    }

    [Test]
    public async Task Cancel_AfterMapClaim_WhenRestoreIsSuperseded_PreservesCommittedArtifactsAndFailsClosed()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), "private", "example-Q4_K_M.gguf");
        var security = Options.Create(new SecurityOptions());
        var resolver = new GgufAcquisitionIdentityResolver(new ModelNameValidator(security));
        var identity = resolver.Resolve(new GgufAcquisitionIntent(GgufAcquisitionOperationKind.Import,
            "example",
            "Q4_K_M"));
        var importer = new BlockingCommitImporter(identity);
        importer.ReleaseCommit.SetResult();
        var mapStore = new SupersedingMapStore(identity.CanonicalModelName);
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddSingleton<IGgufAcquisitionPreflight>(new AvailablePreflight(identity));
        serviceCollection.AddSingleton<ICoordinatedModelProviderMapStore>(mapStore);
        var services = serviceCollection.BuildServiceProvider();
        var coordinator = BuildCoordinator(sourcePath,
            importer: importer,
            resolver: resolver,
            services: services);
        var preview = await coordinator.PreviewAsync(sourcePath);

        var ticket = await coordinator.StartAsync(new StartGgufImportCommand(sourcePath,
            preview.PreviewToken,
            preview.ModelBaseName,
            "Q4_K_M"));
        await mapStore.ClaimEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        AssertEx.True(coordinator.Cancel(ticket.OperationId));
        mapStore.ReleaseClaim.SetResult();
        await WaitForPhaseAsync(coordinator, ticket.OperationId, GgufAcquisitionPhase.Failed);

        var status = coordinator.GetStatus(ticket.OperationId)!;
        AssertEx.Equal("ImportCompensationFailed", status.ErrorCode);
        AssertEx.Equal(expected: 0, importer.RollbackCount);
    }

    [Test]
    public async Task PartialCommit_WhenRollbackFails_PublishesImportCompensationFailure()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), "private", "example-Q4_K_M.gguf");
        var resolver = new GgufAcquisitionIdentityResolver(new ModelNameValidator(Options.Create(new SecurityOptions())));
        var identity = resolver.Resolve(new GgufAcquisitionIntent(GgufAcquisitionOperationKind.Import,
            "example",
            "Q4_K_M"));
        var importer = new BlockingCommitImporter(identity)
        {
            ThrowPartialCommit = true,
            ThrowRollback = true
        };
        importer.ReleaseCommit.SetResult();
        var services = new ServiceCollection();
        services.AddSingleton<IGgufAcquisitionPreflight>(new AvailablePreflight(identity));
        var coordinator = BuildCoordinator(sourcePath,
            importer: importer,
            resolver: resolver,
            services: services.BuildServiceProvider());
        var preview = await coordinator.PreviewAsync(sourcePath);

        var ticket = await coordinator.StartAsync(new StartGgufImportCommand(sourcePath,
            preview.PreviewToken,
            preview.ModelBaseName,
            "Q4_K_M"));
        await WaitForPhaseAsync(coordinator, ticket.OperationId, GgufAcquisitionPhase.Failed);

        AssertEx.Equal("ImportCompensationFailed", coordinator.GetStatus(ticket.OperationId)!.ErrorCode);
        AssertEx.Equal(expected: 1, importer.RollbackCount);
    }

    [Test]
    public async Task SuccessfulImport_PublishesCopyAndCommitPhasesWithMonotonicTimestamps()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), "private", "example-Q4_K_M.gguf");
        var resolver = new GgufAcquisitionIdentityResolver(new ModelNameValidator(Options.Create(new SecurityOptions())));
        var identity = resolver.Resolve(new GgufAcquisitionIntent(GgufAcquisitionOperationKind.Import,
            "example",
            "Q4_K_M"));
        var importer = new BlockingCommitImporter(identity);
        importer.ReleaseCommit.SetResult();
        var publisher = new RecordingPublisher();
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddSingleton<IGgufAcquisitionPreflight>(new AvailablePreflight(identity));
        serviceCollection.AddSingleton<ICoordinatedModelProviderMapStore>(new InMemoryCoordinatedModelProviderMapStore());
        var coordinator = BuildCoordinator(sourcePath,
            importer: importer,
            resolver: resolver,
            services: serviceCollection.BuildServiceProvider(),
            publisher: publisher);
        var preview = await coordinator.PreviewAsync(sourcePath);

        var ticket = await coordinator.StartAsync(new StartGgufImportCommand(sourcePath,
            preview.PreviewToken,
            preview.ModelBaseName,
            "Q4_K_M"));
        await WaitForPhaseAsync(coordinator, ticket.OperationId, GgufAcquisitionPhase.Completed);

        var events = publisher.Events.ToArray();
        AssertEx.True(events.Select(static value => value.Phase).SequenceEqual(["Validating", "Copying", "Committing", "Completed"],
            StringComparer.Ordinal));
        AssertEx.True(events.All(static value => value.UpdatedAtUtc is not null));
        AssertEx.True(events.Zip(events.Skip(1), static (left, right) => left.UpdatedAtUtc < right.UpdatedAtUtc).All(static value => value));
    }

    private static GgufImportTransactionCoordinator BuildCoordinator(string sourcePath,
        GgufImportInspection? inspection = null,
        long availableBytes = long.MaxValue,
        long diskMarginBytes = 0,
        IGgufModelImporter? importer = null,
        GgufAcquisitionIdentityResolver? resolver = null,
        ServiceProvider? services = null,
        IGgufImportInspector? inspector = null,
        IGgufDownloadEventPublisher? publisher = null)
    {
        var security = Options.Create(new SecurityOptions());
        services ??= new ServiceCollection().BuildServiceProvider();
        return new GgufImportTransactionCoordinator(inspector ?? new AcceptedInspector(inspection ?? AcceptedInspection(Path.GetFileName(sourcePath))),
            importer ?? new UnusedImporter(),
            resolver ?? new GgufAcquisitionIdentityResolver(new ModelNameValidator(security)),
            new GgufAcquisitionOperationRegistry(TimeProvider.System),
            services.GetRequiredService<IServiceScopeFactory>(),
            publisher ?? new NullGgufDownloadEventPublisher(),
            new FixedFreeSpaceProbe(availableBytes),
            new HuggingFaceOptions
            {
                ModelsDirectory = Path.GetTempPath(),
                DiskMarginBytes = diskMarginBytes
            },
            TimeProvider.System,
            NullLogger<GgufImportTransactionCoordinator>.Instance);
    }

    private static async Task WaitForPhaseAsync(GgufImportTransactionCoordinator coordinator,
        Guid operationId,
        GgufAcquisitionPhase phase)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (coordinator.GetStatus(operationId)?.Phase == phase)
            {
                return;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException($"Import operation '{operationId}' did not reach phase {phase}.");
    }

    private static GgufImportInspection AcceptedInspection(string displayName) =>
        new(42,
            GgufVersion: 3,
            Architecture: "llama",
            GgufImportWorkload.CausalChat,
            "Q4_K_M",
            displayName,
            Array.Empty<GgufImportRejectionCode>(),
            Array.Empty<string>())
        {
            SourceIdentityToken = $"v1:{new string('a', 64)}"
        };

    private sealed class AcceptedInspector(GgufImportInspection inspection) : IGgufImportInspector
    {
        public Task<GgufImportInspection> InspectAsync(GgufImportSource source,
            GgufImportInspectionMode mode,
            CancellationToken cancellationToken) =>
            Task.FromResult(inspection);
    }

    private sealed class SequenceInspector(params GgufImportInspection[] inspections) : IGgufImportInspector
    {
        private int _index;

        public Task<GgufImportInspection> InspectAsync(GgufImportSource source,
            GgufImportInspectionMode mode,
            CancellationToken cancellationToken)
        {
            var index = Math.Min(Interlocked.Increment(ref _index) - 1, inspections.Length - 1);
            return Task.FromResult(inspections[index]);
        }
    }

    private sealed class FixedFreeSpaceProbe(long availableBytes) : IFreeSpaceProbe
    {
        public long GetAvailableFreeBytes(string path) =>
            availableBytes;
    }

    private sealed class UnusedImporter : IGgufModelImporter
    {
        public Task<PreparedGgufImport> PrepareAsync(GgufImportSource source,
            GgufImportDestination destination,
            IProgress<GgufImportProgress>? progress,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<GgufImportCommitReceipt> CommitAsync(PreparedGgufImport preparedImport, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task RollbackCommittedAsync(GgufImportCommitReceipt commitReceipt, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DiscardPreparedAsync(PreparedGgufImport preparedImport, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class AvailablePreflight(ResolvedGgufAcquisitionIdentity identity) : IGgufAcquisitionPreflight
    {
        private readonly KeyedCompositeLockDomain _domain = new();

        [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
            Justification = "The returned PreparedGgufAcquisition takes exclusive ownership of the mutation lease and the production coordinator disposes or transfers it on every path.")]
        public async Task<PreparedGgufAcquisition> ResolveAndReserveAsync(GgufAcquisitionIntent intent,
            CancellationToken cancellationToken = default)
        {
            var request = new InstalledModelMutationRequest(identity.CanonicalModelName,
                InstalledModelMutationKind.Acquire,
                [
                    new IntendedInstalledModelMember(identity.RelativeGgufPath, InstalledModelPhysicalMemberRole.Weight),
                    new IntendedInstalledModelMember(identity.RelativeSidecarPath, InstalledModelPhysicalMemberRole.Sidecar)
                ]);
            var keys = new[]
            {
                ModelCoordinationKeys.Model(identity.CanonicalModelName),
                ModelCoordinationKeys.Path(identity.RelativeGgufPath),
                ModelCoordinationKeys.Path(identity.RelativeSidecarPath),
                ModelCoordinationKeys.ProviderMap(identity.CanonicalModelName)
            };
            var inner = await _domain.AcquireMutationAsync(keys, cancellationToken);
            var lease = new InstalledModelMutationLease(request, snapshot: null, providerMapping: null, inner);
            return new PreparedGgufAcquisition(identity,
                GgufAcquisitionDisposition.Available,
                ProviderMapDisposition.Absent,
                lease,
                activeOperationId: null);
        }
    }

    // Hangs forever on the SECOND call to ResolveAndReserveAsync (the preflight call that acquires the composite
    // mutation lease). Used to prove the pre-preflight active-operation check rejects a second Start for the same
    // model before preflight is ever reached — if it were reached, this class would make the test time out.
    private sealed class SecondCallHangsPreflight(ResolvedGgufAcquisitionIdentity identity) : IGgufAcquisitionPreflight
    {
        private readonly KeyedCompositeLockDomain _domain = new();
        private int _callCount;

        [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
            Justification = "The returned PreparedGgufAcquisition takes exclusive ownership of the mutation lease and the production coordinator disposes or transfers it on every path.")]
        public async Task<PreparedGgufAcquisition> ResolveAndReserveAsync(GgufAcquisitionIntent intent,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _callCount) > 1)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }

            var request = new InstalledModelMutationRequest(identity.CanonicalModelName,
                InstalledModelMutationKind.Acquire,
                [
                    new IntendedInstalledModelMember(identity.RelativeGgufPath, InstalledModelPhysicalMemberRole.Weight),
                    new IntendedInstalledModelMember(identity.RelativeSidecarPath, InstalledModelPhysicalMemberRole.Sidecar)
                ]);
            var keys = new[]
            {
                ModelCoordinationKeys.Model(identity.CanonicalModelName),
                ModelCoordinationKeys.Path(identity.RelativeGgufPath),
                ModelCoordinationKeys.Path(identity.RelativeSidecarPath),
                ModelCoordinationKeys.ProviderMap(identity.CanonicalModelName)
            };
            var inner = await _domain.AcquireMutationAsync(keys, cancellationToken);
            var lease = new InstalledModelMutationLease(request, snapshot: null, providerMapping: null, inner);
            return new PreparedGgufAcquisition(identity,
                GgufAcquisitionDisposition.Available,
                ProviderMapDisposition.Absent,
                lease,
                activeOperationId: null);
        }
    }

    private sealed class BlockingCommitImporter(ResolvedGgufAcquisitionIdentity identity) : IGgufModelImporter
    {
        private static readonly string Hash = new('a', 64);

        private readonly GgufModelRegistryEntry _entry = new()
        {
            RegistryRevision = $"v1:{Hash}",
            Origin = LocalModelOrigin.Imported,
            ModelName = identity.CanonicalModelName,
            RepoId = identity.CanonicalModelName,
            FileName = identity.FinalFileName,
            Quant = identity.CanonicalQuantization,
            LocalPath = identity.RelativeGgufPath,
            SizeBytes = 42,
            Sha256 = Hash,
            SourceRevision = $"sha256:{Hash}",
            DownloadedAtUtc = DateTimeOffset.UnixEpoch,
            Role = GgufRole.Chat,
            SourceDisplayName = "example-Q4_K_M.gguf",
            MetadataSchemaVersion = GgufAcquisitionMetadata.CurrentSchemaVersion,
            ModelContentFingerprint = $"v1:{Hash}"
        };

        public TaskCompletionSource CommitEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseCommit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int RollbackCount { get; private set; }
        public bool ThrowPartialCommit { get; init; }
        public bool ThrowRollback { get; init; }

        public Task<PreparedGgufImport> PrepareAsync(GgufImportSource source,
            GgufImportDestination destination,
            IProgress<GgufImportProgress>? progress,
            CancellationToken cancellationToken)
        {
            var metadata = new GgufAcquisitionMetadata
            {
                SchemaVersion = GgufAcquisitionMetadata.CurrentSchemaVersion,
                RegistryRevision = _entry.RegistryRevision!,
                ModelName = _entry.ModelName,
                Origin = LocalModelOrigin.Imported,
                LocalFileName = _entry.FileName,
                Quantization = _entry.Quant,
                WeightContentSha256 = Hash,
                WeightSizeBytes = _entry.SizeBytes,
                WeightMemberFingerprint = $"sha256:{Hash}:42",
                SourceDisplayName = _entry.SourceDisplayName!,
                AcquiredAtUtc = _entry.DownloadedAtUtc,
                RegistryRepoId = _entry.RepoId,
                RegistrySourceRevision = _entry.SourceRevision,
                Role = _entry.Role,
                ModelContentFingerprint = _entry.ModelContentFingerprint!
            };
            return Task.FromResult(new PreparedGgufImport("operation",
                destination,
                "temporary.gguf",
                "temporary.xe-model.json",
                _entry,
                metadata,
                metadata.WeightMemberFingerprint,
                metadata.ModelContentFingerprint));
        }

        public async Task<GgufImportCommitReceipt> CommitAsync(PreparedGgufImport preparedImport,
            CancellationToken cancellationToken)
        {
            CommitEntered.SetResult();
            await ReleaseCommit.Task;
            var receipt = new GgufImportCommitReceipt(_entry,
                "final.gguf",
                "final.xe-model.json",
                preparedImport.WeightMemberFingerprint,
                preparedImport.ModelContentFingerprint);
            if (ThrowPartialCommit)
            {
                throw new GgufImportCommitException(receipt,
                    "The import could not be committed safely.",
                    new IOException("registry failed"));
            }

            return receipt;
        }

        public Task RollbackCommittedAsync(GgufImportCommitReceipt commitReceipt, CancellationToken cancellationToken)
        {
            RollbackCount++;
            if (ThrowRollback)
            {
                throw new IOException("rollback failed");
            }

            return Task.CompletedTask;
        }

        public Task DiscardPreparedAsync(PreparedGgufImport preparedImport, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class SupersedingMapStore(string modelName) : ICoordinatedModelProviderMapStore
    {
        private readonly ProviderMapMutationReceipt _receipt = new(modelName,
            Prior: null,
            new ModelProviderMapRecord(modelName, "llamacpp", 1, "revision"),
            WasRemoval: false);

        public TaskCompletionSource ClaimEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseClaim { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ModelProviderMapRecord?> ReadWithRevisionAsync(IModelProviderMapReadLease lease,
            string modelName,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async Task<ProviderMapClaimResult> TryClaimLlamaCppAsync(IModelProviderMapMutationLease lease,
            string modelName,
            CancellationToken cancellationToken = default)
        {
            ClaimEntered.SetResult();
            await ReleaseClaim.Task.ConfigureAwait(false);
            return new ProviderMapClaimResult.Created(_receipt);
        }

        public Task<ProviderMapMutationResult> TryUpsertAsync(IModelProviderMapMutationLease lease,
            string modelName,
            string providerName,
            string? expectedRevision = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ProviderMapRestoreResult> TryRestoreAsync(IModelProviderMapMutationLease lease,
            ProviderMapMutationReceipt receipt,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ProviderMapRestoreResult.Superseded);

        public Task<ProviderMapRemovalResult> TryRemoveIfMatchAsync(IModelProviderMapMutationLease lease,
            string modelName,
            string expectedProvider,
            string expectedRevision,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingPublisher : IGgufDownloadEventPublisher
    {
        public ConcurrentQueue<GgufDownloadStatusHubEvent> Events { get; } = new();

        public Task PublishStatusAsync(GgufDownloadStatusHubEvent statusEvent, CancellationToken cancellationToken = default)
        {
            Events.Enqueue(statusEvent);
            return Task.CompletedTask;
        }
    }
}
