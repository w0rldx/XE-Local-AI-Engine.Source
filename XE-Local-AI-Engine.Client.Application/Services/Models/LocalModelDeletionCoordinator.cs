namespace XE_Local_AI_Engine.Client.Services.Models;

using System.Text.Json;
using System.Text.Json.Serialization;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.HuggingFace.Options;
using XE_Local_AI_Engine.Providers.LlamaServer;

/// <summary>
///     Journalled deletion of an installed model: stage the members, remove the registry aliases, remove the provider-map
///     rows, commit. A failure compensates in reverse, and when that compensation itself fails it is logged and the
///     ORIGINAL failure is rethrown, so the caller keeps its typed reason and the retained journal drives
///     <see cref="ReconcileAsync" /> on the next start.
/// </summary>
public sealed class LocalModelDeletionCoordinator : ILocalModelDeletionCoordinator, ILocalModelDeletionJournalReconciler
{
    private readonly DeletionJournalStore _journals;
    private readonly IInstalledModelSnapshotCoordinator _snapshotCoordinator;
    private readonly IInstalledGgufDeletionStore _deletionStore;
    private readonly ICoordinatedModelProviderMapStore _providerMapStore;
    private readonly ILocalModelProviderResolver _providerResolver;
    private readonly IGgufModelRegistry _modelRegistry;
    private readonly ILogger<LocalModelDeletionCoordinator> _logger;

    public LocalModelDeletionCoordinator(
        IInstalledModelSnapshotCoordinator snapshotCoordinator,
        IInstalledGgufDeletionStore deletionStore,
        ICoordinatedModelProviderMapStore providerMapStore,
        ILocalModelProviderResolver providerResolver,
        IGgufModelRegistry modelRegistry,
        HuggingFaceOptions options,
        ILogger<LocalModelDeletionCoordinator> logger)
    {
        _snapshotCoordinator = snapshotCoordinator;
        _deletionStore = deletionStore;
        _providerMapStore = providerMapStore;
        _providerResolver = providerResolver;
        _modelRegistry = modelRegistry;
        _logger = logger;
        _journals = new(options?.ModelsDirectory
                        ?? throw new ArgumentNullException(nameof(options)), logger);
    }

