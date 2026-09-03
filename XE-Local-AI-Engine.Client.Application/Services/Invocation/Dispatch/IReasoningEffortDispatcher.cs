namespace XE_Local_AI_Engine.Client.Services.Invocation.Dispatch;

/// <summary>
///     The tier a turn authored with reasoning effort <c>auto</c> resolves to. Ordinals are stable — they are the
///     vocabulary of the persisted <c>dispatched_tier</c> column — and are never renumbered.
/// </summary>
public enum ReasoningTier
{
    /// <summary>Least reasoning: a short answer on a short question. Optionally served by a small node-local model.</summary>
    Fast = 0,

    /// <summary>The everyday middle, and the answer whenever no signal is decisive.</summary>
    Normal = 1,

    /// <summary>Most reasoning: code, an explicit "think it through", a long prompt, or a deep conversation.</summary>
    Deep = 2
}

/// <summary>
///     The persisted <c>dispatched_tier</c> vocabulary. Written to a column and read back by the measurement queries,
///     so the three labels are stable and are never renamed — hence a switch rather than a case fold of the enum name.
/// </summary>
internal static class ReasoningTierLabels
{
    internal static string For(ReasoningTier tier)
    {
        return tier switch
        {
            ReasoningTier.Fast => "fast",
            ReasoningTier.Normal => "normal",
            ReasoningTier.Deep => "deep",
            _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "Unknown reasoning tier.")
        };
    }
}

/// <summary>
///     Everything the reasoning-effort dispatcher is allowed to look at. Immutable constraints are deliberately
///     ABSENT: approval policy, secret masking, the loopback/Host gates, path guards, the sandbox,
///     <c>AllowCloudModelAccess</c>, node-local-only analysis/eval/extraction/judge models and tool authorisation are
///     never routing inputs, and no member here can carry one. Every field has exactly one named source on the
///     runtime package, so nothing is invented at the call site.
/// </summary>
/// <param name="ResolvedModel">The model the turn would run on before dispatch (the runner's resolved model).</param>
/// <param name="SupportsThinking">Whether the resolved model advertises the graded thinking capability.</param>
/// <param name="ReasoningBudgetEnforceable">Whether llama-server can enforce a per-request reasoning budget for it.</param>
/// <param name="AllowAutoModelSwap">Whether the model may be replaced (false = pinned; see <c>RuntimePackage</c>).</param>
/// <param name="HasOrchestration">Whether the turn drives a compiled orchestration.</param>
/// <param name="ConversationDepth">Number of messages in the turn's conversation context.</param>
/// <param name="LatestUserText">
///     The latest user message's text. Read in-memory for shape only (length, a code fence, a phrase) and NEVER
///     persisted, logged, or put in a log scope — see the dispatcher's logging invariant.
/// </param>
/// <param name="HasAttachments">Whether an image rides the latest user turn.</param>
/// <param name="OfferedToolCount">How many tools the turn offers. NEVER a score term — it only refuses the model swap.</param>
/// <param name="HasSkills">Whether the turn carries resolved agent skills.</param>
/// <param name="HasResponseSchema">Whether the turn's output is constrained to a JSON schema.</param>
/// <param name="IsUnattended">Whether this is a scheduled/headless run.</param>
public sealed record ReasoningDispatchRequest(string ResolvedModel,
    bool SupportsThinking,
    bool ReasoningBudgetEnforceable,
    bool AllowAutoModelSwap,
    bool HasOrchestration,
    int ConversationDepth,
    string LatestUserText,
    bool HasAttachments,
    int OfferedToolCount,
    bool HasSkills,
    bool HasResponseSchema,
    bool IsUnattended);

/// <summary>
///     What the dispatcher resolved <c>auto</c> into for one turn. The runner rewrites exactly these members onto the
///     package and then builds the agent definition as it always has.
/// </summary>
/// <param name="Tier">The resolved tier, also the persisted category label.</param>
/// <param name="Model">The model to run — the resolved model, or the node-local FAST model when a swap was admitted.</param>
/// <param name="Effort">A concrete effort from the ordinary vocabulary. Never <c>auto</c>: that is what was resolved.</param>
/// <param name="MaxOutputTokens">
///     Always null today. No tier caps the turn's output: the FAST cap was dropped because it bought nothing on the
///     reasoning side (the provider's own clamp already yields the full <c>low</c> budget without it) and cost real
///     history, since both context budgeters derive their output RESERVATION from the requested max-output-tokens.
///     The member stays so a future tier can carry one without moving the seam.
/// </param>
/// <param name="SupportsThinking">Re-resolved for <paramref name="Model" /> when it was swapped; the input's value otherwise.</param>
/// <param name="ReasoningBudgetEnforceable">Likewise — a stale flag after a swap sends a budget the model 400s on.</param>
/// <param name="ReasonCode">
///     A stable kebab-case label, the ONLY dispatcher output that may be logged or displayed. Carries no signal
///     value and no message text.
/// </param>
/// <param name="CapacityReservation">
///     The ledger reservation a swap's capacity admission produced, or null. The RUNNER owns its disposal and must
///     release it at turn end (or before a fallback re-run), or later admissions are wrongly rejected.
/// </param>
public sealed record ReasoningDispatchDecision(ReasoningTier Tier,
    string Model,
    string Effort,
    int? MaxOutputTokens,
    bool SupportsThinking,
    bool ReasoningBudgetEnforceable,
    string ReasonCode,
    IDisposable? CapacityReservation);

