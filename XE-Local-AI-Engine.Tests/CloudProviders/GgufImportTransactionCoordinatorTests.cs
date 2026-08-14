namespace XE_Local_AI_Engine.Tests.CloudProviders;

using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Client.Services.ModelFit;
using XE_Local_AI_Engine.Client.Services.ModelFit.Implementation;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Client.Services.Validation;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.HuggingFace.Contracts;
using XE_Local_AI_Engine.Providers.HuggingFace.Options;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class GgufImportTransactionCoordinatorTests
{
    [Test]
    public async Task Preview_ReturnsSafeMetadataWithoutAbsoluteSourcePath()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), "private", "example-Q4_K_M.gguf");
        var coordinator = BuildCoordinator(sourcePath);

        var result = await coordinator.PreviewAsync(sourcePath);
        var serialized = System.Text.Json.JsonSerializer.Serialize(result);

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

        var exception = await Assert.ThrowsAsync<GgufImportApplicationException>(() => coordinator.StartAsync(
            new StartGgufImportCommand(sourcePath + ".replacement",
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
        var second = first with { SourceIdentityToken = $"v1:{new string('b', 64)}" };
        var coordinator = BuildCoordinator(sourcePath, inspector: new SequenceInspector(first, second));
        var preview = await coordinator.PreviewAsync(sourcePath);

        var exception = await Assert.ThrowsAsync<GgufImportApplicationException>(() => coordinator.StartAsync(
            new StartGgufImportCommand(sourcePath,
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
            Rejections = new[] { GgufImportRejectionCode.QuantizationRequired }
        };
        var coordinator = BuildCoordinator(sourcePath, inspection: inspection);

        var result = await coordinator.PreviewAsync(sourcePath);

        AssertEx.True(result.CanonicalQuantizationChoices.Count > 0);
        AssertEx.True(result.CanonicalQuantizationChoices.Contains("Q4_K_M", StringComparer.Ordinal));
        AssertEx.True(result.CanonicalQuantizationChoices.SequenceEqual(
            GgufAcquisitionIdentityResolver.CanonicalQuantizationChoices,
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

        var exception = await Assert.ThrowsAsync<GgufImportApplicationException>(() => coordinator.StartAsync(
            new StartGgufImportCommand(sourcePath,
                preview.PreviewToken,
                preview.ModelBaseName,
                "Q5_K_M")));

        AssertEx.Equal("UnsupportedQuantization", exception!.ErrorCode);
    }

    [Test]
    public async Task Cancel_DuringCommit_RollsBackAndNeverPublishesCompleted()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), "private", "example-Q4_K_M.gguf");
        var security = Options.Create(new SecurityOptions());
        var resolver = new GgufAcquisitionIdentityResolver(new ModelNameValidator(security));
        var identity = resolver.Resolve(new GgufAcquisitionIntent(
            XE_Local_AI_Engine.Client.Services.Models.GgufAcquisitionOperationKind.Import,
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

    private static GgufImportTransactionCoordinator BuildCoordinator(string sourcePath,
        GgufImportInspection? inspection = null,
        long availableBytes = long.MaxValue,
        long diskMarginBytes = 0,
        IGgufModelImporter? importer = null,
        GgufAcquisitionIdentityResolver? resolver = null,
        ServiceProvider? services = null,
        IGgufImportInspector? inspector = null)
    {
        var security = Options.Create(new SecurityOptions());
        services ??= new ServiceCollection().BuildServiceProvider();
        return new GgufImportTransactionCoordinator(
            inspector ?? new AcceptedInspector(inspection ?? AcceptedInspection(Path.GetFileName(sourcePath))),
            importer ?? new UnusedImporter(),
            resolver ?? new GgufAcquisitionIdentityResolver(new ModelNameValidator(security)),
            new GgufAcquisitionOperationRegistry(TimeProvider.System),
            services.GetRequiredService<IServiceScopeFactory>(),
            new NullGgufDownloadEventPublisher(),
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

    private static GgufImportInspection AcceptedInspection(string displayName) => new(42,
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
        public Task<GgufImportInspection> InspectAsync(GgufImportSource source, CancellationToken cancellationToken) =>
            Task.FromResult(inspection);
    }

    private sealed class SequenceInspector(params GgufImportInspection[] inspections) : IGgufImportInspector
    {
        private int _index;

        public Task<GgufImportInspection> InspectAsync(GgufImportSource source, CancellationToken cancellationToken)
        {
            var index = Math.Min(Interlocked.Increment(ref _index) - 1, inspections.Length - 1);
            return Task.FromResult(inspections[index]);
        }
    }

    private sealed class FixedFreeSpaceProbe(long availableBytes) : IFreeSpaceProbe
    {
        public long GetAvailableFreeBytes(string path) => availableBytes;
    }

    private sealed class UnusedImporter : IGgufModelImporter
    {
        public Task<PreparedGgufImport> PrepareAsync(GgufImportSource source,
            GgufImportDestination destination,
            IProgress<GgufImportProgress>? progress,
            CancellationToken cancellationToken) => throw new NotSupportedException();

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
            return new GgufImportCommitReceipt(_entry,
                "final.gguf",
                "final.xe-model.json",
                preparedImport.WeightMemberFingerprint,
                preparedImport.ModelContentFingerprint);
        }

        public Task RollbackCommittedAsync(GgufImportCommitReceipt commitReceipt, CancellationToken cancellationToken)
        {
            RollbackCount++;
            return Task.CompletedTask;
        }

        public Task DiscardPreparedAsync(PreparedGgufImport preparedImport, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
