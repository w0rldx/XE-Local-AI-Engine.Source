namespace XE_Local_AI_Engine.Client.Services.Chat;

using System.Runtime.InteropServices;

/// <summary>
///     One model's advertised <c>thinking</c>/<c>tools</c> capabilities plus its provider locality, all resolved from a
///     single provider-routing decision. <see cref="IsCloud" /> feeds ONLY the private-data gates; the capability flags
///     are independent of it, so a fail-closed locality never disturbs reasoning or tool detection.
///     <para>
///         A value type on purpose: <c>default</c> is the safe not-capable, node-local answer, so an unresolved value
///         can never be a null that faults a caller reading a flag.
///     </para>
/// </summary>
[StructLayout(LayoutKind.Auto)]
public readonly record struct ModelCapabilitySnapshot(bool SupportsThinking, bool SupportsTools, bool IsCloud)
{
    /// <summary>
    ///     Whether the model accepts image input. Only a node-local llama.cpp GGUF can advertise it (from the
    ///     descriptor's projector-gated flag); Codex, Azure Foundry and Ollama-routed models all resolve non-vision.
    ///     Init-only rather than positional so the three-member deconstruction the capability callers use is the
    ///     thinking/tools/locality triple, and an unset value is the safe non-vision default.
    /// </summary>
    public bool SupportsVision { get; init; }

    /// <summary>
    ///     Whether llama-server can ENFORCE a per-request <c>reasoning_budget_tokens</c> for this model — its chat
    ///     template renders a literal reasoning end marker, which is what llama.cpp turns into the non-empty
    ///     think-end-tag set its budget gate requires. Only a node-local llama.cpp GGUF can report
    ///     <see langword="false" /> (from the descriptor's template detection). Every other route reports the value the
    ///     budget marker's absence from its wire makes correct: Codex and Ollama, whose thinking-capable models would
    ///     read it, resolve <see langword="true" />; the Azure arms resolve a non-thinking model, where it is never read.
    ///     <para>
    ///         Read ONLY together with <see cref="SupportsThinking" /> — the budget is emitted exclusively on the graded
    ///         branch — which is why <c>default</c> (a not-capable, node-local model) leaving this
    ///         <see langword="false" /> is unobservable.
    ///     </para>
    /// </summary>
    public bool ReasoningBudgetEnforceable { get; init; }
}
