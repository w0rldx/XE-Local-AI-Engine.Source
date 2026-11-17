namespace XE_Local_AI_Engine.Client.Services.ModelLab;

using System.Text.Json.Serialization;

/// <summary>What another service may ask about the running job. It is a live answer, not a row: a failed completion can leave a row that says Running with no job.</summary>
public interface IModelLabActiveJob
{
    Guid? ActiveBaseArtifactId { get; }
}

/// <summary>Bounds on one Model Lab job, each a separate failure.</summary>
public sealed class ModelLabJobOptions
{
    /// <summary>How long the script may stay silent. It beats every 20 seconds until the first token and while saving, and reports each token between.</summary>
    public TimeSpan InactivityTimeout { get; init; } = TimeSpan.FromMinutes(5);

    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>How long the script gets to exit after a stop request or after closing its output, before its group is killed.</summary>
    public TimeSpan ExitGracePeriod { get; init; } = TimeSpan.FromSeconds(15);
}

/// <summary>What <c>modellab.py</c> reads from <c>job.json</c> for a trace. Holds the plaintext prompt, in the owner-only work directory.</summary>
public sealed record ModelLabTraceJobV1
{
    public int ContractVersion { get; init; } = ModelLabTraceSpec.ContractVersion;

    public string Kind { get; init; } = "trace";

    public required string CheckpointPath { get; init; }

    public required string Prompt { get; init; }

    public required string TracePath { get; init; }

    public required string ResultPath { get; init; }

    public required IReadOnlyList<int> Layers { get; init; }

    public int MaxNewTokens { get; init; } = ModelLabTraceSpec.MaxNewTokens;

    /// <summary><see cref="ModelLabTraceSpec.TraceByteCapFor" /> of the layer count.</summary>
    public required int TraceByteCap { get; init; }

    public int Seed { get; init; } = ModelLabTraceSpec.Seed;

    /// <summary>Where the script writes the attention rows. This and the three below are set together, and left out of the file when the trace records no attention.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AttentionPath { get; init; }

    /// <summary><see cref="ModelLabTraceSpec.AttentionLayersFor" />: full-attention blocks only, ascending.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<int>? AttentionLayers { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? AttentionTopKeys { get; init; }

    /// <summary><see cref="ModelLabTraceSpec.AttentionByteCapFor" /> of the layers and the checkpoint's query heads.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? AttentionByteCap { get; init; }
}

/// <summary>What <c>modellab.py</c> reads for a steering job. Holds the plaintext contrast prompts, in the owner-only work directory.</summary>
public sealed record ModelLabSteeringJobV1
{
    public int ContractVersion { get; init; } = ModelLabTraceSpec.ContractVersion;

    public string Kind { get; init; } = "steering";

    public required string CheckpointPath { get; init; }

    public required IReadOnlyList<string> PositivePrompts { get; init; }

    public required IReadOnlyList<string> NegativePrompts { get; init; }

    public required int Layer { get; init; }

    public required string VectorPath { get; init; }

    public required string RandomVectorPath { get; init; }

    public required string ResultPath { get; init; }

    public int Seed { get; init; } = ModelLabTraceSpec.Seed;
}

public sealed record ModelLabExperimentRequest
{
    public required Guid BaseArtifactId { get; init; }

    public required string InstalledModelName { get; init; }

    /// <summary>A trace's prompt, or a steering job's first test prompt.</summary>
    public required string Prompt { get; init; }

    public required bool LicenseConfirmed { get; init; }

    /// <summary>Makes this a steering job: derive a vector, then run baseline, vector and random control. Null for a trace.</summary>
    public ModelLabSteeringRequest? Steering { get; init; }

    /// <summary>The blocks a trace records, in any order. Null for <see cref="ModelLabTraceSpec.LayersFor" />; a steering job has its own layer.</summary>
    public IReadOnlyList<int>? Layers { get; init; }

