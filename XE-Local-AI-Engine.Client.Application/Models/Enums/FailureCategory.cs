namespace XE_Local_AI_Engine.Client.Models.Enums;

/// <summary>
///     Enumerates supported failure category values.
/// </summary>
public enum FailureCategory
{
    Cancelled = 0,
    Timeout = 1,
    AgentRuntime = 2,
    ProviderUnreachable = 3,
    Unexpected = 4,
    AgentToolCall = 5,
    HashMismatch = 6,
    ModelUnavailable = 7,

    /// <summary>
    ///     The model rejected the request because it does not support a requested capability (an HTTP 400
    ///     "does not support thinking" / "does not support tools"). Surfaced instead of the generic
    ///     <see cref="ProviderUnreachable" /> so the operator can pick a capable model.
    /// </summary>
    ModelCapabilityUnsupported = 8,

    /// <summary>
    ///     The provider could not load the model (an HTTP 500, e.g. an unsupported model architecture or a failed
    ///     load). Surfaced instead of the generic <see cref="ProviderUnreachable" />; the sanitized message carries
    ///     no filesystem paths.
    /// </summary>
    ModelLoadFailed = 9,

    /// <summary>
    ///     The turn's model is not on this node: a "Local runtime default" send found no installed GGUF chat model, or the turn named a
    ///     model that is not installed.
    /// </summary>
    /// <remarks>
    ///     Surfaced instead of <see cref="ProviderUnreachable" /> or <see cref="InsufficientCapacity" /> so the frontend can point at the
    ///     Models page. Other chat models may be installed, so the two cases carry different server text.
    /// </remarks>
    ModelNotInstalled = 10,

    /// <summary>
    ///     The conversation history still exceeded the resolved context budget after the deterministic budgeter's two-pass truncation
    ///     (oversized tool-result excerpting, then whole-turn dropping).
    /// </summary>
    /// <remarks>
    ///     Surfaced BEFORE any provider call — a clean, classified hard-stop instead of silently overrunning the model's launched context
    ///     window or being rejected deep inside the provider with an opaque error.
    /// </remarks>
    ContextWindowExceeded = 11,

    /// <summary>
    ///     The provider or gateway refused the connection (HTTP 401/403, or a failed Azure sign-in). The message says whether to
    ///     replace the credential or names a denial no new key would change.
    /// </summary>
    ProviderAuthFailed = 12,

    /// <summary>
    ///     The provider or gateway rate-limited the request (HTTP 429). The message quotes a bounded <c>Retry-After</c>
    ///     value when the response carried one.
    /// </summary>
    ProviderRateLimited = 13,

    /// <summary>
    ///     The turn stopped at its provider-call budget: a work session step's own cap, or the node-wide ceiling on a
    ///     runaway tool or hand-off loop. A designed bound rather than a fault; a work session continues on its next step.
    /// </summary>
    CallBudgetReached = 14,

    /// <summary>
    ///     The node had no room to load the model next to the models already loaded or loading (a capacity refusal of
    ///     the model's launch). The sanitized message names those models and says what to do.
    /// </summary>
    InsufficientCapacity = 15
}
