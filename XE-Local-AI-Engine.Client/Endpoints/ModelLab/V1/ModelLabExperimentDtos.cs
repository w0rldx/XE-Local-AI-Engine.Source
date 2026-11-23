namespace XE_Local_AI_Engine.Client.Endpoints.ModelLab.V1;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.ModelLab;

public sealed class ModelLabCheckpointDownloadRequest
{
    public required string RepoId { get; init; }

    public string? Revision { get; init; }

    public required string InstalledModelName { get; init; }

    /// <summary>The operator has confirmed the preflight's license text.</summary>
    public required bool LicenseConfirmed { get; init; }
}

/// <summary>A refused pick is a 200 with the preflight's refusal set, and no artifact.</summary>
public sealed class ModelLabCheckpointDownloadResponse
{
    public required ModelLabCheckpointPreflightResponse Preflight { get; init; }

    public required Guid? BaseArtifactId { get; init; }

    public required string? ArtifactStatus { get; init; }
}

public sealed class StartModelLabExperimentRequest
{
    public required Guid BaseArtifactId { get; init; }

    public required string InstalledModelName { get; init; }

    public required string Prompt { get; init; }

    public required bool LicenseConfirmed { get; init; }

    /// <summary>Makes this a steering job. Left out, it is a trace.</summary>
    public ModelLabSteeringRequest? Steering { get; init; }

    /// <summary>The distinct blocks a trace records, in any order. Left out, the three quarter points; not allowed with <see cref="Steering" />.</summary>
    public IReadOnlyList<int>? Layers { get; init; }

    /// <summary>A trace also records each step's attention row at up to eight full-attention blocks the node picks. Not allowed with <see cref="Steering" />.</summary>
    public bool RecordAttention { get; init; }

    /// <summary>The tokens a trace generates, 1 to 256. Left out, 128; not allowed with <see cref="Steering" />.</summary>
    public int? MaxNewTokens { get; init; }
}

public sealed class StartModelLabExperimentResponse
{
    public required Guid ExperimentId { get; init; }
}

/// <summary>A start refused before anything was written. <see cref="Reason" /> is a stable code; the message is what to do next.</summary>
public sealed class ModelLabStartBlockedResponse
{
    public required ModelLabStartRefusal Reason { get; init; }

    public required string Message { get; init; }
}

public sealed class ModelLabExperimentResponse
{
    public required Guid Id { get; init; }

    public required ModelLabExperimentStatus Status { get; init; }

    public required ModelLabExperimentKind Kind { get; init; }

    public required string RepoId { get; init; }

    public required string Revision { get; init; }

    public required string InstalledModelName { get; init; }

    public required string Prompt { get; init; }

    public required string? ErrorMessage { get; init; }

    /// <summary>A trace's size; null for a steering job, which has no trace to read.</summary>
    public required long? TraceByteCount { get; init; }

    /// <summary>Set on the single-experiment read: a finished trace, or as much as a steering job has done. A list leaves it out.</summary>
    public required ModelLabTraceResult? Result { get; init; }

    /// <summary>The operator's note, on the single-experiment read only.</summary>
    public required string? Notes { get; init; }

    public required bool IsPinned { get; init; }

    /// <summary>The node's version when the run started; null on a run saved before it was recorded.</summary>
    public required string? AppVersion { get; init; }

    public required DateTimeOffset CreatedAtUtc { get; init; }

    public required DateTimeOffset UpdatedAtUtc { get; init; }
}

public sealed class ModelLabExperimentListResponse
{
    /// <summary>False on a platform with no trace job. The page shows <see cref="UnsupportedMessage" /> where a trace would start.</summary>
    public required bool PlatformSupported { get; init; }

    public required string? UnsupportedMessage { get; init; }

    public required Guid? ActiveExperimentId { get; init; }

    /// <summary>How many unpinned runs retention keeps; pinned runs are kept on top of these.</summary>
    public required int MaxRetainedExperiments { get; init; }

    public required int MaxPinnedExperiments { get; init; }

    public required IReadOnlyList<ModelLabExperimentResponse> Experiments { get; init; }
}

public sealed class ModelLabExperimentRouteRequest
{
    public Guid ExperimentId { get; init; }
}

public sealed class SetModelLabExperimentNotesRequest
{
    public Guid ExperimentId { get; init; }

    /// <summary>Null, empty or blank clears the note.</summary>
    public string? Notes { get; init; }
}

public sealed class SetModelLabExperimentPinRequest
{
    public Guid ExperimentId { get; init; }

    public required bool Pinned { get; init; }
}

/// <summary>One token at one layer, as measured.</summary>
public sealed class ModelLabTraceRecordResponse
{
    public required int Step { get; init; }

    public required int Layer { get; init; }

    /// <summary>F for a full-attention block, L for a linear one, ? when the model does not say.</summary>
    public required string Kind { get; init; }

    /// <summary>Null, here and in the lists below, where the measured value was not a finite number.</summary>
    public required double? Norm { get; init; }

    /// <summary>The 1st, 50th and 99th percentiles of the residual's values.</summary>
    public required IReadOnlyList<double?> Percentiles { get; init; }

    public required IReadOnlyList<int> TopIndices { get; init; }

    public required IReadOnlyList<double?> TopValues { get; init; }
}

public sealed class ModelLabExperimentAttentionRouteRequest
{
    public Guid ExperimentId { get; init; }

    public int Layer { get; init; }
}

/// <summary>One head of one attention row: the kept keys, most weight first, and the weight of every key left out.</summary>
public sealed class ModelLabAttentionHeadResponse
{
    /// <summary>Key positions in the prompt-plus-generated sequence.</summary>
    public required IReadOnlyList<int> Keys { get; init; }