    /// <summary>A trace also records each generated step's attention row at the blocks <see cref="ModelLabTraceSpec.AttentionLayersFor" /> picks.</summary>
    public bool RecordAttention { get; init; }
}

/// <summary>A steering job's inputs. The arm's prompts are the request's <c>prompt</c> and then <see cref="AdditionalTestPrompts" />.</summary>
public sealed record ModelLabSteeringRequest
{
    public required IReadOnlyList<string> PositivePrompts { get; init; }

    public required IReadOnlyList<string> NegativePrompts { get; init; }

    public IReadOnlyList<string> AdditionalTestPrompts { get; init; } = [];

    /// <summary>Null for a full-attention block at about 0.6 depth, read from the checkpoint's config.</summary>
    public int? Layer { get; init; }

    /// <summary>The multiplier on the derived vector, which is already half the residual's length.</summary>
    public double Scale { get; init; } = 1;
}

/// <summary>Why a trace did not start. The value is the API's stable refusal code.</summary>
public enum ModelLabStartRefusal
{
    UnsupportedPlatform,
    CheckpointNotFound,
    CheckpointNotReady,
    LicenseNotConfirmed,
    PreflightRefused,
    RuntimeUnavailable,
    Busy,
    ModelLoaded,
    InsufficientMemory,
    ProcessNotConfirmedStopped,
    InvalidLayer,
    InvalidTraceLayers,
    PinLimitReached,
    ModelNotLoaded,
    AttentionUnavailable
}

public sealed record ModelLabStart
{
    public Guid? ExperimentId { get; init; }

    public ModelLabStartRefusal? Refusal { get; init; }

    public string? RefusalMessage { get; init; }
}

public enum ModelLabTraceReadStatus
{
    Found,
    NotFound,
    Unavailable
}

/// <summary>One token at one layer, as measured. The short names are the file's, chosen to keep the capped trace small.</summary>
public sealed record ModelLabTraceRecord
{
    [JsonPropertyName("s")]
    public int Step { get; init; }

    [JsonPropertyName("l")]
    public int Layer { get; init; }

    [JsonPropertyName("k")]
    public string Kind { get; init; } = "?";

    /// <summary>Null, here and in the lists below, where the measured value was not a finite number.</summary>
    [JsonPropertyName("n")]
    public double? Norm { get; init; }

    /// <summary>The 1st, 50th and 99th percentiles of the residual's values.</summary>
    [JsonPropertyName("p")]
    public IReadOnlyList<double?> Percentiles { get; init; } = [];

    [JsonPropertyName("ti")]
    public IReadOnlyList<int> TopIndices { get; init; } = [];

    [JsonPropertyName("tv")]
    public IReadOnlyList<double?> TopValues { get; init; } = [];
}

public sealed record ModelLabTraceFooter
{
    [JsonPropertyName("truncated")]
    public bool Truncated { get; init; }

    [JsonPropertyName("records")]
    public int Records { get; init; }

    [JsonPropertyName("dropped")]
    public int Dropped { get; init; }

    [JsonPropertyName("cap")]
    public long Cap { get; init; }
}

public sealed record ModelLabTrace
{
    public required ModelLabTraceReadStatus Status { get; init; }

    public IReadOnlyList<ModelLabTraceRecord> Records { get; init; } = [];

    public ModelLabTraceFooter? Footer { get; init; }
}

/// <summary>One predicted token's query row at one block, per query head: the kept keys, most weight first, and the mass of the keys left out.</summary>
public sealed record ModelLabAttentionRecord
{
    /// <summary>The index of the token this row predicted.</summary>
    [JsonPropertyName("s")]
    public int Step { get; init; }

    [JsonPropertyName("l")]
    public int Layer { get; init; }

    [JsonPropertyName("q")]
    public int QueryPosition { get; init; }

    /// <summary>Key positions, per head.</summary>
    [JsonPropertyName("k")]
    public IReadOnlyList<IReadOnlyList<int>> Keys { get; init; } = [];

