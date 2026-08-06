namespace XE_Local_AI_Engine.Client.Services.Inference;

/// <summary>Inputs needed before an explored profile has been persisted.</summary>
public sealed record InferenceProfileFingerprintInput(
    string ModelName,
    int Role,
    string Backend,
    string ModelFilePath,
    int CtxSize,
    int? NGpuLayers,
    string? TensorSplit,
    string? OverrideTensor,
    string? KvTypeK,
    string? KvTypeV,
    bool FlashAttn);

/// <summary>
///     Persisted launch-policy identity: schema version plus a strong capture hash and a cheap validation hash. The
///     validation half lets cold-spawn staleness checks avoid streaming multi-gigabyte model/runtime files.
/// </summary>
public sealed record LaunchPolicyFingerprint(int Version, string Value);
