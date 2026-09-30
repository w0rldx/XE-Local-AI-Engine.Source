namespace XE_Local_AI_Engine.Providers.HuggingFace.Implementation;

/// <summary>
///     The capability classification of a single GGUF model derived from its chat template: the tool / graded-reasoning
///     / native-reasoning flags plus the matching Ollama-style capability tokens (always including <c>completion</c>).
/// </summary>
/// <param name="IsReasoningCapable">GRADED reasoning: the template exposes a switchable thinking channel, so a <c>think:&lt;level&gt;</c> control is available.</param>
/// <param name="IsNativeReasoningCapable">NATIVE reasoning: the model reasons on a channel baked into its template with no graded switch (harmony/gpt-oss).</param>
/// <param name="ReasoningBudgetEnforceable">Whether llama-server can ENFORCE a per-request <c>reasoning_budget_tokens</c> for this template. Defaults to <see langword="true" />.</param>
/// <remarks>
///     The two reasoning flags are mutually exclusive: a native-reasoning model must stay OUT of the graded path, and the enforcing
///     layer keeps it on the omit-<c>think</c> branch. <paramref name="ReasoningBudgetEnforceable" /> reports whether the template
///     renders a literal reasoning END marker (see <see cref="GgufCapabilityDetector" />'s budget markers) and is only meaningful with
///     <paramref name="IsReasoningCapable" />, since a budget is sent exclusively on the graded branch. For every other template —
///     native-reasoning, plain, or none at all — it stays <see langword="true" />, so nothing downstream reads it as "drop the cap".
/// </remarks>
internal readonly record struct GgufCapabilities(
    bool IsToolCapable,
    bool IsReasoningCapable,
    bool IsNativeReasoningCapable,
    IReadOnlyList<string> Capabilities,
    bool ReasoningBudgetEnforceable = true);