    /// <summary>Weights in units of <see cref="ModelLabAttentionSummary.WeightScale" />, rounded down, matching <see cref="Keys" />.</summary>
    [JsonPropertyName("w")]
    public IReadOnlyList<IReadOnlyList<int>> Weights { get; init; } = [];

    /// <summary>The weight outside the kept keys, per head, in the same units.</summary>
    [JsonPropertyName("r")]
    public IReadOnlyList<int> Rest { get; init; } = [];
}

/// <summary>A trace's attention rows at one block, with the run's capture shape.</summary>
public sealed record ModelLabAttention
{
    public required ModelLabTraceReadStatus Status { get; init; }

    public ModelLabAttentionSummary? Summary { get; init; }

    public IReadOnlyList<ModelLabAttentionRecord> Records { get; init; } = [];

    public ModelLabTraceFooter? Footer { get; init; }
}

/// <summary>One generated token. The text is what the tokenizer decodes the id to on its own.</summary>
public sealed record ModelLabResultToken
{
    public int Id { get; init; }

    public string Text { get; init; } = string.Empty;

    /// <summary>The token's natural-log probability from the model's raw logits over the whole vocabulary; null in a trace made before candidates were recorded, or where it was not finite.</summary>
    public double? Logprob { get; init; }

    /// <summary>The top candidates from the raw logits, most probable first; null in a trace made before they were recorded.</summary>
    public IReadOnlyList<ModelLabResultCandidate>? Candidates { get; init; }

    /// <summary>As <see cref="Logprob" />, after the checkpoint's generation-config processors (for example a repetition penalty).</summary>
    public double? ProcessedLogprob { get; init; }

    /// <summary>As <see cref="Candidates" />, after the checkpoint's generation-config processors. A candidate they masked is left out.</summary>
    public IReadOnlyList<ModelLabResultCandidate>? ProcessedCandidates { get; init; }
}

/// <summary>One next-token candidate at a generated step. The logprob is rounded down to four decimals.</summary>
public sealed record ModelLabResultCandidate
{
    public int Id { get; init; }

    public string Text { get; init; } = string.Empty;

    public double Logprob { get; init; }
}

/// <summary>What <c>modellab.py</c> measured, from <c>result.json</c>.</summary>
public sealed record ModelLabTraceResult
{
    public int PromptTokens { get; init; }

    public IReadOnlyList<ModelLabResultToken> Tokens { get; init; } = [];

    public string Text { get; init; } = string.Empty;

    public double Seconds { get; init; }

    public long PeakVramBytes { get; init; }

    public int BlockCount { get; init; }

    public string? ModelClass { get; init; }

    public string? Device { get; init; }

    public string? Torch { get; init; }

    public string? Transformers { get; init; }

    public string? Python { get; init; }

    /// <summary>The CUDA version torch was built with. Null, like the three below, on a result saved before it was recorded.</summary>
    public string? Cuda { get; init; }

    /// <summary>The traced blocks, ascending; a steering job leaves it null.</summary>
    public IReadOnlyList<int>? Layers { get; init; }

    public int? Seed { get; init; }

    public int? MaxNewTokens { get; init; }

    /// <summary>Set on a steering job. The fields above then describe its derive pass, and the tokens stay empty.</summary>
    public ModelLabSteeringResult? Steering { get; init; }

    /// <summary>The rendered prompt's tokens, in order. Null, like the three below, on a result saved before contract 4.</summary>
    public IReadOnlyList<ModelLabPromptPiece>? PromptPieces { get; init; }

    /// <summary>One character per block: F for full attention, L for linear, ? when the model does not say.</summary>
    public string? LayerKinds { get; init; }

    /// <summary>The attention kernel the run used, such as <c>sdpa</c>.</summary>
    public string? AttnImplementation { get; init; }

    /// <summary>Null unless the trace recorded attention.</summary>
    public ModelLabAttentionSummary? Attention { get; init; }
}

