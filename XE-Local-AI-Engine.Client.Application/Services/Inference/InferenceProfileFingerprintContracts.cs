namespace XE_Local_AI_Engine.Client.Services.Inference;

/// <summary>Inputs needed before an explored profile has been persisted.</summary>
public sealed record InferenceProfileFingerprintInput
{
    public required string ModelName { get; init; }

    public required int Role { get; init; }

    public required string Backend { get; init; }

    public required string ModelFilePath { get; init; }

    public required int CtxSize { get; init; }

    public required int? NGpuLayers { get; init; }

    public required string? TensorSplit { get; init; }

    public required string? OverrideTensor { get; init; }

    public required string? KvTypeK { get; init; }

    public required string? KvTypeV { get; init; }

    public required bool FlashAttn { get; init; }
}

/// <summary>
///     Persisted launch-policy identity: schema version plus a strong capture hash and a cheap validation hash. The
///     validation half lets cold-spawn staleness checks avoid streaming multi-gigabyte model/runtime files.
/// </summary>
public sealed record LaunchPolicyFingerprint
{
    public required int Version { get; init; }

    public required string Value { get; init; }
}
