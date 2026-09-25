namespace XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>
///     Resolves the model a "Local runtime default" send, one that names no model, should run on.
/// </summary>
/// <remarks>
///     It resolves ONLY to an installed GGUF chat-capable model and NEVER to Ollama, which stays opt-in: an explicit
///     Ollama pick is unaffected, but the default must not route a stale id to a possibly absent daemon.
/// </remarks>
public interface ILocalDefaultChatModelResolver
{
    /// <summary>
    ///     The local-default chat model name, or <see langword="null" /> when no installed GGUF chat model exists, so
    ///     the caller can surface "no chat model installed" rather than route to a dead provider.
    /// </summary>
    /// <remarks>
    ///     It enumerates the installed GGUF models, drops the Embedding-classified ones on the same chat-capability
    ///     notion the chat picker uses. A model already resident in a llama.cpp Chat-role process wins first (the
    ///     persisted default if resident, else the most recently used resident one), so a default send reuses loaded
    ///     weights. With none resident it picks <paramref name="persistedDefault" /> if it is an installed GGUF chat
    ///     model, else the most-recently-modified one, tie-broken by name.
    /// </remarks>
    Task<string?> ResolveAsync(string? persistedDefault, CancellationToken cancellationToken = default);
}
