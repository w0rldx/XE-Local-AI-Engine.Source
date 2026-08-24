namespace XE_Local_AI_Engine.AI.Agent.Invocation;

/// <summary>
///     Shared reasoning-effort → Ollama <c>think</c> value / Codex side-channel mapping logic, used by both the
///     single-agent path (<c>InvocationAgentFactory.CreateAsync</c>) and the orchestration participant path
///     (<c>ParticipantReasoningOptions.Build</c>). Both call sites need the identical capability/effort matrix; this
///     class is the single source of truth so a future change to the matrix (a new effort level, an Ollama behavior
///     change) only has to be made once.
/// </summary>
internal static class ReasoningOptionsResolver
{
    /// <summary>
    ///     The binary reasoning-"on" sentinel for a model that lacks the Ollama <c>thinking</c> capability but reasons
    ///     by default. "on" — and any graded level (low/medium/high) carried onto such a model — makes the caller OMIT
    ///     the think field so the model's built-in reasoning runs; only "none"/unspecified suppresses it via think:false
    ///     (see <see cref="IsReasoningRequested" />). Thinking-capable models never take this path — they honor
    ///     false/low/medium/high via <see cref="ResolveThinkOption" />.
    /// </summary>
    private const string BinaryReasoningOn = "on";

    /// <summary>
    ///     Codex-only side channel carrying the RAW normalized reasoning-effort string
    ///     (minimal/low/medium/high/xhigh) for a thinking-capable model, so the Codex Responses boundary can map
    ///     it to <c>ResponseReasoningEffortLevel</c> with full fidelity. The Ollama <c>think</c> key cannot carry
    ///     <c>minimal</c>/<c>xhigh</c> (Ollama 400s on an unknown think level), so those collapse to
    ///     <c>think:true</c> there; this key preserves the distinction without affecting the Ollama wire — the
    ///     OllamaSharp AbstractionMapper reads only its fixed option allowlist and ignores unknown keys. The key
    ///     is added ONLY when a graded/explicit effort is present, so the no-effort path stays byte-identical
    ///     (single <c>think</c> entry).
    /// </summary>
    internal const string CodexReasoningEffortKey = "codex_reasoning_effort";

    /// <summary>
    ///     In-process marker carrying the per-request thinking budget in tokens
    ///     (<see cref="ResolveReasoningBudgetTokens" />) that the llama.cpp chat client patches onto the outbound body
    ///     as <c>reasoning_budget_tokens</c>. Present ONLY when an explicit graded effort is requested on a
    ///     thinking-capable model; without it llama-server free-runs the reasoning until the context window is
    ///     exhausted and the turn returns no final answer. Like the disable-thinking marker the key never reaches any
    ///     wire — the Ollama mapper reads a fixed allowlist and Codex reads its own keys, so only
    ///     <c>DeferredLlamaServerChatClient</c> consumes it. The literal is duplicated there (the AI.Agent assembly does
    ///     not reference the LlamaServer provider); keep the two in sync.
    /// </summary>
    internal const string LlamaReasoningBudgetMarkerKey = "xe.llama.reasoning_budget_tokens";

    /// <summary>
    ///     Maps a normalized reasoning effort to the Ollama <c>think</c> option value for a thinking-capable model:
    ///     false / "low" / "medium" / "high", or true (reason) as the default/fallback. minimal/xhigh — OpenAI
    ///     Responses reasoning levels Ollama does not understand — collapse to true here (Ollama 400s on an unknown
    ///     think level); the Codex boundary reads the un-collapsed level from <see cref="CodexReasoningEffortKey" />
    ///     instead. Also covers the "on" binary-reasoning sentinel defensively (it is normally handled by the
    ///     non-thinking-model branch via <see cref="IsReasoningRequested" />, and only reaches this graded path if an
    ///     agent definition or stale composer selection carries it onto a thinking-capable model).
    /// </summary>
    internal static object ResolveThinkOption(string? reasoningEffort)
    {
        if (string.IsNullOrWhiteSpace(reasoningEffort))
        {
            return true;
        }

        var normalized = reasoningEffort.Trim();
        if (string.Equals(normalized, "low", StringComparison.OrdinalIgnoreCase))
        {
            return "low";
        }

        if (string.Equals(normalized, "none", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(normalized, "medium", StringComparison.OrdinalIgnoreCase))
        {
            return "medium";
        }

        if (string.Equals(normalized, "high", StringComparison.OrdinalIgnoreCase))
        {
            return "high";
        }

        return true;
    }

