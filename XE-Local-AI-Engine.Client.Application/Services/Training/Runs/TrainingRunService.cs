namespace XE_Local_AI_Engine.Client.Services.Training.Runs;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Training.Datasets;

/// <summary>The frozen membership: what trains, what is held back, and the held-back rows' canonical sequences.</summary>
internal sealed class TrainingSplit
{
    public required IReadOnlyList<Guid> Train { get; init; }

    public required IReadOnlyList<Guid> Holdout { get; init; }

    public required IReadOnlyList<int> HoldoutSequences { get; init; }
}

/// <summary>
///     Creation, listing and cancellation of training runs.
/// </summary>
/// <remarks>
///     Creation is where the freeze happens: the dataset is exported through its own canonical writer, hashed, split,
///     and written to an encrypted copy the run owns — all BEFORE the enqueue transaction, which then re-checks the
///     dataset version it was told to expect. A sample edit that slips in between bumps that version and the whole
///     creation is refused, frozen copy included.
/// </remarks>
public interface ITrainingRunService
{
    Task<TrainingRunRecord> CreateAsync(CreateTrainingRunCommand command, CancellationToken cancellationToken = default);

    Task<TrainingRunPage> ListAsync(TrainingRunQuery query, CancellationToken cancellationToken = default);

    Task<TrainingRunRecord?> GetAsync(Guid runId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Requests cancellation. A queued run is terminalized directly; a running one is signalled through the
    ///     executor's registry so the trainer can stop cooperatively and still be recorded as cancelled.
    /// </summary>
    Task<bool> CancelAsync(Guid runId, CancellationToken cancellationToken = default);
}

public sealed class TrainingRunService : ITrainingRunService
{
    private readonly IInstalledBaseModelLinker _linker;
    private readonly TrainingRunCancellationRegistry _cancellations;
    private readonly ITrainingDatasetStore _datasetStore;
    private readonly ITrainingOptionDefaultsCalculator _defaults;
    private readonly IDatasetExportService _exportService;
    private readonly ILicenseGateService _licenseGate;
    private readonly ITrainingRunStore _runStore;
    private readonly ITrainingRunQueueSignal _signal;
    private readonly TrainingRunWorkspace _workspace;

    public TrainingRunService(
        ITrainingRunStore runStore,
        ITrainingDatasetStore datasetStore,
        IDatasetExportService exportService,
        ITrainingOptionDefaultsCalculator defaults,
        ILicenseGateService licenseGate,
        TrainingRunWorkspace workspace,
        TrainingRunCancellationRegistry cancellations,
        ITrainingRunQueueSignal signal,
        IInstalledBaseModelLinker linker)
    {
        ArgumentNullException.ThrowIfNull(linker);
        ArgumentNullException.ThrowIfNull(cancellations);
        ArgumentNullException.ThrowIfNull(datasetStore);
        ArgumentNullException.ThrowIfNull(defaults);
        ArgumentNullException.ThrowIfNull(exportService);
        ArgumentNullException.ThrowIfNull(licenseGate);
        ArgumentNullException.ThrowIfNull(runStore);
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentNullException.ThrowIfNull(workspace);
        _linker = linker;
        _cancellations = cancellations;
        _datasetStore = datasetStore;
        _defaults = defaults;
        _exportService = exportService;
        _licenseGate = licenseGate;
        _runStore = runStore;
        _signal = signal;
        _workspace = workspace;
    }

    public async Task<TrainingRunRecord> CreateAsync(CreateTrainingRunCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!command.LicenseConfirmed)
        {
            throw new TrainingRunRejectedException("The base checkpoint's licensing has to be confirmed before a run can start.");
        }

        var license = await _licenseGate.GetAsync(command.BaseArtifactId, cancellationToken)
                      ?? throw new TrainingRunRejectedException("The base checkpoint was not found.");
        var resolved = await _defaults.ResolveAsync(command.BaseArtifactId, command.Options, cancellationToken);

