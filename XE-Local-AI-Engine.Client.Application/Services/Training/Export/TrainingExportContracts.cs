namespace XE_Local_AI_Engine.Client.Services.Training.Export;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>What the operator asked to export out of a finished run.</summary>
public sealed class TrainingExportRequest
{
    /// <summary>
    ///     <see cref="TrainingArtifactKind.MergedGguf" /> merges the adapter into the base and quantizes the result;
    ///     <see cref="TrainingArtifactKind.AdapterGguf" /> converts the adapter alone, to be served with
    ///     <c>--lora</c> on top of the installed base model.
    /// </summary>
    public required TrainingArtifactKind Kind { get; init; }

    /// <summary>Target quantization for a merged export. Ignored for an adapter, which is always f16.</summary>
    public string? QuantType { get; init; }
}

/// <summary>Why a start was refused, or that it was accepted. A refusal is a 4xx, never a fault.</summary>
public enum TrainingExportStartOutcome
{
    Accepted,

    /// <summary>No such run, or the run never reached a state with an adapter to export.</summary>
    RunNotExportable,

    /// <summary>The GPU is held by a training run, another export, or a warm inference process.</summary>
    Busy,

    /// <summary>The Python training runtime is not installed, so nothing can be merged or converted.</summary>
    RuntimeUnavailable,

    /// <summary>The requested quantization is not one this export supports.</summary>
    UnsupportedQuantization
}

public sealed class TrainingExportStart
{
    public required TrainingExportStartOutcome Outcome { get; init; }

    public string? Reason { get; init; }
}

/// <summary>The <c>export-job.json</c> handed to <c>export.py</c>. Merge mode only — see the script's own docstring.</summary>
public sealed record TrainingExportJobConfigV1
{
    public int ContractVersion { get; init; }

    public string Mode { get; init; } = "merge";

    public string BasePath { get; init; } = string.Empty;

    /// <summary>The trainer's staged adapter directory. peft resolves the base checkpoint from its own config here.</summary>
    public string AdapterDir { get; init; } = string.Empty;

    public string OutputDir { get; init; } = string.Empty;
}

/// <summary>
///     The verdict of one transient load-and-serve check against a staged artifact.
/// </summary>
public sealed class TrainedModelSmokeResult
{
    /// <summary>Passed, Failed, or Skipped — never Pending; the gate always decides.</summary>
    public required TrainingArtifactSmokeState State { get; init; }

    /// <summary>Operator-facing diagnosis. Required for anything but a pass.</summary>
    public required string? Reason { get; init; }
}

/// <summary>
///     Loads a staged, unpromoted GGUF in a throwaway <c>llama-server</c> and proves it can actually serve before
///     anything is allowed into the registry.
/// </summary>
public interface ITrainedModelSmokeGate
{
    /// <summary>
    ///     Runs the gate against <paramref name="artifact" />. Never throws for a model-side failure — a model that
    ///     cannot load IS the answer, and it is recorded rather than raised.
    /// </summary>
    Task<TrainedModelSmokeResult> RunAsync(TrainingArtifactRecordView artifact, CancellationToken cancellationToken);
}

/// <summary>
///     What the smoke gate needs about an artifact, with the base model already resolved. Keeps the gate free of the
///     run/registry lookups that decide which file to launch.
/// </summary>
public sealed class TrainingArtifactRecordView
{
    /// <summary>The staged GGUF.</summary>
    public required string ArtifactPath { get; init; }

    /// <summary>
    ///     For an adapter: the installed base model's own GGUF, launched as <c>-m</c> with the artifact applied as
    ///     <c>--lora</c>. Null for a merged model, which is loaded directly.
    /// </summary>
    public required string? BaseModelFilePath { get; init; }
}

/// <summary>Promotes a smoke-passed staged artifact with a current successful quality decision into the local model registry.</summary>
public interface IArtifactPromotionService
{
    /// <exception cref="TrainingExportRejectedException">The artifact cannot be promoted; the message is operator-facing.</exception>
    Task<string> PromoteAsync(Guid artifactId, string modelName, CancellationToken cancellationToken = default);
}

