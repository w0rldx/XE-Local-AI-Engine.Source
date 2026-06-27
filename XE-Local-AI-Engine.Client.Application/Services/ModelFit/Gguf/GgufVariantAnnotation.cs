namespace XE_Local_AI_Engine.Client.Services.ModelFit.Gguf;

using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

/// <summary>
///     A per-file recommendation annotation for one selectable GGUF variant: its quality tier, its hardware fit verdict,
///     and whether it is THE single recommended variant for the repo. Keyed back to its file by <see cref="FileName" />.
/// </summary>
public sealed record GgufVariantAnnotation(
    string FileName,
    GgufQuantTier QualityTier,
    GgufFitVerdict FitVerdict,
    bool IsRecommended);