    /// <summary>Fractions of the row, matching <see cref="Keys" />, rounded down to four decimals.</summary>
    public required IReadOnlyList<double> Weights { get; init; }

    public required double Rest { get; init; }
}

/// <summary>The query row of the position that predicted generated token <see cref="Step" />.</summary>
public sealed class ModelLabAttentionRowResponse
{
    public required int Step { get; init; }

    public required int QueryPosition { get; init; }

    /// <summary>Indexed by query head.</summary>
    public required IReadOnlyList<ModelLabAttentionHeadResponse> Heads { get; init; }
}

public sealed class ModelLabAttentionResponse
{
    public required int Layer { get; init; }

    public required int Heads { get; init; }

    public required int KvHeads { get; init; }

    public required int TopKeys { get; init; }

    public required IReadOnlyList<ModelLabAttentionRowResponse> Rows { get; init; }

    /// <summary>True when the run's byte cap dropped rows, at this block or another.</summary>
    public required bool Truncated { get; init; }

    public required int DroppedRecords { get; init; }
}

/// <summary>One block's lens readout at one step: the model's final norm and output layer applied to that block's output.</summary>
public sealed class ModelLabLensBlockResponse
{
    public required int Layer { get; init; }

    /// <summary>The readout's top token ids, most probable first; their texts are in <see cref="ModelLabLensResponse.Texts" />.</summary>
    public required IReadOnlyList<int> Ids { get; init; }

    /// <summary>Natural-log probabilities matching <see cref="Ids" />, rounded down to four decimals; null, here and below, where not finite.</summary>
    public required IReadOnlyList<double?> Logprobs { get; init; }

    /// <summary>The generated token's logprob in this readout.</summary>
    public required double? ChosenLogprob { get; init; }

    /// <summary>1 for the most probable; tokens tied with the generated one share the better rank. Null where <see cref="ChosenLogprob" /> is.</summary>
    public required int? ChosenRank { get; init; }

    /// <summary>The readout's entropy in nats, to three decimals: 0 when one token holds all the mass, at most ln <see cref="ModelLabLensResponse.VocabSize" />.</summary>
    public required double? Entropy { get; init; }
}

public sealed class ModelLabLensStepResponse
{
    public required int Step { get; init; }

    /// <summary>One per block, in <see cref="ModelLabLensResponse.Layers" /> order.</summary>
    public required IReadOnlyList<ModelLabLensBlockResponse> Blocks { get; init; }
}

public sealed class ModelLabLensTextResponse
{
    public required int Id { get; init; }

    /// <summary>What the tokenizer decodes the id to on its own, cut to 64 escaped bytes.</summary>
    public required string Text { get; init; }
}

/// <summary>A derived analysis of a saved trace, not what the model computed: only the last block's output reached the output layer.</summary>
public sealed class ModelLabLensResponse
{
    public required IReadOnlyList<int> Layers { get; init; }

    public required int TopTokens { get; init; }

    public required bool TiedEmbeddings { get; init; }

    public required int VocabSize { get; init; }

    /// <summary>From step 0, in order; a truncated lens keeps a prefix.</summary>
    public required IReadOnlyList<ModelLabLensStepResponse> Steps { get; init; }

    /// <summary>Each distinct top id once.</summary>
    public required IReadOnlyList<ModelLabLensTextResponse> Texts { get; init; }

    /// <summary>True when the lens cap dropped steps; the trace's own records are not affected.</summary>
    public required bool Truncated { get; init; }

    public required int DroppedSteps { get; init; }
}

/// <summary>One MoE block's router choice at one step.</summary>
public sealed class ModelLabRoutingBlockResponse
{
    public required int Layer { get; init; }

    /// <summary>The picked expert ids, in the router's order.</summary>
    public required IReadOnlyList<int> Experts { get; init; }

    /// <summary>The weights the picked experts were given, matching <see cref="Experts" />: the bfloat16 values the model used, exactly.</summary>
    public required IReadOnlyList<double> Weights { get; init; }
}

public sealed class ModelLabRoutingStepResponse
{
    public required int Step { get; init; }

    /// <summary>One per MoE block, in <see cref="ModelLabRoutingResponse.Layers" /> order.</summary>
    public required IReadOnlyList<ModelLabRoutingBlockResponse> Blocks { get; init; }
}

/// <summary>Measured: the router choices behind each generated token, read at the position whose logits chose it.</summary>
public sealed class ModelLabRoutingResponse
{
    /// <summary>The MoE blocks.</summary>
    public required IReadOnlyList<int> Layers { get; init; }

    /// <summary>The experts each router chooses from.</summary>
    public required int Experts { get; init; }

    public required int ExpertsPerToken { get; init; }

    /// <summary>True when the picks' weights sum to 1; false when they keep their share of the router's probability.</summary>
    public required bool NormalizedTopK { get; init; }

    /// <summary>From step 0, in order; a truncated recording keeps a prefix.</summary>
    public required IReadOnlyList<ModelLabRoutingStepResponse> Steps { get; init; }

    /// <summary>True when the routing cap dropped steps; the trace's other sections are not affected.</summary>
    public required bool Truncated { get; init; }

    public required int DroppedSteps { get; init; }
}

public sealed class ModelLabTraceResponse
{
    public required IReadOnlyList<ModelLabTraceRecordResponse> Records { get; init; }

    public required bool Truncated { get; init; }

    public required int DroppedRecords { get; init; }

    public required long ByteCap { get; init; }
}
