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
    ///     The model rejected the request because it does not support a requested capability (Ollama HTTP 400
    ///     "does not support thinking" / "does not support tools"). Surfaced instead of the generic
    ///     <see cref="ProviderUnreachable" /> so the operator can pick a capable model.
    /// </summary>
    ModelCapabilityUnsupported = 8,

    /// <summary>
    ///     The provider could not load the model (Ollama HTTP 500, e.g. an unsupported model architecture or a failed
    ///     blob load). Surfaced instead of the generic <see cref="ProviderUnreachable" />; the sanitized message carries
    ///     no filesystem paths.
    /// </summary>
    ModelLoadFailed = 9,

    /// <summary>
    ///     A "Local runtime default" send could not resolve an installed GGUF (llama.cpp) chat-capable model — no chat
    ///     model is installed on the node. Surfaced instead of the generic <see cref="ProviderUnreachable" /> so the
    ///     frontend can show a clear "pull a GGUF model" call to action rather than reporting an unreachable provider.
    /// </summary>
    ModelNotInstalled = 10,

    /// <summary>
    ///     The conversation history still exceeded the resolved context budget after the deterministic budgeter's
    ///     two-pass truncation (oversized tool-result excerpting, then whole-turn dropping). Surfaced BEFORE any
    ///     provider call — a clean, classified hard-stop instead of silently overrunning the model's launched context
    ///     window or being rejected deep inside the provider with an opaque error.
    /// </summary>
    ContextWindowExceeded = 11
}