        var dataset = await _datasetStore.GetDatasetAsync(command.DatasetId, cancellationToken)
                      ?? throw new TrainingRunRejectedException("The training dataset was not found.");
        if (dataset.Status != TrainingDatasetStatus.Ready)
        {
            throw new TrainingRunRejectedException("The training dataset is not ready.");
        }

        var freezeId = Guid.NewGuid();
        var freeze = await MaterializeFreezeAsync(dataset, freezeId, cancellationToken);
        try
        {
            return await EnqueueAsync(command, resolved, license, freeze, cancellationToken);
        }
        catch
        {
            // The enqueue is the only thing that makes a freeze meaningful; an orphan copy is dead plaintext-at-rest.
            _workspace.DeleteFrozenDataset(command.DatasetId, freezeId);
            throw;
        }
    }

    public Task<TrainingRunPage> ListAsync(TrainingRunQuery query, CancellationToken cancellationToken = default) =>
        _runStore.ListAsync(query, cancellationToken);

    public Task<TrainingRunRecord?> GetAsync(Guid runId, CancellationToken cancellationToken = default) =>
        _runStore.GetAsync(runId, cancellationToken);

    public async Task<bool> CancelAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        var run = await _runStore.GetAsync(runId, cancellationToken);
        if (run is null || run.Status is TrainingRunStatus.Succeeded or TrainingRunStatus.Failed or TrainingRunStatus.Cancelled)
        {
            return false;
        }

        // A running trainer is signalled, never terminalized here: the executor owns the process-group SIGTERM, the
        // cooperative stop and the terminal write, so cancelling from two places would race the work item.
        if (_cancellations.Cancel(runId))
        {
            return true;
        }

