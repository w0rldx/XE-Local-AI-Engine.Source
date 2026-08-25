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

    /// <summary>
    ///     An explicit per-request thinking budget in tokens, overriding the one the reasoning EFFORT would imply.
    ///     Null keeps the effort-derived ceiling (or none), which is what every path but the benchmark freeze wants:
    ///     the fixed effort ladder is a ceiling chosen without knowing the window, whereas a benchmark pins a number
    ///     and has to replay it exactly. Only honoured on a thinking-capable model whose runtime can enforce a budget.
    /// </summary>
    public int? ReasoningBudgetTokens { get; init; }

    public float? RepeatPenalty { get; init; }

    public int? RepeatLastN { get; init; }

    public float? PresencePenalty { get; init; }

    public float? FrequencyPenalty { get; init; }

    // Carried as a string, not a number: a seed is an unconstrained 64-bit value, so a JSON number would lose precision
    // above 2^53 on the wire (see SeedValue). Null means "no override"; a non-null value is validated/parsed to long via
    // SeedValue at the ingress boundary (chat send + node-settings save) and again when mapped onto the invocation options.
    public string? Seed { get; init; }

    public IReadOnlyList<string>? Stop { get; init; }

    public int? NumCtx { get; init; }
}
