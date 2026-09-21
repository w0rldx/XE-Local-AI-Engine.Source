namespace XE_Local_AI_Engine.AI.Agent.Invocation;

using XE_Local_AI_Engine.Providers.Abstractions.External;

/// <summary>
///     Shared reasoning-effort mapping to the Ollama <c>think</c> value and the provider side channels, used by both
///     the single-agent path and the orchestration participant path.
/// </summary>
/// <remarks>
///     <c>InvocationAgentFactory.CreateAsync</c> and <c>ParticipantReasoningOptions.Build</c> need the identical
///     capability/effort matrix, so this class is the single source of truth and a new effort level or an Ollama
///     behavior change is made once. See docs/wiki/04-agent-mode.md ("The reasoning-effort matrix and the thinking
///     budget").
/// </remarks>
internal static class ReasoningOptionsResolver
{
    /// <summary>
    ///     The binary reasoning-"on" sentinel for a model that lacks the Ollama <c>thinking</c> capability but reasons
    ///     by default.
    /// </summary>
    /// <remarks>
    ///     "on", and any graded level carried onto such a model, makes the caller OMIT the think field so the model's
    ///     built-in reasoning runs; only "none" or unspecified suppresses it (see <see cref="IsReasoningRequested" />).
    ///     Thinking-capable models never take this path — they honor false/low/medium/high via
    ///     <see cref="ResolveThinkOption" />.
    /// </remarks>
    private const string BinaryReasoningOn = "on";

    /// <summary>
    ///     Codex-only side channel carrying the RAW normalized reasoning-effort string for a thinking-capable model, so
    ///     the Codex Responses boundary maps it to <c>ResponseReasoningEffortLevel</c> with full fidelity.
    /// </summary>
    /// <remarks>
    ///     The Ollama <c>think</c> key cannot carry <c>minimal</c> or <c>xhigh</c> — Ollama 400s on an unknown think
    ///     level — so those collapse to <c>think:true</c> there, and this key preserves the distinction without
    ///     touching the Ollama wire: the OllamaSharp AbstractionMapper reads a fixed option allowlist and ignores
    ///     unknown keys. Added ONLY when a graded or explicit effort is present, so the no-effort path stays
    ///     byte-identical.
    /// </remarks>
    internal const string CodexReasoningEffortKey = "codex_reasoning_effort";

    /// <summary>
    ///     In-process marker carrying the per-request thinking budget in tokens
    ///     (<see cref="ResolveReasoningBudgetTokens" />) that the llama.cpp chat client patches onto the outbound body
    ///     as <c>reasoning_budget_tokens</c>.
    /// </summary>
    /// <remarks>
    ///     Present ONLY when an explicit graded effort is requested on a thinking-capable model; without it
    ///     llama-server free-runs the reasoning until the context window is exhausted and the turn returns no final
    ///     answer. The key never reaches any wire, so only <c>DeferredLlamaServerChatClient</c> consumes it — and the
    ///     literal is duplicated there, because the AI.Agent assembly does not reference the LlamaServer provider.
    ///     Keep the two in sync.
    /// </remarks>
    internal const string LlamaReasoningBudgetMarkerKey = "xe.llama.reasoning_budget_tokens";

    /// <summary>
    ///     In-process marker carrying the turn's SELECTED reasoning effort, in the canonical lowercase vocabulary, for
    ///     an external OpenAI-compatible model.
    /// </summary>
    /// <remarks>
    ///     The external provider reads it, applies its registered default when the marker is absent, and clamps the
    ///     result to the interoperable <c>low|medium|high</c> set before putting it on the wire as
    ///     <c>reasoning_effort</c>. The literal is duplicated in
    ///     <c>ExternalProviderConstants.ReasoningEffortMarkerKey</c> because this assembly references no provider
    ///     project; a test pins the two spellings together — keep them in sync.
    /// </remarks>
    internal const string ExternalReasoningEffortMarkerKey = "xe.external.reasoning_effort";

    /// <summary>
    ///     Maps a normalized reasoning effort to the Ollama <c>think</c> option value for a thinking-capable model:
    ///     false, "low", "medium", "high", or true as the default and fallback.
    /// </summary>
    /// <remarks>
    ///     minimal and xhigh — OpenAI Responses levels Ollama does not understand, and 400s on — collapse to true here;
    ///     the Codex boundary reads the un-collapsed level from <see cref="CodexReasoningEffortKey" /> instead. The
    ///     "on" sentinel is covered defensively: the non-thinking-model branch normally handles it via
    ///     <see cref="IsReasoningRequested" />, and it reaches this graded path only when a definition or a stale
    ///     composer selection carries it onto a thinking-capable model.
    /// </remarks>
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
    ///     (<c>reasoning_budget_tokens</c>), or <see langword="null" /> to send no budget at all.
    /// </summary>
    /// <remarks>
    ///     Null is the unrestricted status quo — blank effort, <c>none</c>, the binary <c>on</c> sentinel and any
    ///     unrecognized value keep the model free-running. These are FIXED counts and neither caller knows the launched
    ///     window here, so the value is a ceiling rather than a promise;
    ///     <c>DeferredLlamaServerChatClient.ClampToGenerationRoom</c> is the seam that narrows it. See
    ///     docs/wiki/04-agent-mode.md ("The reasoning-effort matrix and the thinking budget") for the ladder's sizing.
    /// </remarks>
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
    ///     side channel, or <see langword="null" /> to omit it.
    /// </summary>
    /// <remarks>
    ///     Recognizes the OpenAI Responses graded levels and explicit <c>none</c>; blank, the binary <c>on</c> sentinel
    ///     and any unrecognized value return <see langword="null" />, so the Codex boundary falls back to interpreting
    ///     the Ollama <c>think</c> value, where true means its default effort. The input is expected already normalized
    ///     upstream by the Application layer's reasoning-effort normalizer.
    /// </remarks>
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
    ///     Returns the effort to carry on <see cref="ExternalReasoningEffortMarkerKey" /> for
    ///     <paramref name="modelId" />, or <see langword="null" /> to omit the marker entirely.
    /// </summary>
    /// <remarks>
    ///     Omitted for every non-external model — the marker would be inert, but the no-override guarantee says the
    ///     dictionary stays byte-identical for a model that cannot read it — and for a blank or unrecognized effort,
    ///     where its ABSENCE is meaningful: that is what lets the registered default apply. Otherwise the whole
    ///     vocabulary is carried, <c>none</c> and the binary <c>on</c> sentinel included even though the provider sends
    ///     no field for either, because both are turn-level decisions a registered default must NOT override.
    /// </remarks>
    internal static string? ResolveExternalReasoningEffort(string? modelId, string? reasoningEffort)
    {
        if (!ExternalModelId.HasExternalScheme(modelId) || string.IsNullOrWhiteSpace(reasoningEffort))
        {
            return null;
        }

        return reasoningEffort.Trim().ToUpperInvariant() switch
        {
            "NONE" => "none",
            "ON" => BinaryReasoningOn,
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
    ///     graded level. Only <c>none</c>, unspecified or blank returns false, which the caller sends as think:false.
    /// </summary>
    /// <remarks>
    ///     Used ONLY on the non-thinking-model branch. A graded level can be carried onto a model that lacks the Ollama
    ///     <c>thinking</c> capability — a definition pins it, or the composer keeps a stale selection across a model
    ///     switch — and the model cannot honor it (Ollama 400s on <c>think:&lt;level&gt;</c>), but the user still asked
    ///     to reason, so the caller OMITS the think field and lets the model's built-in reasoning run.
    /// </remarks>
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
