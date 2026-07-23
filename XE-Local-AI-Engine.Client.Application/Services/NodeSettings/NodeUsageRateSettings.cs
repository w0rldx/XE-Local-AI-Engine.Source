namespace XE_Local_AI_Engine.Client.Services.NodeSettings;

using System.Text.Json.Serialization;

/// <summary>
///     A per-model price point used to estimate the USD cost of token usage: two rates quoted in US dollars per one
///     million tokens. Reasoning tokens are billed at the <see cref="OutputPer1M" /> rate (they are model output), so the
///     estimate is <c>InputPer1M/1e6 * promptTokens + OutputPer1M/1e6 * (completionTokens + reasoningTokens)</c>.
/// </summary>
public sealed record ModelRate
{
    /// <summary>Price of one million input/prompt tokens, in US dollars.</summary>
    public double InputPer1M { get; init; }

    /// <summary>Price of one million output tokens (completion AND reasoning), in US dollars.</summary>
    public double OutputPer1M { get; init; }

    /// <summary>
    ///     Shared validity predicate (one authority for the boundary validator, the store's <c>Normalize</c>, and the
    ///     resolver): both rates must be finite and non-negative. A negative or NaN/∞ rate is a mistake, not a price.
    ///     <see cref="System.Text.Json.Serialization.JsonIgnoreAttribute" />d — it is an internal check, not part of the
    ///     wire contract, so it never appears in the OpenAPI schema or the persisted JSON.
    /// </summary>
    [JsonIgnore]
    public bool HasValidRates => double.IsFinite(InputPer1M) && double.IsFinite(OutputPer1M) && InputPer1M >= 0 && OutputPer1M >= 0;
}

/// <summary>
///     The persisted operator override of usage cost rates, stored as JSON inside <see cref="StoredNodeSettings" /> and
///     used by the usage-summary cost estimate. <see cref="Models" /> is keyed by model NAME (matched case-insensitively
///     against the run-envelope <c>ModelName</c>); a value overrides the built-in default rate table for that model.
///     <see langword="null" /> / absent (the default) means no operator override — the resolver falls back to the built-in
///     defaults, and any model with neither an override nor a default is treated as unpriced (zero).
///     <para>
///         Local runtimes (llama.cpp / Ollama) are always free regardless of this map — that is enforced in the resolver,
///         not here. The map is string-keyed so <c>node-settings.json</c> stays human-editable. Negative / non-finite
///         entries are dropped by <c>NodeSettingsStore.Normalize</c> on read (the persistence authority; the resolver also
///         guards defensively). Edits apply on the next usage-summary read (the resolver reads current node settings).
///     </para>
/// </summary>
public sealed record NodeUsageRateSettings
{
    /// <summary>Per-model-name rate override, keyed by model name (case-insensitive).</summary>
    public IReadOnlyDictionary<string, ModelRate>? Models { get; init; }
}
