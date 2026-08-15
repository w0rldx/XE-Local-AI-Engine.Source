namespace XE_Local_AI_Engine.Client.Services.Training.Export;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

/// <summary>What the operator asked to export out of a finished run.</summary>
/// <param name="Kind">
///     <see cref="TrainingArtifactKind.MergedGguf" /> merges the adapter into the base and quantizes the result;
///     <see cref="TrainingArtifactKind.AdapterGguf" /> converts the adapter alone, to be served with
///     <c>--lora</c> on top of the installed base model.
/// </param>
/// <param name="QuantType">Target quantization for a merged export. Ignored for an adapter, which is always f16.</param>
public sealed record TrainingExportRequest(TrainingArtifactKind Kind, string? QuantType = null);

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

public sealed record TrainingExportStart(TrainingExportStartOutcome Outcome, string? Reason = null);

/// <summary>
///     The quantizations a training export may produce.
/// </summary>
/// <remarks>
///     Deliberately a short allow-list rather than the whole quant ladder: the value is passed straight to
///     <c>llama-quantize</c> as a type argument, and the set below is what that tool accepts AND what the advisor
///     would ever recommend serving a fine-tune at. An unknown token would otherwise reach the subprocess and fail
///     late, after the merge has already cost minutes and gigabytes.
/// </remarks>
public static class TrainingExportQuantizations
{
    public const string Default = "Q4_K_M";

    /// <summary>The f16 intermediate every merged export passes through, and the only shape an adapter is emitted in.</summary>
    public const string Float16 = "F16";

    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        Float16,
        "Q8_0",
        "Q6_K",
        "Q5_K_M",
        "Q5_K_S",
        "Q4_K_M",
        "Q4_K_S",
        "Q3_K_M"
    };

    public static IReadOnlyCollection<string> All =>
        Allowed;

    /// <summary>Normalizes and validates a requested quantization, or returns null when it is not supported.</summary>
    public static string? TryNormalize(string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested))
        {
            return Default;
        }

        var normalized = requested.Trim().ToUpperInvariant();
        return Allowed.Contains(normalized) ? normalized : null;
    }
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
/// <param name="State">Passed, Failed, or Skipped — never Pending; the gate always decides.</param>
/// <param name="Reason">Operator-facing diagnosis. Required for anything but a pass.</param>
public sealed record TrainedModelSmokeResult(TrainingArtifactSmokeState State, string? Reason);

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
/// <param name="ArtifactPath">The staged GGUF.</param>
/// <param name="BaseModelFilePath">
///     For an adapter: the installed base model's own GGUF, launched as <c>-m</c> with the artifact applied as
///     <c>--lora</c>. Null for a merged model, which is loaded directly.
/// </param>
public sealed record TrainingArtifactRecordView(string ArtifactPath, string? BaseModelFilePath);

/// <summary>Promotes a smoke-passed staged artifact into the local model registry.</summary>
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

    /// <summary>Re-runs the smoke gate against an already-staged artifact and records the new verdict.</summary>
    /// <exception cref="TrainingExportRejectedException">The artifact cannot be smoke-tested.</exception>
    Task<TrainedModelSmokeResult> RunSmokeAsync(Guid artifactId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Deletes a staged artifact — the row AND the bytes it staged. The ONLY supported way to delete one: the
    ///     store owns rows and never touches the filesystem, so calling it directly leaks a multi-gigabyte GGUF or a
    ///     whole adapter directory that nothing will ever collect.
    /// </summary>
    /// <remarks>
    ///     The row goes first, so a store refusal — a stale <paramref name="expectedVersion" />, an unknown id, or an
    ///     artifact the registry now owns — has left the disk untouched. The bytes then go best-effort and ONLY from
    ///     inside the run's own staged directory; anything else is logged and left alone.
    /// </remarks>
    Task DeleteArtifactAsync(Guid artifactId, long expectedVersion, CancellationToken cancellationToken = default);
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

/// <summary>Where the pipeline's intermediate and final files live inside the run's staged directory.</summary>
internal static class TrainingExportPaths
{
    /// <summary>
    ///     Staged file names carry the canonical quant token because that is what the GGUF inspector reads a
    ///     quantization off when the header does not declare one — an adapter's header never does.
    /// </summary>
    public static string MergedGgufName(string quantization) =>
        $"merged-{quantization}.gguf";

    public static string AdapterGgufName() =>
        $"adapter-{TrainingExportQuantizations.Float16}.gguf";

    /// <summary>Reads back the quantization the export named a staged file with.</summary>
    public static string? QuantizationOf(string stagedPath) =>
        GgufQuantParser.TryParse(Path.GetFileName(stagedPath));
}
