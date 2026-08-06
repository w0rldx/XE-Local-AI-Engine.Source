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
