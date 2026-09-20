namespace XE_Local_AI_Engine.Client.Services.Chat;

using System.Runtime.InteropServices;

/// <summary>
///     One model's advertised <c>thinking</c> and <c>tools</c> capabilities plus its provider locality, all from a
///     single provider-routing decision.
/// </summary>
/// <remarks>
///     <see cref="IsCloud" /> feeds ONLY the private-data gates and the capability flags are independent of it, so a
///     fail-closed locality never disturbs reasoning or tool detection. It is a value type on purpose:
///     <c>default</c> is the safe not-capable, node-local answer, so an unresolved value is never a faulting null.
/// </remarks>
[StructLayout(LayoutKind.Auto)]
public readonly record struct ModelCapabilitySnapshot(bool SupportsThinking, bool SupportsTools, bool IsCloud)
{
    /// <summary>
    ///     Whether the model accepts image input, which only a node-local GGUF can advertise, from the descriptor's
    ///     projector-gated flag; every other route resolves non-vision.
    /// </summary>
    /// <remarks>
    ///     It is init-only rather than positional, so the deconstruction capability callers use stays the
    ///     thinking, tools and locality triple and an unset value is the safe non-vision default.
    /// </remarks>
    public bool SupportsVision { get; init; }

    /// <summary>
    ///     Whether llama-server can ENFORCE a per-request <c>reasoning_budget_tokens</c>, which its chat template must
    ///     render a literal reasoning end marker for.
    /// </summary>
    /// <remarks>
    ///     Only a node-local GGUF can report <see langword="false" />, from the descriptor's template detection; every
    ///     other route reports the value the budget marker's absence from its wire makes correct. It is read ONLY
    ///     together with <see cref="SupportsThinking" />, since a budget is emitted exclusively on the graded branch,
    ///     which is why <c>default</c> leaving it <see langword="false" /> is unobservable.
    /// </remarks>
    public bool ReasoningBudgetEnforceable { get; init; }
}