    /// <summary>
    ///     Maps a normalized reasoning effort to the llama.cpp per-request thinking budget in tokens
    ///     (<c>reasoning_budget_tokens</c>), or <see langword="null" /> to send no budget at all. Null is the
    ///     unrestricted status quo: blank/unspecified effort, <c>none</c> (reasoning is being turned OFF, so a budget is
    ///     meaningless), the binary <c>on</c> sentinel, and any unrecognized value all keep the model free-running,
    ///     leaving the no-effort request byte-identical to before.
    ///     <para>
    ///         The levels are sized so the capped reasoning still leaves room for a real final answer inside the 64k
    ///         windows local runtimes are launched with: low is a short scratchpad, medium the everyday cap, and high
    ///         (24576) still leaves well over half the window for the answer plus the prompt. Without a cap a
    ///         Qwen3-class model can spend the whole window thinking and return no answer at all — the failure this
    ///         mapping exists to prevent. Because these are FIXED counts and neither caller knows the window here, the
    ///         value is a ceiling rather than a promise: <c>DeferredLlamaServerChatClient.ClampToGenerationRoom</c>
    ///         narrows it to half the room a SMALLER launched window (or an explicit max-output cap) actually leaves,
    ///         which is the seam that has those numbers. <c>minimal</c>/<c>xhigh</c> are the Codex-only levels
    ///         (see <see cref="ResolveCodexReasoningEffort" />); they are mapped rather than left null so a definition
    ///         that pins one onto a local model does not silently get MORE thinking than <c>high</c>.
    ///     </para>
    ///     <para>
    ///         llama-server honors the budget only for chat templates with explicit think-end tags — the same
    ///         Qwen3/DeepSeek-R1 family the capability detector classifies as graded-reasoning-capable — and ignores the
    ///         field otherwise. That is the same acceptable silent no-op as the existing <c>enable_thinking</c> switch.
    ///     </para>
    /// </summary>
    internal static int? ResolveReasoningBudgetTokens(string? reasoningEffort)
    {
        if (string.IsNullOrWhiteSpace(reasoningEffort))
        {
            return null;
        }

        return reasoningEffort.Trim().ToUpperInvariant() switch
        {
            "MINIMAL" => 1024,
            "LOW" => 2048,
            "MEDIUM" => 8192,
            "HIGH" or "XHIGH" => 24576,
            _ => null
        };
    }

    /// <summary>
    ///     Returns the canonical reasoning effort to carry on the Codex-only <see cref="CodexReasoningEffortKey" />
    ///     side channel, or <see langword="null" /> to omit it. Recognizes the OpenAI Responses graded levels
    ///     (<c>minimal</c>/<c>low</c>/<c>medium</c>/<c>high</c>/<c>xhigh</c>) and explicit <c>none</c>; blank, the
    ///     binary <c>on</c> sentinel, and any unrecognized value return <see langword="null" /> so the Codex boundary
    ///     falls back to interpreting the Ollama <c>think</c> value (true → its default effort). The input is expected
    ///     already normalized upstream by the Application layer's reasoning-effort normalizer.
    /// </summary>
    internal static string? ResolveCodexReasoningEffort(string? reasoningEffort)
    {
        if (string.IsNullOrWhiteSpace(reasoningEffort))
        {
            return null;
        }

        return reasoningEffort.Trim().ToUpperInvariant() switch
        {
            "NONE" => "none",
            "MINIMAL" => "minimal",
            "LOW" => "low",
            "MEDIUM" => "medium",
            "HIGH" => "high",
            "XHIGH" => "xhigh",
            _ => null
        };
    }

    /// <summary>
    ///     True when the effort asks the model to reason: the binary <see cref="BinaryReasoningOn" /> sentinel or a
    ///     graded level (low/medium/high). Used ONLY on the non-thinking-model branch — a graded level can be carried
    ///     onto a model that lacks the Ollama <c>thinking</c> capability (an agent definition pins it, or the composer
    ///     keeps a stale selection across a model switch). The model cannot honor the graded level (Ollama 400s on
    ///     <c>think:&lt;level&gt;</c>), but the user still asked to reason, so the caller OMITS the think field and lets
    ///     the model's built-in reasoning run. Only <c>none</c> (or unspecified/blank) returns false → think:false.
    /// </summary>
    internal static bool IsReasoningRequested(string? reasoningEffort)
    {
        if (string.IsNullOrWhiteSpace(reasoningEffort))
        {
            return false;
        }

        var normalized = reasoningEffort.Trim();
        return string.Equals(normalized, BinaryReasoningOn, StringComparison.OrdinalIgnoreCase)
               || string.Equals(normalized, "low", StringComparison.OrdinalIgnoreCase)
               || string.Equals(normalized, "medium", StringComparison.OrdinalIgnoreCase)
               || string.Equals(normalized, "high", StringComparison.OrdinalIgnoreCase);
    }
}
