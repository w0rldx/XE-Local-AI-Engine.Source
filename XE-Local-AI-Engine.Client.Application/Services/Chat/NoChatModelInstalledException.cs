namespace XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>
///     Thrown when a "Local runtime default" send (request model unspecified) cannot resolve an installed GGUF
///     (llama.cpp) chat-capable model — no chat model is installed on the node. The chat stream / regeneration paths
///     classify this as <c>FailureCategory.ModelNotInstalled</c> and surface a clear, actionable terminal error
///     ("No chat model installed. Pull a GGUF model to start chatting.") instead of routing the stale default to a dead
///     provider and reporting the generic "Provider unreachable.".
/// </summary>
public sealed class NoChatModelInstalledException()
    : InvalidOperationException("No chat model installed. Pull a GGUF model to start chatting.");
