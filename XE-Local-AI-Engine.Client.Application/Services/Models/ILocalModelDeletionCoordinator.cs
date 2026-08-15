namespace XE_Local_AI_Engine.Client.Services.Models;

using System.Text.Json;
using System.Text.Json.Serialization;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.HuggingFace.Options;
using XE_Local_AI_Engine.Providers.LlamaServer;

public sealed record CommittedModelDeletion(
    Guid OperationId,
    string RequestedModelName,
    IReadOnlyList<string> RemovedModelNames,
    GgufDeletionStageReceipt StageReceipt);

public interface ILocalModelDeletionCoordinator
{
    Task<CommittedModelDeletion> CommitDeleteAsync(string modelName, CancellationToken cancellationToken = default);
    Task PurgeAfterSuccessAsync(CommittedModelDeletion committedDeletion, CancellationToken cancellationToken = default);
}

public interface ILocalModelDeletionJournalReconciler
{
    Task ReconcileAsync(CancellationToken cancellationToken = default);
}

public sealed class LocalModelDeletionCoordinator(
    IInstalledModelSnapshotCoordinator snapshotCoordinator,
    IInstalledGgufDeletionStore deletionStore,
    ICoordinatedModelProviderMapStore providerMapStore,
    ILocalModelProviderResolver providerResolver,
    IGgufModelRegistry modelRegistry,
    HuggingFaceOptions options,
    ILogger<LocalModelDeletionCoordinator> logger) : ILocalModelDeletionCoordinator, ILocalModelDeletionJournalReconciler
{
    private readonly DeletionJournalStore _journals = new(options?.ModelsDirectory
                                                          ?? throw new ArgumentNullException(nameof(options)), logger);

    public async Task<CommittedModelDeletion> CommitDeleteAsync(string modelName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        await using var lease = await snapshotCoordinator.AcquireMutationAsync(new InstalledModelMutationRequest(modelName, InstalledModelMutationKind.Delete), cancellationToken)
                                                         .ConfigureAwait(false);
        var snapshot = lease.Snapshot ?? throw new KeyNotFoundException("The installed model was not found.");
        await EnsureNoDependentAdaptersAsync(snapshot, cancellationToken).ConfigureAwait(false);
        var stagePlan = GgufDeletionStageReceipt.Create(ToProviderSnapshot(snapshot), Guid.NewGuid());
        var mappings = await ReadAliasMappingsAsync(lease, stagePlan.RemovalAliases, cancellationToken).ConfigureAwait(false);
        var journal = DeletionJournal.Create(snapshot, stagePlan, mappings);
        await _journals.WriteAsync(journal, cancellationToken).ConfigureAwait(false);

        GgufDeletionStageReceipt? staged = null;
        GgufRegistryAliasMutationReceipt? aliasReceipt = null;
        var mapReceipts = new List<ProviderMapMutationReceipt>();
        try
        {
            staged = await deletionStore.StageAsync(ToProviderSnapshot(snapshot), stagePlan.OperationId, cancellationToken).ConfigureAwait(false);
            journal = journal with
            {
                Phase = DeletionJournalPhase.Staged,
                StageReceipt = staged
            };
            await _journals.WriteAsync(journal, cancellationToken).ConfigureAwait(false);

            aliasReceipt = await deletionStore.RemoveAliasesByLocalPathAsync(staged, staged.RemovalAliases, cancellationToken)
                                              .ConfigureAwait(false);
            journal = journal with
            {
                Phase = DeletionJournalPhase.AliasesRemoved,
                RegistryReceipt = aliasReceipt
            };
            await _journals.WriteAsync(journal, cancellationToken).ConfigureAwait(false);

            foreach (var aliasModelName in staged.RemovalAliases.Select(static alias => alias.ModelName))
            {
                var mapping = mappings.Single(state => string.Equals(state.ModelName, aliasModelName, StringComparison.OrdinalIgnoreCase));
                if (mapping.Mapping is null)
                {
                    continue;
                }

                var result = await providerMapStore.TryRemoveIfMatchAsync(lease,
                    aliasModelName,
                    LlamaServerProviderConstants.ProviderName,
                    mapping.Mapping.Revision,
                    cancellationToken).ConfigureAwait(false);
                switch (result)
                {
                    case ProviderMapRemovalResult.Removed removed:
                        mapReceipts.Add(removed.Receipt);
                        journal = journal with
                        {
                            Phase = DeletionJournalPhase.MapsRemoved,
                            ProviderMapReceipts = Array.AsReadOnly(mapReceipts.ToArray())
                        };
                        await _journals.WriteAsync(journal, cancellationToken).ConfigureAwait(false);
                        break;
                    case ProviderMapRemovalResult.Absent:
                        break;
                    case ProviderMapRemovalResult.Superseded:
                        throw new InvalidOperationException("InstalledModelProviderMapSuperseded");
                }
            }

            providerResolver.InvalidateModelProviderMap();
            journal = journal with
            {
                Phase = DeletionJournalPhase.Committed,
                ProviderMapReceipts = Array.AsReadOnly(mapReceipts.ToArray())
            };
            await _journals.WriteAsync(journal, cancellationToken).ConfigureAwait(false);
            return new CommittedModelDeletion(staged.OperationId,
                modelName,
                Array.AsReadOnly(staged.RemovalAliases.Select(static alias => alias.ModelName).ToArray()),
                staged);
        }
        catch
        {
            await RollBackAsync(lease, journal, staged ?? stagePlan, aliasReceipt, mapReceipts, CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }
    }

    public async Task PurgeAfterSuccessAsync(CommittedModelDeletion committedDeletion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(committedDeletion);
        await deletionStore.PurgeAsync(committedDeletion.StageReceipt, cancellationToken).ConfigureAwait(false);
        await _journals.DeleteAsync(committedDeletion.OperationId).ConfigureAwait(false);
    }

    public async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        foreach (var journal in await _journals.ReadAllValidAsync(cancellationToken).ConfigureAwait(false))
        {
            var aliasNames = journal.StageReceipt.RemovalAliases.Select(static alias => alias.ModelName).ToArray();
            var intendedMembers = journal.Snapshot.Members.Select(static member =>
                new IntendedInstalledModelMember(member.RelativePath, member.Role)).ToArray();
            await using var lease = await snapshotCoordinator.AcquireMutationAsync(new InstalledModelMutationRequest(journal.RequestedModelName,
                InstalledModelMutationKind.Delete,
                intendedMembers,
                aliasNames), cancellationToken).ConfigureAwait(false);
            if (journal.Phase == DeletionJournalPhase.Committed)
            {
                providerResolver.InvalidateModelProviderMap();
                await deletionStore.PurgeAsync(journal.StageReceipt, cancellationToken).ConfigureAwait(false);
                await _journals.DeleteAsync(journal.OperationId).ConfigureAwait(false);
                continue;
            }

            if (journal.Phase == DeletionJournalPhase.RolledBack)
            {
                await _journals.DeleteAsync(journal.OperationId).ConfigureAwait(false);
                continue;
            }

            await RollBackAsync(lease,
                journal,
                journal.StageReceipt,
                journal.RegistryReceipt,
                journal.ProviderMapReceipts,
                cancellationToken).ConfigureAwait(false);
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
        var entries = await modelRegistry.ListAsync(cancellationToken).ConfigureAwait(false);
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

        logger.LogWarning("Refused to delete {ModelName}: {DependentCount} installed adapter(s) apply to it.",
            snapshot.ModelName,
            dependents.Length);
        throw new InvalidOperationException("InstalledModelHasDependentAdapters");
    }

    private async Task<IReadOnlyList<DeletionAliasMapping>> ReadAliasMappingsAsync(InstalledModelMutationLease lease,
        IReadOnlyList<InstalledModelRegistryAliasSnapshot> aliases,
        CancellationToken cancellationToken)
    {
        var result = new List<DeletionAliasMapping>(aliases.Count);
        foreach (var aliasModelName in aliases.Select(static alias => alias.ModelName))
        {
            var mapping = await providerMapStore.ReadWithRevisionAsync(lease, aliasModelName, cancellationToken).ConfigureAwait(false);
            if (mapping is not null
                && !string.Equals(mapping.ProviderName, LlamaServerProviderConstants.ProviderName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("InstalledModelProviderConflict");
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
            if (await providerMapStore.TryRestoreAsync(lease, receipt, cancellationToken).ConfigureAwait(false)
                == ProviderMapRestoreResult.Superseded)
            {
                throw new InvalidOperationException("InstalledModelProviderMapSuperseded");
            }
        }

        await deletionStore.RestoreAsync(stageReceipt, aliasReceipt, cancellationToken).ConfigureAwait(false);
        providerResolver.InvalidateModelProviderMap();
        await _journals.WriteAsync(journal with
        {
            Phase = DeletionJournalPhase.RolledBack
        }, cancellationToken).ConfigureAwait(false);
        await _journals.DeleteAsync(journal.OperationId).ConfigureAwait(false);
    }

    private static InstalledGgufSnapshot ToProviderSnapshot(InstalledModelSnapshot snapshot) =>
        new(snapshot.ModelName,
            snapshot.RegistryRevision,
            snapshot.RegistryAliases,
            snapshot.RegistryAliasSetHash,
            snapshot.Members,
            snapshot.PhysicalMemberSetHash,
            snapshot.Origin,
            snapshot.RepoId,
            snapshot.SourceRevision,
            snapshot.Quantization,
            snapshot.Role,
            snapshot.ModelContentFingerprint);

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
                await JsonSerializer.SerializeAsync(stream, journal, SerializerOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
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
                                                      .ConfigureAwait(false)
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

public sealed class LocalModelDeletionStartupReconciler(
    IServiceScopeFactory scopeFactory,
    ILogger<LocalModelDeletionStartupReconciler> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ILocalModelDeletionJournalReconciler>()
                       .ReconcileAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogCritical(exception, "Installed-model deletion recovery failed; installed model mutations are unsafe.");
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