    public async Task<CommittedModelDeletion> CommitDeleteAsync(string modelName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        await using var lease = await _snapshotCoordinator.AcquireMutationAsync(new InstalledModelMutationRequest { ModelName = modelName, Kind = InstalledModelMutationKind.Delete }, cancellationToken);
        var snapshot = lease.Snapshot ?? throw new KeyNotFoundException("The installed model was not found.");
        await EnsureNoDependentAdaptersAsync(snapshot, cancellationToken);
        var stagePlan = GgufDeletionStageReceipt.Create(ToProviderSnapshot(snapshot), Guid.NewGuid());
        var mappings = await ReadAliasMappingsAsync(lease, stagePlan.RemovalAliases, cancellationToken);
        var journal = DeletionJournal.Create(snapshot, stagePlan, mappings);
        await _journals.WriteAsync(journal, cancellationToken);

        GgufDeletionStageReceipt? staged = null;
        GgufRegistryAliasMutationReceipt? aliasReceipt = null;
        var mapReceipts = new List<ProviderMapMutationReceipt>();
        try
        {
            staged = await _deletionStore.StageAsync(ToProviderSnapshot(snapshot), stagePlan.OperationId, cancellationToken);
            journal = journal with
            {
                Phase = DeletionJournalPhase.Staged,
                StageReceipt = staged
            };
            await _journals.WriteAsync(journal, cancellationToken);

            aliasReceipt = await _deletionStore.RemoveAliasesByLocalPathAsync(staged, staged.RemovalAliases, cancellationToken);
            journal = journal with
            {
                Phase = DeletionJournalPhase.AliasesRemoved,
                RegistryReceipt = aliasReceipt
            };
            await _journals.WriteAsync(journal, cancellationToken);

            foreach (var aliasModelName in staged.RemovalAliases.Select(static alias => alias.ModelName))
            {
                var mapping = mappings.Single(state => string.Equals(state.ModelName, aliasModelName, StringComparison.OrdinalIgnoreCase));
                if (mapping.Mapping is null)
                {
                    continue;
                }

                var result = await _providerMapStore.TryRemoveIfMatchAsync(lease,
                    aliasModelName,
                    LlamaServerProviderConstants.ProviderName,
                    mapping.Mapping.Revision,
                    cancellationToken);
                switch (result)
                {
                    case ProviderMapRemovalResult.Removed removed:
                        mapReceipts.Add(removed.Receipt);
                        journal = journal with
                        {
                            Phase = DeletionJournalPhase.MapsRemoved,
                            ProviderMapReceipts = Array.AsReadOnly(mapReceipts.ToArray())
                        };
                        await _journals.WriteAsync(journal, cancellationToken);
                        break;
                    case ProviderMapRemovalResult.Absent:
                        break;
                    case ProviderMapRemovalResult.Superseded:
                        throw new InstalledModelProviderMapSupersededException();
                }
            }

            _providerResolver.InvalidateModelProviderMap();
            journal = journal with
            {
                Phase = DeletionJournalPhase.Committed,
                ProviderMapReceipts = Array.AsReadOnly(mapReceipts.ToArray())
            };
            await _journals.WriteAsync(journal, cancellationToken);
            return new CommittedModelDeletion
            {
                OperationId = staged.OperationId,
                RequestedModelName = modelName,
                RemovedModelNames = Array.AsReadOnly(staged.RemovalAliases.Select(static alias => alias.ModelName).ToArray()),
                StageReceipt = staged
            };
        }
        catch
        {
            // A failing compensation must never replace the failure that triggered it: the caller needs the original
            // reason (and the endpoint its 409 mapping), while the rollback failure is what the retained journal and
            // this log carry for recovery.
            try
            {
                await RollBackAsync(lease, journal, staged ?? stagePlan, aliasReceipt, mapReceipts, CancellationToken.None);
            }
            catch (Exception rollbackFailure)
            {
                _logger.LogError(rollbackFailure,
                    "Installed-model deletion rollback failed for {ModelName}; the deletion journal is retained for startup recovery.",
                    modelName);
            }

            throw;
        }
    }

    public async Task PurgeAfterSuccessAsync(CommittedModelDeletion committedDeletion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(committedDeletion);
        await _deletionStore.PurgeAsync(committedDeletion.StageReceipt, cancellationToken);
        await _journals.DeleteAsync(committedDeletion.OperationId);
    }

