namespace XE_Local_AI_Engine.Client.Services.Training.Export;

using System.Security.Cryptography;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

/// <summary>
///     Commits a smoke-passed, quality-approved staged artifact into the local model registry, with its lineage attached.
/// </summary>
/// <remarks>
///     <para>
///         The commit runs through the SAME acquisition preflight and importer every local import uses — name
///         reservation under the installed-model mutation lease, a strict re-inspection of the bytes, an atomic
///         sidecar-then-weight move, and the registry insert. Nothing here writes the registry directly: a trained
///         model that skipped those steps would be a second, weaker path to the exact invariants they exist to hold.
///     </para>
///     <para>
///         The two shapes differ only in what the destination carries. A merged model is a standalone entry. An
///         adapter entry has no weights of its own — its own bytes ARE the adapter — so it names the installed base
///         model it will be launched against with <c>--lora</c>, and a run with no linked installed model cannot
///         produce one at all.
///     </para>
/// </remarks>
public sealed class ArtifactPromotionService(
    ITrainingRunStore runStore,
    ITrainingBaseArtifactStore baseArtifacts,
    IGgufModelStore models,
    IGgufAcquisitionPreflight preflight,
    IGgufModelImporter importer,
    ILogger<ArtifactPromotionService> logger) : IArtifactPromotionService
{
    private readonly ITrainingBaseArtifactStore _baseArtifacts = baseArtifacts ?? throw new ArgumentNullException(nameof(baseArtifacts));
    private readonly IGgufModelImporter _importer = importer ?? throw new ArgumentNullException(nameof(importer));
    private readonly ILogger<ArtifactPromotionService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IGgufModelStore _models = models ?? throw new ArgumentNullException(nameof(models));
    private readonly IGgufAcquisitionPreflight _preflight = preflight ?? throw new ArgumentNullException(nameof(preflight));
    private readonly ITrainingRunStore _runStore = runStore ?? throw new ArgumentNullException(nameof(runStore));

    public async Task<string> PromoteAsync(Guid artifactId, string modelName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(modelName))
        {
            throw new TrainingExportRejectedException("A model name is required.");
        }

        var artifact = await _runStore.GetArtifactAsync(artifactId, cancellationToken).ConfigureAwait(false)
                       ?? throw new TrainingExportRejectedException("The artifact was not found.");
        if (artifact.CommittedModelName is { } existing)
        {
            throw new TrainingExportRejectedException($"The artifact is already registered as '{existing}'.");
        }

        if (artifact.DiscardedAtUtc is not null)
        {
            throw new TrainingExportRejectedException("The staged artifact was discarded and is retained only as a quality audit tombstone.");
        }

        // Stricter than the store, deliberately. The store lets an explicitly SKIPPED artifact out because a skip is
        // an operator decision in general; the only thing that skips a TRAINED artifact is the export's own
        // architecture rejection, and that file could not be committed by the importer anyway. Refusing here turns a
        // confusing late inspection failure into an answer the operator can act on.
        if (artifact.SmokeState != TrainingArtifactSmokeState.Passed)
        {
            throw new TrainingExportRejectedException("The artifact has not passed its smoke test.");
        }

        if (artifact.Kind == TrainingArtifactKind.HfAdapterDir || !File.Exists(artifact.Path))
        {
            throw new TrainingExportRejectedException("Only a staged GGUF export can be registered.");
        }

        var decision = ArtifactQualityService.ReadDecision(artifact);
        if (decision?.PolicyVersion != ArtifactQualityDecisionV1.CurrentPolicyVersion
            || decision.Outcome is not (ArtifactQualityOutcome.Passed or ArtifactQualityOutcome.Overridden)
            || decision.ArtifactId != artifact.Id
            || decision.ComparisonId != artifact.QualityComparisonId
            || decision.Outcome == ArtifactQualityOutcome.Passed && decision.FailureCodes.Count != 0
            || decision.Outcome == ArtifactQualityOutcome.Overridden
            && (string.IsNullOrWhiteSpace(decision.OverrideReason) || decision.OverriddenAtUtc is null))
        {
            throw new TrainingExportRejectedException("The artifact has no current successful quality decision or audited override.");
        }

        await EnsureInstalledBaseAsync(artifact, cancellationToken).ConfigureAwait(false);

        var currentDigest = await ComputeSha256Async(artifact.Path, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(currentDigest, artifact.Sha256, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(currentDigest, decision.ArtifactSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new TrainingExportRejectedException("The staged artifact changed after its quality decision.");
        }

        var lineage = await BuildLineageAsync(artifact, cancellationToken).ConfigureAwait(false);
        var quantization = TrainingExportPaths.QuantizationOf(artifact.Path)
                           ?? throw new TrainingExportRejectedException("The staged file does not carry a recognizable quantization.");

        await using var reservation = await ReserveAsync(modelName, quantization, cancellationToken).ConfigureAwait(false);
        var identity = reservation.Identity;
        var destination = new GgufImportDestination(identity.CanonicalModelName,
            identity.CanonicalQuantization,
            identity.RelativeGgufPath,
            identity.RelativeSidecarPath,
            LocalModelOrigin.Trained,
            ProjectorRelativePath: null,
            lineage);

        var prepared = await _importer.PrepareAsync(new GgufImportSource(artifact.Path), destination, progress: null, cancellationToken)
                                      .ConfigureAwait(false);
        if (!string.Equals(prepared.RegistryEntry.Sha256, artifact.Sha256, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(prepared.RegistryEntry.Sha256, decision.ArtifactSha256, StringComparison.OrdinalIgnoreCase)
            || prepared.RegistryEntry.SizeBytes != artifact.SizeBytes)
        {
            var rejection = new TrainingExportRejectedException("The staged artifact changed while the registry import was being prepared.");
            try
            {
                await _importer.DiscardPreparedAsync(prepared, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception cleanupException)
            {
                throw new AggregateException("The changed staged artifact was rejected, but its prepared import could not be discarded.",
                    rejection,
                    cleanupException);
            }

            throw rejection;
        }

        GgufImportCommitReceipt receipt;
        try
        {
            // Not cancellable: between the sidecar move and the registry insert there is no state a cancel could
            // leave that is better than a completed commit.
            receipt = await _importer.CommitAsync(prepared, CancellationToken.None).ConfigureAwait(false);
        }
        catch (GgufImportCommitException exception)
        {
            try
            {
                await _importer.RollbackCommittedAsync(exception.CommitReceipt, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception rollbackException)
            {
                throw new AggregateException("The registry import failed after creating final artifacts, and its rollback also failed.",
                    exception,
                    rollbackException);
            }

            throw;
        }
        catch
        {
            await _importer.DiscardPreparedAsync(prepared, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        try
        {
            _ = await _runStore.SetArtifactCommittedNameAsync(artifact.Id, artifact.Version, receipt.RegistryEntry.ModelName,
                                   CancellationToken.None)
                               .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // The registry now owns a model the run does not know about. Roll it back rather than leave a promoted
            // model no operator can trace or a run whose delete would orphan it.
            _logger.LogError(exception, "The promotion of artifact {ArtifactId} could not be recorded; rolling the registry entry back.", artifact.Id);
            try
            {
                await _importer.RollbackCommittedAsync(receipt, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception rollbackException)
            {
                throw new ArtifactPromotionCompensationException(receipt, exception, rollbackException);
            }

            throw;
        }

        return receipt.RegistryEntry.ModelName;
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920, useAsync: true);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    private async Task EnsureInstalledBaseAsync(TrainingArtifactRecord artifact, CancellationToken cancellationToken)
    {
        var run = await _runStore.GetAsync(artifact.RunId, cancellationToken).ConfigureAwait(false)
                  ?? throw new TrainingExportRejectedException("The run behind this artifact was not found.");
        var baseName = run.LinkedInstalledModelName
                       ?? throw new TrainingExportRejectedException("This run has no installed base counterpart, so the artifact cannot be promoted.");
        var installed = await _models.ListInstalledModelsAsync(cancellationToken).ConfigureAwait(false);
        if (!installed.Any(model => model.IsAvailable
                                    && string.Equals(model.ModelName, baseName, StringComparison.Ordinal)
                                    && string.Equals(model.ModelContentFingerprint, run.LinkedModelContentFingerprint, StringComparison.Ordinal)))
        {
            throw new TrainingExportRejectedException("The exact installed base counterpart used by the quality comparison is no longer available.");
        }
    }

    private async Task<PreparedGgufAcquisition> ReserveAsync(string modelName, string quantization, CancellationToken cancellationToken)
    {
        try
        {
            return await _preflight.ResolveAndReserveAsync(new GgufAcquisitionIntent(GgufAcquisitionOperationKind.Import, modelName.Trim(), quantization),
                                       cancellationToken)
                                   .ConfigureAwait(false);
        }
        catch (ArgumentException exception)
        {
            throw new TrainingExportRejectedException("The model name is not usable as a registry name.", exception);
        }
        catch (InvalidOperationException exception)
        {
            throw new TrainingExportRejectedException("A model with that name is already installed.", exception);
        }
    }

    private async Task<TrainedModelLineage> BuildLineageAsync(TrainingArtifactRecord artifact, CancellationToken cancellationToken)
    {
        var run = await _runStore.GetAsync(artifact.RunId, cancellationToken).ConfigureAwait(false)
                  ?? throw new TrainingExportRejectedException("The run behind this artifact was not found.");
        string? baseModelName = null;
        if (artifact.Kind == TrainingArtifactKind.AdapterGguf)
        {
            baseModelName = run.LinkedInstalledModelName is { Length: > 0 } linked
                ? linked
                : throw new TrainingExportRejectedException("This run is not linked to an installed model, so its adapter has no base model to be applied to. Export a merged model instead.");
        }

        var checkpoint = await _baseArtifacts.GetAsync(run.BaseArtifactId, cancellationToken).ConfigureAwait(false);
        return new TrainedModelLineage(checkpoint?.RepoId,
            checkpoint?.Revision,
            // Nullable by contract: a run created before the linked-model fingerprint was recorded still has full
            // checkpoint lineage, and refusing the promotion over the missing half would help nobody.
            run.LinkedModelContentFingerprint,
            baseModelName);
    }
}

public sealed class ArtifactPromotionCompensationException(
    GgufImportCommitReceipt commitReceipt,
    Exception persistenceFailure,
    Exception rollbackFailure)
    : AggregateException("The promoted registry entry could not be recorded or rolled back; recovery receipt evidence is attached.",
        persistenceFailure,
        rollbackFailure)
{
    public GgufImportCommitReceipt CommitReceipt { get; } = commitReceipt;
}
