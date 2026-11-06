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
    InvalidTraceLayers
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

/// <summary>One generated token. The text is what the tokenizer decodes the id to on its own.</summary>
public sealed record ModelLabResultToken
{
    public int Id { get; init; }

    public string Text { get; init; } = string.Empty;
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

    /// <summary>Set on a steering job. The fields above then describe its derive pass, and the tokens stay empty.</summary>
    public ModelLabSteeringResult? Steering { get; init; }
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