/// <summary>
///     Stable kebab-case reason labels for <see cref="ReasoningDispatchDecision.ReasonCode" />. Safe to log and to
///     show: each names a RULE, never a signal value. Split into the tier reasons (why this tier) and the swap
///     reasons (why the model was not replaced); a FAST turn reports a swap reason when a gate refused, and the tier
///     reason otherwise.
/// </summary>
public static class ReasoningDispatchReasons
{
    /// <summary>An orchestrated turn is always <see cref="ReasoningTier.Normal" /> and is never swapped.</summary>
    public const string Orchestration = "orchestration";

    /// <summary>The resolved model has no graded thinking ladder, so the tier maps onto the binary on/off pair.</summary>
    public const string BinaryModel = "binary-model";

    /// <summary>A fenced code block in the latest user message.</summary>
    public const string CodeFence = "code-fence";

    /// <summary>An explicit "think it through"-class phrase.</summary>
    public const string DeepPhrase = "deep-phrase";

    /// <summary>A long user message.</summary>
    public const string LongMessage = "long-message";

    /// <summary>A conversation that has already run deep.</summary>
    public const string DeepContext = "deep-context";

    /// <summary>An explicit "quick answer"-class phrase.</summary>
    public const string FastPhrase = "fast-phrase";

    /// <summary>A short question early in a conversation.</summary>
    public const string ShortTurn = "short-turn";

    /// <summary>No signal was decisive.</summary>
    public const string Balanced = "balanced";

    /// <summary>The model was picked by the user or honored from the agent's pin, so it is not ours to replace.</summary>
    public const string ModelPinned = "model-pinned";

    /// <summary>The turn can execute tools; the offer was ranked and authorised against the resolved model.</summary>
    public const string ToolsNoSwap = "tools-no-swap";

    /// <summary>An image rides the turn and the egress gate admitted it against the resolved model.</summary>
    public const string AttachmentsNoSwap = "attachments-no-swap";

    /// <summary>The turn expects the model to fetch and follow a skill body.</summary>
    public const string SkillsNoSwap = "skills-no-swap";

    /// <summary>The turn's output is grammar-constrained for the resolved model.</summary>
    public const string SchemaNoSwap = "schema-no-swap";

    /// <summary>A scheduled/headless run already holds a capacity reservation for its effective model.</summary>
    public const string UnattendedNoSwap = "unattended-no-swap";

    /// <summary>The resolved model is cloud or external; a swap would change the egress posture the turn was authorised under.</summary>
    public const string CloudNoSwap = "cloud-no-swap";

    /// <summary>This node names no FAST model, so the tier is served by the resolved model at a lower effort.</summary>
    public const string FastModelUnset = "fast-model-unset";

    /// <summary>The configured FAST model IS the resolved model, so there is nothing to swap to.</summary>
    public const string FastModelIsActiveModel = "fast-model-is-active-model";

    /// <summary>The configured FAST model is not an installed node-local model.</summary>
    public const string FastModelNotLocal = "fast-model-not-local";

    /// <summary>Capacity refused the FAST model's admission.</summary>
    public const string FastModelNoCapacity = "fast-model-no-capacity";

    /// <summary>
    ///     The FAST model's process exists but cannot serve this turn — it is profiling-owned, draining, or gone. Also
    ///     the reason the runner reports when a swapped send fails before its first token and is re-run on the
    ///     original model.
    /// </summary>
    public const string FastModelUnavailable = "fast-model-unavailable";
}

/// <summary>
///     Resolves the reasoning effort <c>auto</c> into a concrete <c>{model, effort, output budget}</c> for ONE turn.
///     Deterministic: no model call, no embedding, no randomness, no clock — the same request always produces the
///     same decision. Invoked by the invocation runner only when the turn's normalized effort is <c>auto</c>, so
///     every other turn is byte-identical to today and never even resolves this service.
/// </summary>
public interface IReasoningEffortDispatcher
{
    /// <summary>Resolves one turn. Never throws for a routing reason: under any refusal it falls back to the resolved model at a lower effort.</summary>
    Task<ReasoningDispatchDecision> DispatchAsync(ReasoningDispatchRequest request, CancellationToken cancellationToken);
}
