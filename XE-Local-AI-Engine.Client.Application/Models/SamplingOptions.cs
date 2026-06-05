namespace XE_Local_AI_Engine.Client.Models;

/// <summary>
///     Developer-gated per-send sampling overrides for the local chat loopback path. Every field is optional; a null
///     field means "no override" and the invocation factory leaves the corresponding Ollama option at its model default.
///     Carried on the <see cref="RuntimePackage" /> but deliberately excluded from the config hash (mirrors
///     <see cref="RuntimePackage.SupportsThinking" />): sampling is a loopback-only per-send knob, so excluding it keeps
///     the cross-repo encrypted/server digest byte-identical to the pre-sampling payload when no overrides are set.
/// </summary>
public sealed record SamplingOptions
{
    public float? Temperature { get; init; }

    public float? TopP { get; init; }

    public int? TopK { get; init; }

    public float? MinP { get; init; }

    public int? MaxOutputTokens { get; init; }

    public float? RepeatPenalty { get; init; }

    public int? RepeatLastN { get; init; }

    public float? PresencePenalty { get; init; }

    public float? FrequencyPenalty { get; init; }

    public long? Seed { get; init; }

    public IReadOnlyList<string>? Stop { get; init; }

    public int? NumCtx { get; init; }
}
