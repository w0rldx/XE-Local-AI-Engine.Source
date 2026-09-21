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
    ///     The offered Codex model ids, strongest-first (<c>gpt-5.6-sol</c>, the frontier model, leads). The node
    ///     default (<see cref="CodexOptions.DefaultModel" />) is <c>gpt-5.6-terra</c>, not necessarily the first entry.
    /// </summary>
    public static IReadOnlyList<string> ModelIds { get; } =
    [
        "gpt-5.6-sol",
        "gpt-5.6-terra",
        "gpt-5.6-luna",
        "gpt-5.5",
        "gpt-5.4",
        "gpt-5.4-mini",
        "gpt-5.3-codex-spark"
    ];

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
