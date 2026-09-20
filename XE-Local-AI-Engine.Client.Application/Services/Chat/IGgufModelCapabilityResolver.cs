namespace XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>
///     Resolves a node-local GGUF model's advertised <c>thinking</c> and <c>tools</c> capabilities from its
///     installed-model descriptor, detected offline from the chat template rather than an Ollama probe.
/// </summary>
/// <remarks>
///     The send and regenerate paths consult it so a GGUF is offered tools and a graded reasoning effort exactly when
///     its template supports them, without ever probing the Ollama daemon that desktop mode does not have.
/// </remarks>
public interface IGgufModelCapabilityResolver
{
    /// <summary>
    ///     The GGUF capabilities for <paramref name="modelName" />, or <see langword="null" /> when no installed GGUF
    ///     carries that name and another runtime's path must resolve it.
    /// </summary>
    /// <remarks>The lookup reuses the store's per-file header cache, so a hit reads no file.</remarks>
    Task<GgufModelCapabilities?> TryResolveAsync(string modelName, CancellationToken cancellationToken = default);
}

/// <summary>The thinking / tools / vision capabilities advertised by an installed GGUF model.</summary>
/// <param name="ReasoningBudgetEnforceable">
///     Whether llama-server can ENFORCE a per-request <c>reasoning_budget_tokens</c>, which its chat template must
///     render a literal reasoning end marker for.
/// </param>
/// <remarks>
///     <c>ReasoningBudgetEnforceable</c> is read only alongside <c>SupportsThinking</c>, since a budget is sent
///     exclusively on the graded branch. It defaults to <see langword="true" />, the inert safe value: only a
///     positively-detected closing-tag-less template turns the cap off, so no unknown silently removes it.
/// </remarks>
public readonly record struct GgufModelCapabilities(
    bool SupportsThinking,
    bool SupportsTools,
    bool SupportsVision,
    bool ReasoningBudgetEnforceable = true);
