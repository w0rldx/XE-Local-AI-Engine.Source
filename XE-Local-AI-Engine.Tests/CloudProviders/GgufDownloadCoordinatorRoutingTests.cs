namespace XE_Local_AI_Engine.Tests.CloudProviders;

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.ModelFit;
using XE_Local_AI_Engine.Client.Services.ModelFit.Implementation;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Client.Services.Validation;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Tests.Testing;
using GgufAcquisitionOperationKind = XE_Local_AI_Engine.Client.Services.Models.GgufAcquisitionOperationKind;

/// <summary>
///     Proves the GGUF download path writes the <c>model_provider_map</c> "llamacpp" row for the canonical model name on
///     a successful download — the single production writer that makes a downloaded GGUF reach the llama.cpp runtime
///     regardless of the unmapped-routing default. A failed download writes NO mapping row.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class GgufDownloadCoordinatorRoutingTests
{
    private const string Repo = "bartowski/Qwen2.5-0.5B-Instruct-GGUF";
    private const string Quant = "Q4_K_M";

    [Test]
    public async Task SuccessfulDownload_WritesLlamaCppMapRow_ForCanonicalName()
    {
        var mapStore = new InMemoryCoordinatedModelProviderMapStore();
        var store = new ProvisioningDownloadTransaction();
        var coordinator = BuildCoordinator(store, mapStore);

        var ticket = await coordinator.StartAsync(new GgufModelRequest
        {
            RepoId = Repo,
            Quant = Quant
        }, CancellationToken.None);

        await WaitForPhaseAsync(coordinator, ticket.ModelName, GgufDownloadPhase.Completed);

        var canonical = GgufModelName.Format(Repo, Quant);
        AssertEx.Equal(canonical, ticket.ModelName);
        AssertEx.NotEqual(Guid.Empty, ticket.OperationId);
        AssertEx.Equal("Download", ticket.OperationKind);
        AssertEx.True(mapStore.Mappings.TryGetValue(canonical, out var mapping), "a map row must be written for the canonical name");
        AssertEx.Equal(LlamaServerProviderConstants.ProviderName, mapping!.ProviderName);
    }

    [Test]
    public async Task FailedDownload_WritesNoMapRow()
    {
        var mapStore = new InMemoryCoordinatedModelProviderMapStore();
        var store = new ProvisioningDownloadTransaction
        {
            FailDownload = true
        };
        var coordinator = BuildCoordinator(store, mapStore);

        var ticket = await coordinator.StartAsync(new GgufModelRequest
        {
            RepoId = Repo,
            Quant = Quant
        }, CancellationToken.None);

        await WaitForPhaseAsync(coordinator, ticket.ModelName, GgufDownloadPhase.Failed);

        AssertEx.Equal(expected: 0, mapStore.Mappings.Count);
    }

    [Test]
    public async Task RoutingConflict_FailsWithoutOverwritingOrPublishingCompleted()
    {
        var canonical = GgufModelName.Format(Repo, Quant);
        var mapStore = new InMemoryCoordinatedModelProviderMapStore();
        mapStore.Seed(canonical, "ollama");
        var transaction = new ProvisioningDownloadTransaction();
        var coordinator = BuildCoordinator(transaction, mapStore);

        var ticket = await coordinator.StartAsync(new GgufModelRequest
        {
            RepoId = Repo,
            Quant = Quant
        }, CancellationToken.None);

        await WaitForPhaseAsync(coordinator, ticket.ModelName, GgufDownloadPhase.Failed);

        var status = coordinator.GetStatus(ticket.OperationId);
        AssertEx.Equal("ModelConflict", status!.ErrorCode);
        AssertEx.Equal("ollama", mapStore.Mappings[canonical].ProviderName);
        AssertEx.True(transaction.WasRolledBack);
    }

    [Test]
    public async Task IdenticalDownloadWhileActive_RejoinsExistingOperationWithoutSecondPrepare()
    {
        var transaction = new ProvisioningDownloadTransaction
        {
            BlockPrepare = true
        };
        var coordinator = BuildCoordinator(transaction, new InMemoryCoordinatedModelProviderMapStore());
        var request = new GgufModelRequest
        {
            RepoId = Repo,
            Quant = Quant
        };

        var first = await coordinator.StartAsync(request, CancellationToken.None);
        await transaction.PrepareStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = await coordinator.StartAsync(request, CancellationToken.None);

        AssertEx.True(second.AlreadyInFlight);
        AssertEx.Equal(first.OperationId, second.OperationId);
        AssertEx.Equal(expected: 1, transaction.PrepareCount);
        transaction.ReleasePrepare();
        await WaitForPhaseAsync(coordinator, first.ModelName, GgufDownloadPhase.Completed);
    }

    [Test]
    public async Task DifferentResolvedArtifactWhileSameModelActive_ConflictsWithoutSecondPrepare()
    {
        var transaction = new ProvisioningDownloadTransaction
        {
            BlockPrepare = true
        };
        var coordinator = BuildCoordinator(transaction, new InMemoryCoordinatedModelProviderMapStore());
        var first = await coordinator.StartAsync(new GgufModelRequest
        {
            RepoId = Repo,
            Quant = Quant,
            FileName = "first.gguf"
        }, CancellationToken.None);
        await transaction.PrepareStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var exception = await AssertEx.ThrowsAsync<GgufAcquisitionConflictException>(() => coordinator.StartAsync(new GgufModelRequest
            {
                RepoId = Repo,
                Quant = Quant,
                FileName = "second.gguf"
            },
            CancellationToken.None));

        AssertEx.Equal("The model name or destination is already in use.", exception.Message);
        AssertEx.Equal(expected: 1, transaction.PrepareCount);
        transaction.ReleasePrepare();
        await WaitForPhaseAsync(coordinator, first.ModelName, GgufDownloadPhase.Completed);
    }

    [Test]
    public async Task CancelDuringPrepare_PublishesCancelledAndDiscardsPartialPreparation()
    {
        var transaction = new ProvisioningDownloadTransaction
        {
            BlockPrepare = true
        };
        var coordinator = BuildCoordinator(transaction, new InMemoryCoordinatedModelProviderMapStore());
        var ticket = await coordinator.StartAsync(new GgufModelRequest
        {
            RepoId = Repo,
            Quant = Quant
        }, CancellationToken.None);
        await transaction.PrepareStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        AssertEx.True(coordinator.Cancel(ticket.ModelName));
        transaction.ReleasePrepare();
        await WaitForPhaseAsync(coordinator, ticket.ModelName, GgufDownloadPhase.Cancelled);

        AssertEx.False(transaction.WasCommitted);
    }

    [Test]
    public async Task VerifiedInstalled_ReturnsFreshCompletedTicketWithoutPreparingBytesAndClaimsRouting()
    {
        var transaction = new ProvisioningDownloadTransaction();
        var mapStore = new InMemoryCoordinatedModelProviderMapStore();
        var coordinator = BuildCoordinator(transaction, mapStore, GgufAcquisitionDisposition.VerifiedInstalled);

        var ticket = await coordinator.StartAsync(new GgufModelRequest
        {
            RepoId = Repo,
            Quant = Quant
        }, CancellationToken.None);
        var status = coordinator.GetStatus(ticket.OperationId);

        AssertEx.False(ticket.AlreadyInFlight);
        AssertEx.Equal(GgufDownloadPhase.Completed, status!.Phase);
        AssertEx.Equal(expected: 0, transaction.PrepareCount);
        AssertEx.Equal("llamacpp", mapStore.Mappings[ticket.ModelName].ProviderName);
    }

    [Test]
    public async Task VerifiedInstalled_RoutingConflictCreatesNoOperationOrEvent()
    {
        var canonical = GgufModelName.Format(Repo, Quant);
        var mapStore = new InMemoryCoordinatedModelProviderMapStore();
        mapStore.Seed(canonical, "ollama");
        var publisher = new RecordingPublisher();
        var coordinator = BuildCoordinator(new ProvisioningDownloadTransaction(),
            mapStore,
            GgufAcquisitionDisposition.VerifiedInstalled,
            publisher);

        var exception = await AssertEx.ThrowsAsync<GgufAcquisitionConflictException>(() => coordinator.StartAsync(new GgufModelRequest
            {
                RepoId = Repo,
                Quant = Quant
            },
            CancellationToken.None));

        AssertEx.Equal("The model name or destination is already in use.", exception.Message);
        AssertEx.Equal(expected: 0, coordinator.ListStatuses().Count);
        AssertEx.Equal(expected: 0, publisher.Events.Count);
    }

    [Test]
    public async Task VerifiedInstalled_WhenRoutingVerificationThrows_RestoresCreatedMappingWithoutPublishingStatus()
    {
        var canonical = GgufModelName.Format(Repo, Quant);
        var mapStore = new ControlledMapStore(canonical);
        var publisher = new RecordingPublisher();
        var providerResolver = Substitute.For<ILocalModelProviderResolver>();
        providerResolver.ResolveProviderNameForModelAsync(Arg.Any<string>(),
                            Arg.Any<IModelProviderMapReadLease>(),
                            Arg.Any<CancellationToken>())
                        .Returns(_ => Task.FromException<string>(new IOException("routing read failed")));
        var coordinator = BuildCoordinator(new ProvisioningDownloadTransaction(),
            mapStore,
            GgufAcquisitionDisposition.VerifiedInstalled,
            publisher,
            providerResolver);

        _ = await AssertEx.ThrowsAsync<IOException>(() => coordinator.StartAsync(new GgufModelRequest
            {
                RepoId = Repo,
                Quant = Quant
            },
            CancellationToken.None));

        AssertEx.Equal(expected: 1, mapStore.RestoreCount);
        AssertEx.Equal(expected: 0, coordinator.ListStatuses().Count);
        AssertEx.Equal(expected: 0, publisher.Events.Count);
    }

    [Test]
    public async Task CancelAfterMapClaim_WhenRestoreIsSuperseded_PreservesCommittedArtifactsAndFailsClosed()
    {
        var canonical = GgufModelName.Format(Repo, Quant);
        var mapStore = new ControlledMapStore(canonical)
        {
            BlockClaim = true,
            RestoreResult = ProviderMapRestoreResult.Superseded
        };
        var transaction = new ProvisioningDownloadTransaction();
        var coordinator = BuildCoordinator(transaction, mapStore);

        var ticket = await coordinator.StartAsync(new GgufModelRequest
        {
            RepoId = Repo,
            Quant = Quant
        }, CancellationToken.None);
        await mapStore.ClaimEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        AssertEx.True(coordinator.Cancel(ticket.ModelName));
        mapStore.ReleaseClaim.SetResult();
        await WaitForPhaseAsync(coordinator, ticket.ModelName, GgufDownloadPhase.Failed);

        var status = coordinator.GetStatus(ticket.OperationId)!;
        AssertEx.Equal("DownloadCompensationFailed", status.ErrorCode);
        AssertEx.False(transaction.WasRolledBack);
    }

    [Test]
    public async Task RoutingConflict_WhenCommittedRollbackThrows_PublishesCompensationFailure()
    {
        var canonical = GgufModelName.Format(Repo, Quant);
        var mapStore = new InMemoryCoordinatedModelProviderMapStore();
        mapStore.Seed(canonical, "ollama");
        var transaction = new ProvisioningDownloadTransaction
        {
            ThrowRollback = true
        };
        var coordinator = BuildCoordinator(transaction, mapStore);

        var ticket = await coordinator.StartAsync(new GgufModelRequest
        {
            RepoId = Repo,
            Quant = Quant
        }, CancellationToken.None);
        await WaitForPhaseAsync(coordinator, ticket.ModelName, GgufDownloadPhase.Failed);

        AssertEx.Equal("DownloadCompensationFailed", coordinator.GetStatus(ticket.OperationId)!.ErrorCode);
        AssertEx.True(transaction.WasRolledBack);
    }

    [Test]
    public async Task PartialCommit_WhenRollbackThrows_PublishesCompensationFailure()
    {
        var transaction = new ProvisioningDownloadTransaction
        {
            ThrowPartialCommit = true,
            ThrowRollback = true
        };
        var coordinator = BuildCoordinator(transaction, new InMemoryCoordinatedModelProviderMapStore());

        var ticket = await coordinator.StartAsync(new GgufModelRequest
        {
            RepoId = Repo,
            Quant = Quant
        }, CancellationToken.None);
        await WaitForPhaseAsync(coordinator, ticket.ModelName, GgufDownloadPhase.Failed);

        AssertEx.Equal("DownloadCompensationFailed", coordinator.GetStatus(ticket.OperationId)!.ErrorCode);
        AssertEx.True(transaction.WasRolledBack);
    }

    [Test]
    public async Task SuccessfulDownload_PublishesTransactionPhasesWithStrictlyIncreasingTimestamps()
    {
        var publisher = new RecordingPublisher();
        var coordinator = BuildCoordinator(new ProvisioningDownloadTransaction(),
            new InMemoryCoordinatedModelProviderMapStore(),
            publisher: publisher);

        var ticket = await coordinator.StartAsync(new GgufModelRequest
        {
            RepoId = Repo,
            Quant = Quant
        }, CancellationToken.None);
        await WaitForPhaseAsync(coordinator, ticket.ModelName, GgufDownloadPhase.Completed);

        var events = publisher.Events.ToArray();
        AssertEx.True(events.Select(static value => value.Phase).SequenceEqual(["Validating", "Downloading", "Committing", "Completed"],
            StringComparer.Ordinal));
        AssertEx.True(events.All(static value => value.UpdatedAtUtc is not null));
        AssertEx.True(events.Zip(events.Skip(1), static (left, right) => left.UpdatedAtUtc < right.UpdatedAtUtc).All(static value => value));
    }

    [Test]
    public async Task UnexpectedDetachedFailure_PublishesSanitizedTerminalFailure()
    {
        var mapStore = new InMemoryCoordinatedModelProviderMapStore();
        var coordinator = BuildCoordinator(new ProvisioningDownloadTransaction
        {
            UnexpectedFailure = new UnauthorizedAccessException("/private/models/index.json")
        }, mapStore);

        var ticket = await coordinator.StartAsync(new GgufModelRequest
        {
            RepoId = Repo,
            Quant = Quant
        }, CancellationToken.None);

        await WaitForPhaseAsync(coordinator, ticket.ModelName, GgufDownloadPhase.Failed);

        var status = coordinator.GetStatus(ticket.OperationId);
        AssertEx.Equal("DownloadFailed", status!.ErrorCode);
        AssertEx.False(status.SanitizedError!.Contains("/private", StringComparison.Ordinal));
    }

    private static GgufDownloadCoordinator BuildCoordinator(IGgufDownloadTransaction transaction,
        ICoordinatedModelProviderMapStore mapStore,
        GgufAcquisitionDisposition disposition = GgufAcquisitionDisposition.Available,
        IGgufDownloadEventPublisher? publisher = null,
        ILocalModelProviderResolver? providerResolver = null)
    {
        var identityResolver = new GgufAcquisitionIdentityResolver(new ModelNameValidator(Options.Create(new SecurityOptions())));
        var identity = identityResolver.Resolve(new GgufAcquisitionIntent
        {
            OperationKind = GgufAcquisitionOperationKind.Download,
            ModelBaseName = Repo,
            Quantization = Quant,
            Download = ProvisioningDownloadTransaction.IntentMetadata
        });
        var services = new ServiceCollection();
        services.AddScoped(_ => mapStore);
        services.AddScoped<IGgufAcquisitionPreflight>(_ => new AvailablePreflight(identity, disposition));
        if (providerResolver is null)
        {
            providerResolver = Substitute.For<ILocalModelProviderResolver>();
            providerResolver.ResolveProviderNameForModelAsync(Arg.Any<string>(),
                                Arg.Any<IModelProviderMapReadLease>(),
                                Arg.Any<CancellationToken>())
                            .Returns(LlamaServerProviderConstants.ProviderName);
        }

        services.AddScoped(_ => providerResolver);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        return new GgufDownloadCoordinator(transaction,
            identityResolver,
            scopeFactory,
            new GgufAcquisitionOperationRegistry(TimeProvider.System),
            publisher ?? new NullGgufDownloadEventPublisher(),
            NullLogger<GgufDownloadCoordinator>.Instance,
            TimeProvider.System);
    }

    private static async Task WaitForPhaseAsync(IGgufDownloadCoordinator coordinator, string modelName, GgufDownloadPhase phase)
    {
        // real-timer: the download runs detached and publishes nothing but its status, so the status is the only
        // thing to poll. A fixed attempt count made the bound a budget the box could spend on scheduling alone; this
        // is a failure deadline sized for a contended runner, and a green run returns on the first matching read.
        var deadline = DateTimeOffset.UtcNow + TestBudgets.Contended;
        do
        {
            if (coordinator.GetStatus(modelName)?.Phase == phase)
            {
                return;
            }

            await Task.Delay(20);
        } while (DateTimeOffset.UtcNow < deadline);

        throw new TimeoutException($"Download for '{modelName}' did not reach phase {phase}.");
    }

    private sealed class ProvisioningDownloadTransaction : IGgufDownloadTransaction
    {
        private static readonly string Hash = new('a', 64);

        public static GgufDownloadAcquisitionMetadata IntentMetadata { get; } = new()
        {
            RepoId = Repo,
            ResolvedRevision = "revision",
            SourceDisplayName = "model.gguf",
            DeclaredSizeBytes = 1,
            DeclaredSha256 = Hash,
            Role = GgufRole.Chat
        };

        public bool FailDownload { get; init; }
        public Exception? UnexpectedFailure { get; init; }
        public bool BlockPrepare { get; init; }
        public int PrepareCount { get; private set; }
        public bool WasCommitted { get; private set; }
        public bool WasRolledBack { get; private set; }
        public bool ThrowRollback { get; init; }
        public bool ThrowPartialCommit { get; init; }
        public TaskCompletionSource PrepareStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releasePrepare = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ResolvedGgufDownload> ResolveAsync(GgufModelRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new ResolvedGgufDownload
            {
                ModelBaseName = Repo,
                CanonicalQuant = Quant,
                RepoId = Repo,
                ResolvedRevision = "revision",
                SourceDisplayName = request.FileName ?? "model.gguf",
                SourceSizeBytes = 1,
                SourceSha256 = Hash,
                Role = GgufRole.Chat,
                Projector = null
            });

        public async Task<PreparedGgufDownload> PrepareAsync(ResolvedGgufDownload source,
            GgufDownloadDestination destination,
            IProgress<PullProgress>? progress,
            CancellationToken cancellationToken)
        {
            PrepareCount++;
            PrepareStarted.TrySetResult();
            if (BlockPrepare)
            {
                await _releasePrepare.Task.WaitAsync(cancellationToken);
            }

            if (UnexpectedFailure is not null)
            {
                throw UnexpectedFailure;
            }

            if (FailDownload)
            {
                throw new HuggingFaceDownloadException(HuggingFaceDownloadFailure.Network, "Download failed.");
            }

            var entry = Entry(destination);
            return new PreparedGgufDownload
            {
                OperationId = "operation",
                Source = source,
                Destination = destination,
                TemporaryGgufPath = "/fake/temp.gguf",
                TemporarySidecarPath = "/fake/temp.json",
                TemporaryProjectorPath = null,
                RegistryEntry = entry,
                Sidecar = Sidecar(entry),
                WeightMemberFingerprint = GgufMemberFingerprint.Compute(Hash, 1),
                ProjectorMemberFingerprint = null,
                ModelContentFingerprint = entry.ModelContentFingerprint!
            };
        }

        public Task<GgufDownloadCommitReceipt> CommitAsync(PreparedGgufDownload preparedDownload, CancellationToken cancellationToken)
        {
            WasCommitted = true;
            var receipt = new GgufDownloadCommitReceipt
            {
                RegistryEntry = preparedDownload.RegistryEntry,
                FinalGgufPath = "/fake/final.gguf",
                FinalSidecarPath = "/fake/final.json",
                FinalProjectorPath = null,
                WeightMemberFingerprint = preparedDownload.WeightMemberFingerprint,
                ProjectorMemberFingerprint = null,
                ModelContentFingerprint = preparedDownload.ModelContentFingerprint
            };
            if (ThrowPartialCommit)
            {
                throw new GgufDownloadCommitException(receipt,
                    "The download could not be committed safely.",
                    new IOException("registry failed"));
            }

            return Task.FromResult(receipt);
        }

        public Task RollbackCommittedAsync(GgufDownloadCommitReceipt commitReceipt, CancellationToken cancellationToken)
        {
            WasRolledBack = true;
            if (ThrowRollback)
            {
                throw new IOException("rollback failed");
            }

            return Task.CompletedTask;
        }

        public Task DiscardPreparedAsync(PreparedGgufDownload preparedDownload, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public void ReleasePrepare() =>
            _releasePrepare.TrySetResult();

        private static GgufModelRegistryEntry Entry(GgufDownloadDestination destination)
        {
            var fingerprint = GgufModelContentFingerprint.ComputeV1([
                new GgufModelContentMember
                {
                    RelativePath = destination.RelativeGgufPath,
                    Role = InstalledModelPhysicalMemberRole.Weight,
                    SizeBytes = 1,
                    Sha256 = Hash,
                    OwningAliases = [destination.CanonicalModelName]
                }
            ]);
            return new GgufModelRegistryEntry
            {
                RegistryRevision = $"v1:{Hash}",
                Origin = LocalModelOrigin.HuggingFace,
                ModelName = destination.CanonicalModelName,
                RepoId = Repo,
                FileName = destination.RelativeGgufPath,
                Quant = Quant,
                LocalPath = "/fake/final.gguf",
                SizeBytes = 1,
                Sha256 = Hash,
                SourceRevision = "revision",
                DownloadedAtUtc = DateTimeOffset.UnixEpoch,
                Role = GgufRole.Chat,
                SourceDisplayName = "model.gguf",
                MetadataSchemaVersion = GgufAcquisitionMetadata.CurrentSchemaVersion,
                ModelContentFingerprint = fingerprint
            };
        }

        private static GgufAcquisitionMetadata Sidecar(GgufModelRegistryEntry entry) =>
            new()
            {
                SchemaVersion = GgufAcquisitionMetadata.CurrentSchemaVersion,
                RegistryRevision = entry.RegistryRevision!,
                ModelName = entry.ModelName,
                Origin = LocalModelOrigin.HuggingFace,
                LocalFileName = entry.FileName,
                Quantization = entry.Quant,
                WeightContentSha256 = Hash,
                WeightSizeBytes = 1,
                WeightMemberFingerprint = GgufMemberFingerprint.Compute(Hash, 1),
                SourceDisplayName = "model.gguf",
                AcquiredAtUtc = DateTimeOffset.UnixEpoch,
                RegistryRepoId = Repo,
                RegistrySourceRevision = "revision",
                Role = GgufRole.Chat,
                ModelContentFingerprint = entry.ModelContentFingerprint!
            };
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

    private sealed class ControlledMapStore : ICoordinatedModelProviderMapStore
    {
        public ControlledMapStore(string modelName)
        {
            _receipt = new(modelName,
                Prior: null,
                new ModelProviderMapRecord(modelName, LlamaServerProviderConstants.ProviderName, 1, "revision"),
                WasRemoval: false);
        }

        // Not a reconciliation fixture: only the external-provider pass enumerates the whole map, and this double
        // exists to drive a single model's leased path.
        public Task<IReadOnlyList<ModelProviderMapRecord>> ListAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("This test double does not enumerate the provider map.");

        private readonly ProviderMapMutationReceipt _receipt;

        public bool BlockClaim { get; init; }
        public ProviderMapRestoreResult RestoreResult { get; init; } = ProviderMapRestoreResult.Restored;
        public int RestoreCount { get; private set; }
        public TaskCompletionSource ClaimEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseClaim { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ModelProviderMapRecord?> ReadWithRevisionAsync(IModelProviderMapReadLease lease,
            string modelName,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ModelProviderMapRecord?>(_receipt.Mutation);

        public async Task<ProviderMapClaimResult> TryClaimLlamaCppAsync(IModelProviderMapMutationLease lease,
            string modelName,
            CancellationToken cancellationToken = default)
        {
            ClaimEntered.TrySetResult();
            if (BlockClaim)
            {
                await ReleaseClaim.Task;
            }

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
            CancellationToken cancellationToken = default)
        {
            RestoreCount++;
            return Task.FromResult(RestoreResult);
        }

        public Task<ProviderMapRemovalResult> TryRemoveIfMatchAsync(IModelProviderMapMutationLease lease,
            string modelName,
            string expectedProvider,
            string expectedRevision,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class AvailablePreflight : IGgufAcquisitionPreflight
    {
        private readonly KeyedCompositeLockDomain _domain = new();
        private readonly ResolvedGgufAcquisitionIdentity _identity;
        private readonly GgufAcquisitionDisposition _disposition;

        public AvailablePreflight(ResolvedGgufAcquisitionIdentity identity, GgufAcquisitionDisposition disposition)
        {
            _identity = identity;
            _disposition = disposition;
        }

        [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
            Justification = "PreparedGgufAcquisition owns the lease returned to the production coordinator.")]
        public async Task<PreparedGgufAcquisition> ResolveAndReserveAsync(GgufAcquisitionIntent intent,
            CancellationToken cancellationToken = default)
        {
            var request = new InstalledModelMutationRequest
            {
                ModelName = _identity.CanonicalModelName,
                Kind = InstalledModelMutationKind.Acquire,
                IntendedMembers =
                [
                    new()
                    {
                        RelativePath = _identity.RelativeGgufPath,
                        Role = InstalledModelPhysicalMemberRole.Weight
                    },
                    new()
                    {
                        RelativePath = _identity.RelativeSidecarPath,
                        Role = InstalledModelPhysicalMemberRole.Sidecar
                    }
                ]
            };
            var keys = new[]
            {
                ModelCoordinationKeys.Model(_identity.CanonicalModelName),
                ModelCoordinationKeys.Path(_identity.RelativeGgufPath),
                ModelCoordinationKeys.Path(_identity.RelativeSidecarPath),
                ModelCoordinationKeys.ProviderMap(_identity.CanonicalModelName)
            };
            var inner = await _domain.AcquireMutationAsync(keys, cancellationToken);
            var lease = new InstalledModelMutationLease(request, snapshot: null, providerMapping: null, inner);
            return new PreparedGgufAcquisition(_identity, _disposition, ProviderMapDisposition.Absent, lease, activeOperationId: null);
        }
    }
}