/// <summary>One token of the rendered prompt. The text is what the tokenizer decodes the id to on its own, and can be cut to keep the result under its cap.</summary>
public sealed record ModelLabPromptPiece
{
    public int Id { get; init; }

    public string Text { get; init; } = string.Empty;
}

/// <summary>What a trace's attention capture recorded. The rows themselves are in the run's attention blob.</summary>
public sealed record ModelLabAttentionSummary
{
    /// <summary>The captured blocks, ascending.</summary>
    public IReadOnlyList<int> Layers { get; init; } = [];

    /// <summary>Query heads per block; query head h reads key/value head h / (Heads / KvHeads).</summary>
    public int Heads { get; init; }

    public int KvHeads { get; init; }

    public int HeadDim { get; init; }

    /// <summary>Keys kept per row and head; a row over fewer keys keeps them all.</summary>
    public int TopKeys { get; init; }

    /// <summary>A stored weight of <c>n</c> means <c>n / WeightScale</c>.</summary>
    public int WeightScale { get; init; }

    public int Records { get; init; }

    /// <summary>True when the byte cap dropped rows.</summary>
    public bool Truncated { get; init; }
}

/// <summary>A derived vector and each arm that has finished. Arms compare only with each other: their launches are not a chat's.</summary>
public sealed record ModelLabSteeringResult
{
    public int Layer { get; init; }

    /// <summary>F for a full-attention block, L for a linear one, ? when the model does not say.</summary>
    public string LayerKind { get; init; } = "?";

    public double Scale { get; init; }

    public double? ReferenceNorm { get; init; }

    public double? DifferenceNorm { get; init; }

    /// <summary>The vector's own length. The random control has the same.</summary>
    public double? VectorNorm { get; init; }

    public int RandomSeed { get; init; }

    /// <summary>The seed every arm's server runs with. Null on a result saved before it was recorded.</summary>
    public int? Seed { get; init; }

    public double LoadSeconds { get; init; }

    public string? VectorSha256 { get; init; }

    public string? RandomVectorSha256 { get; init; }

    public long VectorBytes { get; init; }

    public IReadOnlyList<string> PositivePrompts { get; init; } = [];

    public IReadOnlyList<string> NegativePrompts { get; init; } = [];

    public IReadOnlyList<ModelLabSteeringArm> Arms { get; init; } = [];
}

/// <summary>One arm: one server process and every test prompt on it.</summary>
public sealed record ModelLabSteeringArm
{
    /// <summary><see cref="ModelLabArmNames" />.</summary>
    public required string Name { get; init; }

    public required IReadOnlyList<ModelLabArmOutput> Outputs { get; init; }

    /// <summary>From the previous phase's end to this arm's server answering: the teardown before it, its hash on the first arm, and its load.</summary>
    public double StartupSeconds { get; init; }

    public double GenerationSeconds { get; init; }

    public string? Runtime { get; init; }

    public string? Build { get; init; }

    public string? ExecutableSha256 { get; init; }

    public string? ModelSha256 { get; init; }

    /// <summary>Null on the baseline.</summary>
    public string? VectorSha256 { get; init; }

    public double? Scale { get; init; }
}

public sealed record ModelLabArmOutput
{
    public required string Prompt { get; init; }

    public required string Text { get; init; }

    public required IReadOnlyList<int> TokenIds { get; init; }

    public string? FinishReason { get; init; }
}

/// <summary>What the three arms run: one GGUF, two vector files and the same prompts.</summary>
public sealed record ModelLabArmPlan
{
    public required string ModelFilePath { get; init; }

    public required string VectorPath { get; init; }

    public required string RandomVectorPath { get; init; }

    public required double Scale { get; init; }

    public required IReadOnlyList<string> Prompts { get; init; }
}

public static class ModelLabArmNames
{
    public const string Baseline = "baseline";

    public const string Vector = "vector";

    public const string Random = "random";
}