    public async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        foreach (var journal in await _journals.ReadAllValidAsync(cancellationToken))
        {
            var aliasNames = journal.StageReceipt.RemovalAliases.Select(static alias => alias.ModelName).ToArray();
            var intendedMembers = journal.Snapshot.Members.Select(static member =>
                new IntendedInstalledModelMember { RelativePath = member.RelativePath, Role = member.Role }).ToArray();
            await using var lease = await _snapshotCoordinator.AcquireMutationAsync(new InstalledModelMutationRequest
            {
                ModelName = journal.RequestedModelName,
                Kind = InstalledModelMutationKind.Delete,
                IntendedMembers = intendedMembers,
                IntendedModelNames = aliasNames
            }, cancellationToken);
            if (journal.Phase == DeletionJournalPhase.Committed)
            {
                _providerResolver.InvalidateModelProviderMap();
                await _deletionStore.PurgeAsync(journal.StageReceipt, cancellationToken);
                await _journals.DeleteAsync(journal.OperationId);
                continue;
            }

            if (journal.Phase == DeletionJournalPhase.RolledBack)
            {
                await _journals.DeleteAsync(journal.OperationId);
                continue;
            }

            await RollBackAsync(lease,
                journal,
                journal.StageReceipt,
                journal.RegistryReceipt,
                journal.ProviderMapReceipts,
                cancellationToken);
        }
    }

    /// <summary>
    ///     Refuses to delete a base model that installed LoRA adapters launch against. An adapter entry carries no
    ///     weights of its own — it is loaded on top of the base named by its <c>BaseModelName</c> — so removing the base
    ///     would leave every dependent adapter permanently unlaunchable. Checked under the mutation lease and before
    ///     anything is staged, so the refusal has nothing to roll back.
    /// </summary>
    private async Task EnsureNoDependentAdaptersAsync(InstalledModelSnapshot snapshot, CancellationToken cancellationToken)
    {
        var removedNames = snapshot.RegistryAliases.Select(static alias => alias.ModelName).ToArray();
        var entries = await _modelRegistry.ListAsync(cancellationToken);
        var dependents = entries
                         .Where(entry => entry.BaseModelName is { Length: > 0 } baseName
                                         && removedNames.Contains(baseName, StringComparer.OrdinalIgnoreCase)
                                         && !removedNames.Contains(entry.ModelName, StringComparer.OrdinalIgnoreCase))
                         .Select(static entry => entry.ModelName)
                         .ToArray();
        if (dependents.Length == 0)
        {
            return;
        }

        _logger.LogWarning("Refused to delete {ModelName}: {DependentCount} installed adapter(s) apply to it.",
            snapshot.ModelName,
            dependents.Length);
        throw new InstalledModelDependentAdaptersException();
    }

    private async Task<IReadOnlyList<DeletionAliasMapping>> ReadAliasMappingsAsync(InstalledModelMutationLease lease,
        IReadOnlyList<InstalledModelRegistryAliasSnapshot> aliases,
        CancellationToken cancellationToken)
    {
        var result = new List<DeletionAliasMapping>(aliases.Count);
        foreach (var aliasModelName in aliases.Select(static alias => alias.ModelName))
        {
            var mapping = await _providerMapStore.ReadWithRevisionAsync(lease, aliasModelName, cancellationToken);
            if (mapping is not null
                && !string.Equals(mapping.ProviderName, LlamaServerProviderConstants.ProviderName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InstalledModelProviderConflictException();
            }

            result.Add(new DeletionAliasMapping(aliasModelName, mapping));
        }

        return Array.AsReadOnly(result.ToArray());
    }

    private async Task RollBackAsync(InstalledModelMutationLease lease,
        DeletionJournal journal,
        GgufDeletionStageReceipt stageReceipt,
        GgufRegistryAliasMutationReceipt? aliasReceipt,
        IReadOnlyList<ProviderMapMutationReceipt> mapReceipts,
        CancellationToken cancellationToken)
    {
        foreach (var receipt in mapReceipts.Reverse())
        {
            if (await _providerMapStore.TryRestoreAsync(lease, receipt, cancellationToken)
                == ProviderMapRestoreResult.Superseded)
            {
                throw new InstalledModelProviderMapSupersededException();
            }
        }

        await _deletionStore.RestoreAsync(stageReceipt, aliasReceipt, cancellationToken);
        _providerResolver.InvalidateModelProviderMap();
        await _journals.WriteAsync(journal with
        {
            Phase = DeletionJournalPhase.RolledBack
        }, cancellationToken);
        await _journals.DeleteAsync(journal.OperationId);
    }

    private static InstalledGgufSnapshot ToProviderSnapshot(InstalledModelSnapshot snapshot) =>
        new()
        {
            ModelName = snapshot.ModelName,
            RegistryRevision = snapshot.RegistryRevision,
            RegistryAliases = snapshot.RegistryAliases,
            RegistryAliasSetHash = snapshot.RegistryAliasSetHash,
            Members = snapshot.Members,
            PhysicalMemberSetHash = snapshot.PhysicalMemberSetHash,
            Origin = snapshot.Origin,
            RepoId = snapshot.RepoId,
            SourceRevision = snapshot.SourceRevision,
            Quantization = snapshot.Quantization,
            Role = snapshot.Role,
            ModelContentFingerprint = snapshot.ModelContentFingerprint
        };

    private sealed record DeletionAliasMapping(string ModelName, ModelProviderMapRecord? Mapping);

    private enum DeletionJournalPhase
    {
        Prepared,
        Staged,
        AliasesRemoved,
        MapsRemoved,
        Committed,
        RolledBack
    }

    private sealed record DeletionJournal(
        int Version,
        Guid OperationId,
        string RequestedModelName,
        InstalledModelSnapshot Snapshot,
        GgufDeletionStageReceipt StageReceipt,
        IReadOnlyList<DeletionAliasMapping> AliasMappings,
        DeletionJournalPhase Phase,
        GgufRegistryAliasMutationReceipt? RegistryReceipt,
        IReadOnlyList<ProviderMapMutationReceipt> ProviderMapReceipts)
    {
        public static DeletionJournal Create(InstalledModelSnapshot snapshot,
            GgufDeletionStageReceipt stageReceipt,
            IReadOnlyList<DeletionAliasMapping> mappings) =>
            new(1,
                stageReceipt.OperationId,
                snapshot.ModelName,
                snapshot,
                stageReceipt,
                mappings,
                DeletionJournalPhase.Prepared,
                RegistryReceipt: null,
                ProviderMapReceipts: Array.Empty<ProviderMapMutationReceipt>());
    }

    private sealed class DeletionJournalStore
    {
        private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            Converters =
            {
                new JsonStringEnumConverter()
            }
        };

        private readonly string _modelsDirectory;
        private readonly string _root;
        private readonly ILogger _logger;

        public DeletionJournalStore(string modelsDirectory, ILogger logger)
        {
            _modelsDirectory = Path.GetFullPath(modelsDirectory);
            _root = GgufFilePath.ResolveContainedPath(_modelsDirectory, ".operations/delete");
            _logger = logger;
        }

        public async Task WriteAsync(DeletionJournal journal, CancellationToken cancellationToken)
        {
            Validate(journal);
            var directory = OperationDirectory(journal.OperationId);
            Directory.CreateDirectory(directory);
            var target = Path.Combine(directory, "journal.json");
            var temp = Path.Combine(directory, $"journal.{Guid.NewGuid():N}.tmp");
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             bufferSize: 4096, FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, journal, SerializerOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temp, target, overwrite: true);
        }

        public async Task<IReadOnlyList<DeletionJournal>> ReadAllValidAsync(CancellationToken cancellationToken)
        {
            if (!Directory.Exists(_root))
            {
                return [];
            }

            var journals = new List<DeletionJournal>();
            foreach (var path in Directory.EnumerateFiles(_root, "journal.json", SearchOption.AllDirectories))
            {
                try
                {
                    await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    var journal = await JsonSerializer.DeserializeAsync<DeletionJournal>(stream, SerializerOptions, cancellationToken)
                                  ?? throw new JsonException("The deletion journal is empty.");
                    Validate(journal);
                    if (!string.Equals(Path.GetDirectoryName(path), OperationDirectory(journal.OperationId), StringComparison.Ordinal))
                    {
                        throw new JsonException("The deletion journal operation directory does not match its identifier.");
                    }

                    journals.Add(journal);
                }
                catch (Exception exception) when (exception is JsonException or IOException or ArgumentException or InvalidOperationException)
                {
                    var invalidRoot = GgufFilePath.ResolveContainedPath(_modelsDirectory, ".operations/delete-invalid");
                    Directory.CreateDirectory(invalidRoot);
                    var destination = Path.Combine(invalidRoot, $"journal-{Guid.NewGuid():N}.json");
                    File.Move(path, destination, overwrite: false);
                    _logger.LogError("Quarantined an invalid installed-model deletion journal: {Reason}", exception.GetType().Name);
                }
            }

            return Array.AsReadOnly(journals.OrderBy(static journal => journal.OperationId).ToArray());
        }

        public Task DeleteAsync(Guid operationId)
        {
            var directory = OperationDirectory(operationId);
            var journal = Path.Combine(directory, "journal.json");
            if (File.Exists(journal))
            {
                File.Delete(journal);
            }

            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }

            return Task.CompletedTask;
        }

        private string OperationDirectory(Guid operationId) =>
            GgufFilePath.ResolveContainedPath(_modelsDirectory, $".operations/delete/{operationId:N}");

        private static void Validate(DeletionJournal journal)
        {
            var expectedStage = GgufDeletionStageReceipt.Create(ToProviderSnapshot(journal.Snapshot), journal.OperationId);
            if (journal.Version != 1 || journal.OperationId == Guid.Empty || journal.OperationId != journal.StageReceipt.OperationId
                || !string.Equals(journal.Snapshot.RegistryAliasSetHash,
                    GgufRegistryAliasSetHash.ComputeV1(journal.Snapshot.RegistryAliases), StringComparison.Ordinal)
                || !string.Equals(journal.Snapshot.PhysicalMemberSetHash,
                    GgufPhysicalMemberSetHash.ComputeV1(journal.Snapshot.Members), StringComparison.Ordinal)
                || !string.Equals(journal.StageReceipt.RegistryAliasSetHash,
                    GgufRegistryAliasSetHash.ComputeV1(journal.StageReceipt.RemovalAliases), StringComparison.Ordinal)
                || !StageReceiptMatches(expectedStage, journal.StageReceipt)
                || !journal.AliasMappings.Select(static mapping => mapping.ModelName)
                           .OrderBy(static modelName => modelName, StringComparer.OrdinalIgnoreCase)
                           .ThenBy(static modelName => modelName, StringComparer.Ordinal)
                           .SequenceEqual(expectedStage.RemovalAliases.Select(static alias => alias.ModelName), StringComparer.OrdinalIgnoreCase)
                || journal.ProviderMapReceipts.Any(receipt =>
                    expectedStage.RemovalAliases.All(alias =>
                        !string.Equals(alias.ModelName, receipt.ModelName, StringComparison.OrdinalIgnoreCase))
                    || receipt.Prior is null
                    || !string.Equals(receipt.Prior.ProviderName, LlamaServerProviderConstants.ProviderName, StringComparison.OrdinalIgnoreCase)
                    || !receipt.WasRemoval))
            {
                throw new InvalidOperationException("The installed-model deletion journal failed integrity validation.");
            }

            var hasAbsolutePath = journal.Snapshot.Members.Select(static member => member.RelativePath)
                                         .Concat(journal.Snapshot.RegistryAliases.SelectMany(static alias =>
                                             new[]
                                                 {
                                                     alias.WeightRelativePath,
                                                     alias.ProjectorRelativePath,
                                                     alias.SidecarRelativePath
                                                 }
                                                 .OfType<string>()))
                                         .Concat(journal.StageReceipt.StagedMembers.SelectMany(static member =>
                                             new[]
                                             {
                                                 member.OriginalRelativePath,
                                                 member.QuarantineRelativePath
                                             }))
                                         .Any(Path.IsPathRooted);
            if (hasAbsolutePath)
            {
                throw new InvalidOperationException("Deletion journals cannot contain absolute member paths.");
            }
        }

        private static bool StageReceiptMatches(GgufDeletionStageReceipt expected, GgufDeletionStageReceipt actual)
        {
            return string.Equals(expected.RequestedModelName, actual.RequestedModelName, StringComparison.Ordinal)
                   && string.Equals(expected.PhysicalMemberSetHash, actual.PhysicalMemberSetHash, StringComparison.Ordinal)
                   && expected.RemovalAliases.Select(static alias => (alias.ModelName, alias.RegistryRevision))
                              .SequenceEqual(actual.RemovalAliases.Select(static alias => (alias.ModelName, alias.RegistryRevision)))
                   && expected.RetainedMembers.Select(static member => (member.RelativePath, member.Sha256, member.SizeBytes))
                              .SequenceEqual(actual.RetainedMembers.Select(static member => (member.RelativePath, member.Sha256, member.SizeBytes)))
                   && expected.StagedMembers.Select(static member =>
                                  (member.OriginalRelativePath, member.QuarantineRelativePath, member.Member.Sha256, member.Member.SizeBytes))
                              .SequenceEqual(actual.StagedMembers.Select(static member =>
                                  (member.OriginalRelativePath, member.QuarantineRelativePath, member.Member.Sha256, member.Member.SizeBytes)));
        }
    }
}
