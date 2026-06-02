namespace XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     The model-fit operations an approved utility image is sanctioned for. A single descriptor may serve more than
///     one purpose (e.g. the llmfit image both recommends and benchmarks), so this is a <c>[Flags]</c> enum. The
///     numeric values are persisted as the bitwise OR, so existing values must never be renumbered — future purposes
///     append new powers of two only.
/// </summary>
[Flags]
public enum UtilityImagePurpose
{
    None = 0,
    ModelRecommendation = 1,
    ModelBenchmark = 2
}
