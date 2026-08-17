namespace XE_Local_AI_Engine.Client.Services.Training.Runs;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Training.BaseArtifacts;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;

/// <summary>
///     Computes the run wizard's starting options from the base checkpoint and the box, and validates an operator's
///     overrides against the same estimate.
/// </summary>
/// <remarks>
///     Defaults step DOWN until they fit — sequence length first, then batch size — because those two are the only
///     levers that move the activation term, and a shorter sequence costs less accuracy than a run that never starts.
///     Overrides are NOT stepped down: an operator who names a configuration gets that configuration or a refusal,
///     never a silently different one that would make the recorded hyper-parameters a lie.
/// </remarks>
public interface ITrainingOptionDefaultsCalculator
{
    /// <summary>The computed defaults for a base checkpoint, or a refusal when nothing fits.</summary>
    Task<TrainingRunDefaults> ComputeAsync(Guid baseArtifactId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Re-estimates against operator-supplied options and hard-rejects a configuration that does not fit.
    /// </summary>
    /// <exception cref="TrainingRunRejectedException">The options do not fit the box, or the checkpoint is unreadable.</exception>
    Task<TrainingRunDefaults> ResolveAsync(Guid baseArtifactId, TrainingRunOptionsV1? requested, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Re-estimates a run's footprint at launch time. The estimate is deliberately not persisted at creation: a run
    ///     can sit queued for hours, and the reservation has to be sized against the same numbers the admission gate is
    ///     about to compare with live free VRAM.
    /// </summary>
    Task<TrainingFootprintEstimate> EstimateAsync(Guid baseArtifactId, TrainingRunOptionsV1 options, CancellationToken cancellationToken = default);
}

public sealed class TrainingOptionDefaultsCalculator(
    ITrainingBaseArtifactStore store,
    INodeDataDirectory dataDirectory,
    IRuntimeDeviceAudit deviceAudit) : ITrainingOptionDefaultsCalculator
{
    /// <summary>Sequence-length ladder, longest first. Powers of two: every attention kernel on this stack likes them.</summary>
    private static readonly int[] SequenceLadder = [4096, 2048, 1024, 512];

    private static readonly int[] BatchLadder = [2, 1];

    private const long SmallModelParameterThreshold = 3_000_000_000L;

    private readonly IRuntimeDeviceAudit _deviceAudit = deviceAudit ?? throw new ArgumentNullException(nameof(deviceAudit));
    private readonly INodeDataDirectory _dataDirectory = dataDirectory ?? throw new ArgumentNullException(nameof(dataDirectory));
    private readonly ITrainingBaseArtifactStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public async Task<TrainingRunDefaults> ComputeAsync(Guid baseArtifactId, CancellationToken cancellationToken = default)
    {
        var (config, parameterCount) = await ReadCheckpointAsync(baseArtifactId, cancellationToken).ConfigureAwait(false);
        var profile = await _deviceAudit.GetEffectiveProfileAsync(forceRefreshProfile: false, cancellationToken).ConfigureAwait(false);
        var budget = AvailableVramBytes(profile);
        var seed = Seed(parameterCount);

        foreach (var sequenceLength in SequenceLadder)
        {
            foreach (var batchSize in BatchLadder)
            {
                var candidate = seed with
                {
                    MaxSeqLength = sequenceLength,
                    PerDeviceTrainBatchSize = batchSize,
                    // Fewer samples per step is compensated by more steps per update, so the effective batch holds.
                    GradientAccumulationSteps = batchSize == 1 ? 8 : 4
                };
                var estimate = TrainingFootprintEstimator.Estimate(parameterCount, config, candidate);
                if (estimate.GpuBytes <= budget)
                {
                    return new TrainingRunDefaults(candidate, estimate, budget, profile.VramKnown, Fits: true, RejectionReason: null);
                }
            }
        }

        var smallest = seed with
        {
            MaxSeqLength = SequenceLadder[^1],
            PerDeviceTrainBatchSize = BatchLadder[^1],
            GradientAccumulationSteps = 8
        };
        var floor = TrainingFootprintEstimator.Estimate(parameterCount, config, smallest);
        return new TrainingRunDefaults(smallest,
            floor,
            budget,
            profile.VramKnown,
            Fits: false,
            RejectionReason: profile.VramKnown
                ? "This checkpoint does not fit the available VRAM even at the smallest sequence length and batch size."
                : "No usable GPU was detected. Training requires CUDA VRAM this node can measure.");
    }

    public async Task<TrainingRunDefaults> ResolveAsync(Guid baseArtifactId,
        TrainingRunOptionsV1? requested,
        CancellationToken cancellationToken = default)
    {
        if (requested is null)
        {
            var computed = await ComputeAsync(baseArtifactId, cancellationToken).ConfigureAwait(false);
            return computed.Fits ? computed : throw new TrainingRunRejectedException(computed.RejectionReason!);
        }

        Validate(requested);
        var (config, parameterCount) = await ReadCheckpointAsync(baseArtifactId, cancellationToken).ConfigureAwait(false);
        var profile = await _deviceAudit.GetEffectiveProfileAsync(forceRefreshProfile: false, cancellationToken).ConfigureAwait(false);
        var budget = AvailableVramBytes(profile);
        var estimate = TrainingFootprintEstimator.Estimate(parameterCount, config, requested);
        if (estimate.GpuBytes > budget)
        {
            throw new TrainingRunRejectedException(
                $"The selected options need about {estimate.GpuBytes / (1024 * 1024)} MB of VRAM and only {budget / (1024 * 1024)} MB is available. Lower the sequence length or the batch size.");
        }

        return new TrainingRunDefaults(requested, estimate, budget, profile.VramKnown, Fits: true, RejectionReason: null);
    }

    public async Task<TrainingFootprintEstimate> EstimateAsync(Guid baseArtifactId,
        TrainingRunOptionsV1 options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var (config, parameterCount) = await ReadCheckpointAsync(baseArtifactId, cancellationToken).ConfigureAwait(false);
        return TrainingFootprintEstimator.Estimate(parameterCount, config, options);
    }

    private static void Validate(TrainingRunOptionsV1 options)
    {
        // Boundary validation, not taste: every one of these reaches a subprocess argument or a tensor shape.
        if (options.MaxSeqLength is < 128 or > 32768
            || options.LoraR is < 1 or > 256
            || options.LoraAlpha is < 1 or > 512
            || options.LoraDropout is < 0 or > 0.5
            || options.PerDeviceTrainBatchSize is < 1 or > 64
            || options.GradientAccumulationSteps is < 1 or > 256
            || options.LearningRate is <= 0 or > 1
            || options.WarmupRatio is < 0 or > 0.5
            || options.Epochs is < 1 or > 50)
        {
            throw new TrainingRunRejectedException("One or more training options are outside their supported range.");
        }

        if (!string.Equals(options.Optimizer, "adamw_8bit", StringComparison.Ordinal))
        {
            throw new TrainingRunRejectedException("Only the adamw_8bit optimizer is supported.");
        }
    }

    private static TrainingRunOptionsV1 Seed(long parameterCount) =>
        new()
        {
            // A small model can afford the extra adapter capacity; a large one spends that VRAM on activations instead.
            LoraR = parameterCount is > 0 and < SmallModelParameterThreshold ? 32 : 16,
            LoraAlpha = parameterCount is > 0 and < SmallModelParameterThreshold ? 32 : 16,
            LoraDropout = 0
        };

    private static long AvailableVramBytes(HardwareProfile profile) =>
        profile.VramKnown ? profile.AvailableVramBytes ?? 0 : 0;

    private async Task<CheckpointFacts> ReadCheckpointAsync(Guid baseArtifactId,
        CancellationToken cancellationToken)
    {
        var artifact = await _store.GetAsync(baseArtifactId, cancellationToken).ConfigureAwait(false)
                       ?? throw new TrainingRunRejectedException("The base checkpoint was not found.");
        if (artifact.Status != TrainingBaseArtifactStatus.Ready)
        {
            throw new TrainingRunRejectedException("The base checkpoint has not finished downloading.");
        }

        var files = BaseArtifactManifest.DeserializeFiles(artifact.FilesJson);
        var directory = BaseArtifactManifest.ResolveDirectory(_dataDirectory, artifact.Id);
        var configFile = files.FirstOrDefault(static file => string.Equals(file.FileName, "config.json", StringComparison.OrdinalIgnoreCase));
        var config = TrainingFootprintEstimator.TryReadConfig(Path.Combine(directory, configFile?.FileName ?? "config.json"))
                     ?? throw new TrainingRunRejectedException("The base checkpoint's config.json could not be read, so its size cannot be estimated.");

        var parameterCount = TrainingFootprintEstimator.EstimateParameterCount(files, config);
        if (parameterCount <= 0)
        {
            throw new TrainingRunRejectedException("The base checkpoint declares no safetensors weights, so its size cannot be estimated.");
        }

        return new CheckpointFacts(config, parameterCount);
    }

    // What a base checkpoint contributes to a footprint estimate: its architecture config and the parameter count
    // derived from its safetensors index.
    private sealed record CheckpointFacts(BaseCheckpointConfigV1 Config, long ParameterCount);
}
