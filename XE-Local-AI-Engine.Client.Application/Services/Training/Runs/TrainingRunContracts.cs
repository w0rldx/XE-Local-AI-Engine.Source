namespace XE_Local_AI_Engine.Client.Services.Training.Runs;

using System.Text.Json.Serialization;

/// <summary>
///     The resolved hyper-parameters a run trains under, persisted (encrypted) in <c>training_runs.options_json</c>.
///     Frozen at creation: the wizard's computed defaults and any operator override are both resolved before the run is
///     enqueued, so the trainer reads one settled document rather than re-deriving anything from live hardware.
/// </summary>
public sealed record TrainingRunOptionsV1
{
    public int SchemaVersion { get; init; } = 1;

    public int MaxSeqLength { get; init; } = 2048;

    public int LoraR { get; init; } = 16;

    public int LoraAlpha { get; init; } = 16;

    public double LoraDropout { get; init; }

    public int PerDeviceTrainBatchSize { get; init; } = 2;

    public int GradientAccumulationSteps { get; init; } = 4;

    public double LearningRate { get; init; } = 2e-4;

    public double WarmupRatio { get; init; } = 0.03;

    public int Epochs { get; init; } = 1;

    public int Seed { get; init; } = 3407;

    /// <summary>bitsandbytes 8-bit Adam. Halves optimizer-state VRAM against fp32 adamw and is the QLoRA default.</summary>
    public string Optimizer { get; init; } = "adamw_8bit";
}

/// <summary>
///     What the run trained on, recorded at creation. The membership lists and the frozen copy's digest are what make
///     "the dataset was edited after the run started" answerable rather than a guess.
/// </summary>
public sealed record TrainingRunFreezeV1
{
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Names the frozen copy on disk. See <c>TrainingRunWorkspace.FrozenDatasetPath</c> for why it is not the run id.</summary>
    public Guid FreezeId { get; init; }

    public string DatasetContentFingerprint { get; init; } = string.Empty;

    public int DatasetRevision { get; init; }

    /// <summary>SHA-256 of the canonical JSONL PLAINTEXT, so the digest is stable across re-encryption.</summary>
    public string FrozenCopySha256 { get; init; } = string.Empty;

    public double HoldoutFraction { get; init; }

    public IReadOnlyList<Guid> TrainSampleIds { get; init; } = [];

    public IReadOnlyList<Guid> HoldoutSampleIds { get; init; } = [];

    /// <summary>
    ///     The holdout members' canonical sequence numbers. The frozen JSONL carries sequences, not sample ids, so this
    ///     is the form the trainer can act on — without it the split would be recorded but never applied.
    /// </summary>
    public IReadOnlyList<int> HoldoutSequences { get; init; } = [];
}

/// <summary>
///     The operator's recorded acknowledgement of the base checkpoint's licensing. A <see cref="License" /> of
///     <see langword="null" /> with <see cref="MetadataPresent" /> false is itself a recorded fact — "the repository
///     declares no license" still requires an explicit confirmation, it just confirms a different thing.
/// </summary>
public sealed record TrainingLicenseConfirmationV1
{
    public int SchemaVersion { get; init; } = 1;

    public string RepoId { get; init; } = string.Empty;

    public string? License { get; init; }

    public bool IsGated { get; init; }

    public bool MetadataPresent { get; init; }

    public long ConfirmedAtUtc { get; init; }

    /// <summary>SHA-256 of the exact text the operator was shown, so a later reword cannot be mistaken for the same consent.</summary>
    public string ConfirmationTextSha256 { get; init; } = string.Empty;
}

/// <summary>Latest progress snapshot, replaced (not appended) on every tick.</summary>
public sealed record TrainingRunProgressV1
{
    public int SchemaVersion { get; init; } = 1;

    /// <summary>The trainer's own phase: <c>loading</c>, <c>tokenizing</c>, <c>training</c> or <c>saving</c>.</summary>
    public string Phase { get; init; } = string.Empty;

    public int Step { get; init; }

    public int TotalSteps { get; init; }

    public double? Epoch { get; init; }

    public double? Loss { get; init; }

    public double? LearningRate { get; init; }

    public long? VramBytes { get; init; }

    public long UpdatedAtUtc { get; init; }
}

/// <summary>The persisted form of the spawn receipt. Every field is a reaper gate — see the startup reaper.</summary>
public sealed record TrainingLaunchReceiptV1
{
    public int SchemaVersion { get; init; } = 1;

    public int Pid { get; init; }

    public int Pgid { get; init; }

    public string? ExecutablePath { get; init; }

    public long StartTicks { get; init; }

    public string RunToken { get; init; } = string.Empty;
}

/// <summary>
///     The minimum of a Hugging Face <c>config.json</c> the defaults calculator needs. Every field is nullable: a
///     repository is free to omit any of them, and a missing field must degrade the estimate rather than fail the parse.
/// </summary>
public sealed record BaseCheckpointConfigV1
{
    [JsonPropertyName("architectures")]
    public IReadOnlyList<string>? Architectures { get; init; }

    [JsonPropertyName("hidden_size")]
    public int? HiddenSize { get; init; }

    [JsonPropertyName("intermediate_size")]
    public int? IntermediateSize { get; init; }

    [JsonPropertyName("num_hidden_layers")]
    public int? NumHiddenLayers { get; init; }

    [JsonPropertyName("num_attention_heads")]
    public int? NumAttentionHeads { get; init; }

    [JsonPropertyName("vocab_size")]
    public int? VocabSize { get; init; }

    [JsonPropertyName("max_position_embeddings")]
    public int? MaxPositionEmbeddings { get; init; }

    [JsonPropertyName("torch_dtype")]
    public string? TorchDtype { get; init; }
}

/// <summary>
///     What one run is expected to cost, and whether the box can pay it. Fails toward rejection by construction: the
///     activation term is inherently fuzzy, so the estimate carries headroom and a floor rather than pretending to
///     predict VRAM to the megabyte.
/// </summary>
public sealed record TrainingFootprintEstimate(
    long GpuBytes,
    long RamBytes,
    long ParameterCount,
    long TrainableParameterCount,
    bool Experimental);

/// <summary>The wizard's computed starting point plus the estimate behind it.</summary>
public sealed record TrainingRunDefaults(
    TrainingRunOptionsV1 Options,
    TrainingFootprintEstimate Estimate,
    long AvailableVramBytes,
    bool VramKnown,
    bool Fits,
    string? RejectionReason);

/// <summary>The base checkpoint's licensing as the run wizard presents it.</summary>
public sealed record TrainingLicenseGateView(
    Guid BaseArtifactId,
    string RepoId,
    string? License,
    bool IsGated,
    bool MetadataPresent,
    string ConfirmationText);

/// <summary>A refusal the run surface reports as a 4xx rather than as a fault. Message is operator-facing.</summary>
public sealed class TrainingRunRejectedException : Exception
{
    public TrainingRunRejectedException()
    {
    }

    public TrainingRunRejectedException(string message)
        : base(message)
    {
    }

    public TrainingRunRejectedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