/// <summary>Starts and drives the export pipeline for one finished run.</summary>
public interface ITrainingExportService
{
    /// <summary>
    ///     Acquires the GPU exclusivity an export needs and starts the pipeline in the background. Returns as soon as
    ///     the work is owned, so a caller sees a refusal synchronously and progress over the run hub.
    /// </summary>
    Task<TrainingExportStart> StartExportAsync(Guid runId, TrainingExportRequest request, CancellationToken cancellationToken = default);

    /// <summary>The run's staged artifacts, as the export surface publishes them. A read, with no policy of its own.</summary>
    Task<IReadOnlyList<TrainingArtifactRecord>> ListArtifactsAsync(Guid runId, CancellationToken cancellationToken = default);

    /// <summary>One staged artifact, as the export surface publishes it, or null when there is no such artifact.</summary>
    Task<TrainingArtifactRecord?> GetArtifactAsync(Guid artifactId, CancellationToken cancellationToken = default);

    /// <summary>Re-runs the smoke gate against an already-staged artifact and records the new verdict.</summary>
    /// <exception cref="TrainingExportRejectedException">The artifact cannot be smoke-tested.</exception>
    Task<TrainedModelSmokeResult> RunSmokeAsync(Guid artifactId, CancellationToken cancellationToken = default);

    /// <summary>Deletes a staged artifact — the row AND the bytes it staged.</summary>
    /// <remarks>
    ///     The ONLY supported way to delete one: the store owns rows and never touches the filesystem, so calling it directly
    ///     leaks a multi-gigabyte GGUF or a whole adapter directory that nothing will ever collect. The row goes first, so a
    ///     store refusal — a stale <paramref name="expectedVersion" />, an unknown id, or an artifact the registry now owns —
    ///     has left the disk untouched. The bytes then go best-effort and ONLY from inside the run's own staged directory;
    ///     anything else is logged and left alone.
    /// </remarks>
    Task DeleteArtifactAsync(Guid artifactId, long expectedVersion, CancellationToken cancellationToken = default);

    Task<TrainingArtifactRecord> DiscardArtifactQualityAsync(Guid artifactId,
        long expectedVersion,
        string reason,
        CancellationToken cancellationToken = default);
}

/// <summary>A refusal the export surface reports as a 4xx rather than as a fault. Message is operator-facing.</summary>
public sealed class TrainingExportRejectedException : Exception
{
    public TrainingExportRejectedException()
    {
    }

    public TrainingExportRejectedException(string message)
        : base(message)
    {
    }

    public TrainingExportRejectedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public enum ArtifactQualityOutcome
{
    Pending,
    Passed,
    Failed,
    Overridden
}

public sealed record ArtifactQualityDecisionAuditV1
{
    public Guid ArtifactId { get; init; }
    public string ArtifactSha256 { get; init; } = string.Empty;
    public Guid ComparisonId { get; init; }
    public Guid BaseEvaluationId { get; init; }
    public Guid TunedEvaluationId { get; init; }
    public int PolicyVersion { get; init; }
    public double MinimumAggregateDelta { get; init; }
    public double MinimumPerKindDelta { get; init; }
    public ArtifactQualityOutcome Outcome { get; init; }
    public IReadOnlyList<string> FailureCodes { get; init; } = [];
    public long DecidedAtUtc { get; init; }
    public string? OverrideReason { get; init; }
    public long? OverriddenAtUtc { get; init; }
}

public sealed record ArtifactQualityDecisionV1
{
    public const int CurrentPolicyVersion = 1;
    public int SchemaVersion { get; init; } = 1;
    public int PolicyVersion { get; init; } = CurrentPolicyVersion;
    public Guid ArtifactId { get; init; }
    public string ArtifactSha256 { get; init; } = string.Empty;
    public Guid ComparisonId { get; init; }
    public Guid BaseEvaluationId { get; init; }
    public Guid TunedEvaluationId { get; init; }
    public ArtifactQualityOutcome Outcome { get; init; }
    public IReadOnlyList<string> FailureCodes { get; init; } = [];
    public double MinimumAggregateDelta { get; init; }
    public double MinimumPerKindDelta { get; init; }
    public long DecidedAtUtc { get; init; }
    public string? OverrideReason { get; init; }
    public long? OverriddenAtUtc { get; init; }
    public IReadOnlyList<ArtifactQualityDecisionAuditV1> History { get; init; } = [];
}
