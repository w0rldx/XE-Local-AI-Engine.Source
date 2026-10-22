namespace XE_Local_AI_Engine.Providers.CodexOAuth.Implementation;

/// <summary>
///     The set of Codex (ChatGPT-subscription) model ids this node offers, and the test for whether a given model id
///     is a Codex cloud model.
/// </summary>
/// <remarks>
///     The subscription Responses backend accepts only a fixed family of ids, and any other — a local Ollama name, or
///     the bare <c>gpt-5-codex</c> — is rejected with HTTP 400. This mirrors the opencode reference client's
///     <c>ALLOWED_MODELS</c> set, which is the catalog source because no "list my account's models" Codex endpoint is
///     documented; a stale id is a one-line change here. It is the single source of truth for both surfaces that must
///     agree on "is this a Codex model": the node model-list endpoint and the chat capability gate.
/// </remarks>
public static class CodexModelCatalog
{
    /// <summary>
    ///     The offered Codex model ids, strongest-first (the <c>gpt-6</c> family leads, then the <c>gpt-5.6</c> family). The node
    ///     default (<see cref="CodexOAuth.Options.CodexOptions.DefaultModel" />) is <c>gpt-5.6-terra</c>, not necessarily the first entry.
    /// </summary>
    public static IReadOnlyList<string> ModelIds { get; } =
    [
        "gpt-6-astra",
        "gpt-6-sol",
        "gpt-6-luna",
        "gpt-5.6-sol",
        "gpt-5.6-terra",
        "gpt-5.6-luna"
    ];

    /// <summary>
    ///     The total context window of a Codex model id, in tokens (ordinal, case-insensitive), or <see langword="null" />
    ///     when the id is unknown or no official figure could be sourced for it.
    /// </summary>
    /// <remarks>
    ///     Values come from the Codex CLI's ChatGPT-path model catalog (read 2026-10-22), not the larger API-key-path
    ///     figures. 272,000 is the catalog's default <c>context_window</c>; its opt-in 872,000 maximum is not used by
    ///     this node. An id absent here, such as a retired one, has no sourced window, so the meter shows "unknown".
    /// </remarks>
    public static int? ContextTokensFor(string? modelId)
    {
        return modelId is not null && ContextTokens.TryGetValue(modelId, out var tokens) ? tokens : null;
    }

    private static readonly Dictionary<string, int> ContextTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gpt-6-astra"] = 272_000,
        ["gpt-6-sol"] = 272_000,
        ["gpt-6-luna"] = 272_000,
        ["gpt-5.6-sol"] = 272_000,
        ["gpt-5.6-terra"] = 272_000,
        ["gpt-5.6-luna"] = 272_000
    };

    /// <summary>
    ///     True when <paramref name="modelId" /> is one of the offered Codex cloud model ids (ordinal, case-insensitive).
    ///     A null/blank id is never a Codex model.
    /// </summary>
    public static bool IsCodexModel(string? modelId)
    {
        // Linear scan over the tiny allow-list with an ordinal, case-insensitive comparer. Avoiding a
        // cached HashSet means there is no static-field init-order dependency for a member reformatter to break.
        return !string.IsNullOrWhiteSpace(modelId)
               && ModelIds.Contains(modelId, StringComparer.OrdinalIgnoreCase);
    }
}