        _ = await _runStore.CompleteRunAsync(runId, TrainingWorkStatus.Cancelled, "Cancelled before the run started.", cancellationToken);
        _signal.Wake();
        return true;
    }

    /// <summary>
    ///     Stratified holdout split. Stratifying by sample kind keeps every kind represented on both sides — a random
    ///     split on a dataset with one rare kind can put all of it in the holdout and train on none of it.
    /// </summary>
    internal static TrainingSplit Split(IReadOnlyList<TrainingSampleRecord> samples, double holdoutFraction)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var train = new List<Guid>(samples.Count);
        var holdout = new List<Guid>();
        var holdoutSequences = new List<int>();
        foreach (var stratum in samples.GroupBy(static sample => sample.Kind, StringComparer.Ordinal))
        {
            var ordered = stratum.OrderBy(static sample => sample.Sequence).ToArray();
            // Deterministic every-Nth pick rather than a shuffle: the freeze must be reproducible from the same input,
            // and a seeded shuffle would only add a seed to explain.
            var take = (int)Math.Floor(ordered.Length * holdoutFraction);
            var stride = take > 0 ? ordered.Length / take : 0;
            // Counted per stratum, not against the shared list: comparing the global count would let the first kind fill the
            // quota and leave every later kind entirely on the training side — the failure stratifying exists to prevent.
            var taken = 0;
            for (var index = 0; index < ordered.Length; index++)
            {
                if (stride > 0 && taken < take && index % stride == 0)
                {
                    taken++;
                    holdout.Add(ordered[index].Id);
                    holdoutSequences.Add(ordered[index].Sequence);
                    continue;
                }

                train.Add(ordered[index].Id);
            }
        }

        return new TrainingSplit { Train = train, Holdout = holdout, HoldoutSequences = holdoutSequences };
    }

    private async Task<TrainingRunFreezeV1> MaterializeFreezeAsync(TrainingDatasetRecord dataset, Guid freezeId, CancellationToken cancellationToken)
    {
        // The export service is the canonical writer: template-agnostic JSONL with rejected samples excluded.
        // Re-implementing the same line format here would guarantee the two drift apart.
        var canonical = await _exportService.ExportAsync(dataset.Id, DatasetExportFormat.Jsonl, cancellationToken);
        var plaintext = Encoding.UTF8.GetBytes(canonical);
        if (plaintext.Length == 0)
        {
            throw new TrainingRunRejectedException("The dataset has no reviewable samples to train on.");
        }

        var samples = await _datasetStore.ListAllSamplesAsync(dataset.Id, cancellationToken);
        var eligible = samples.Where(static sample => sample.ReviewState != TrainingSampleReviewState.Rejected).ToArray();
        var holdoutFraction = await ResolveHoldoutFractionAsync(dataset, cancellationToken);
        var split = Split(eligible, holdoutFraction);

        await _workspace.WriteFrozenDatasetAsync(dataset.Id, freezeId, plaintext, cancellationToken);
        return new TrainingRunFreezeV1
        {
            FreezeId = freezeId,
            DatasetContentFingerprint = dataset.ContentFingerprint ?? string.Empty,
            DatasetRevision = dataset.Revision,
            // Hash the PLAINTEXT: the ciphertext carries a random nonce, so its digest would differ on every write.
            FrozenCopySha256 = Convert.ToHexStringLower(SHA256.HashData(plaintext)),
            HoldoutFraction = holdoutFraction,
            TrainSampleIds = split.Train,
            HoldoutSampleIds = split.Holdout,
            HoldoutSequences = split.HoldoutSequences
        };
    }

    private async Task<double> ResolveHoldoutFractionAsync(TrainingDatasetRecord dataset, CancellationToken cancellationToken)
    {
        var definition = await _datasetStore.GetDefinitionAsync(dataset.DefinitionId, cancellationToken);
        if (definition is null)
        {
            return DatasetDefinitionBodyV1.DefaultHoldoutFraction;
        }

        DatasetDefinitionBodyV1? body = null;
        try
        {
            body = JsonSerializer.Deserialize<DatasetDefinitionBodyV1>(definition.DefinitionJson.Span, TrainingJson.Options);
        }
        catch (JsonException)
        {
            // A definition body this node can no longer read still has a usable default; the split is not the place
            // to fail a run over it.
        }

        var fraction = body?.HoldoutFraction ?? DatasetDefinitionBodyV1.DefaultHoldoutFraction;
        return Math.Clamp(fraction, DatasetDefinitionBodyV1.MinHoldoutFraction, DatasetDefinitionBodyV1.MaxHoldoutFraction);
    }

    private async Task<TrainingRunRecord> EnqueueAsync(CreateTrainingRunCommand command,
        TrainingRunDefaults resolved,
        TrainingLicenseGateView license,
        TrainingRunFreezeV1 freeze,
        CancellationToken cancellationToken)
    {
        // The installed counterpart is what an adapter is served against and the base side of a comparison; it is recorded at
        // creation so a later re-install under the same name cannot swap the base out — the fingerprint travels with the link.
        var link = await _linker.ResolveAsync(license.RepoId, command.LinkedModelName, cancellationToken);
        var run = await _runStore.CreateAndEnqueueAsync(new TrainingRunEnqueueCommand
        {
            DatasetId = command.DatasetId,
            ExpectedDatasetVersion = command.ExpectedDatasetVersion,
            BaseArtifactId = command.BaseArtifactId,
            FreezeJson = JsonSerializer.SerializeToUtf8Bytes(freeze, TrainingJson.Options),
            OptionsJson = JsonSerializer.SerializeToUtf8Bytes(resolved.Options, TrainingJson.Options),
            LicenseConfirmationJson = JsonSerializer.SerializeToUtf8Bytes(_licenseGate.BuildConfirmation(license), TrainingJson.Options),
            LinkedInstalledModelName = link?.ModelName,
            LinkedModelContentFingerprint = link?.ContentFingerprint
        },
                                     cancellationToken);
        _signal.Wake();
        return run;
    }
}
