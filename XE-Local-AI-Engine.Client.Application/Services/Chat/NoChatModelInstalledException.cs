namespace XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>
///     Thrown when a "Local runtime default" send cannot resolve an installed GGUF chat-capable model, because the
///     node has none installed.
/// </summary>
/// <remarks>
///     The send and regenerate paths classify it as <c>FailureCategory.ModelNotInstalled</c> and surface an
///     actionable terminal, rather than routing the stale default to a dead provider and reporting the generic
///     "Provider unreachable.".
/// </remarks>
public sealed class NoChatModelInstalledException : InvalidOperationException
{
    public NoChatModelInstalledException() : base("No chat model installed. Pull a GGUF model to start chatting.")
    {
    }
}
