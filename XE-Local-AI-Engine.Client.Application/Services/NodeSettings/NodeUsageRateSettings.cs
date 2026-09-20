namespace XE_Local_AI_Engine.Client.Services.NodeSettings;

using System.Text.Json.Serialization;

/// <summary>
///     A per-model price point used to estimate the USD cost of token usage: two rates quoted in US dollars per one
///     million tokens.
/// </summary>
/// <remarks>
///     Reasoning tokens are billed at the <see cref="OutputPer1M" /> rate, being model output, so the estimate is
///     <c>InputPer1M/1e6 * promptTokens + OutputPer1M/1e6 * (completionTokens + reasoningTokens)</c>.
/// </remarks>
public sealed record ModelRate
{
    /// <summary>Price of one million input/prompt tokens, in US dollars.</summary>
    public double InputPer1M { get; init; }

    /// <summary>Price of one million output tokens (completion AND reasoning), in US dollars.</summary>
    public double OutputPer1M { get; init; }

    /// <summary>
    ///     Shared validity predicate, one authority for the boundary validator, the store's <c>Normalize</c> and the
    ///     resolver: both rates must be finite and non-negative, a negative or non-finite rate being a mistake.
    /// </summary>
    /// <remarks>
    ///     It is <see cref="System.Text.Json.Serialization.JsonIgnoreAttribute" />d, an internal check rather than part
    ///     of the wire contract, so it never appears in the OpenAPI schema or the persisted JSON.
    /// </remarks>
    [JsonIgnore]
    public bool HasValidRates => double.IsFinite(InputPer1M) && double.IsFinite(OutputPer1M) && InputPer1M >= 0 && OutputPer1M >= 0;
}

/// <summary>
///     The persisted operator override of usage cost rates, stored as JSON inside <see cref="StoredNodeSettings" /> and
///     used by the usage-summary cost estimate.
/// </summary>
/// <remarks>
///     <see cref="Models" /> is keyed by model NAME, matched case-insensitively against the run-envelope
///     <c>ModelName</c>, and a value overrides the built-in default rate table; absent, the default, the resolver falls
///     back to those defaults and treats a model with neither as unpriced. Local runtimes are always free regardless,
///     enforced in the resolver. The map is string-keyed so <c>node-settings.json</c> stays human-editable, negative or
///     non-finite entries are dropped by <c>NodeSettingsStore.Normalize</c> on read, and edits apply on the next read.
/// </remarks>
public sealed record NodeUsageRateSettings
{
    /// <summary>Per-model-name rate override, keyed by model name (case-insensitive).</summary>
    public IReadOnlyDictionary<string, ModelRate>? Models { get; init; }
}
