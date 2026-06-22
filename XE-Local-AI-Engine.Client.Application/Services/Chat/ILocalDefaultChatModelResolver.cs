namespace XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>
///     Resolves the model a "Local runtime default" send (request model unspecified) should run on. The local default
///     resolves ONLY to an installed GGUF (llama.cpp) chat-capable model and NEVER to Ollama: Ollama stays opt-in, so a
///     user who explicitly selects an Ollama model is unaffected, but the local default must not silently route a stale
///     config/node-settings id to a (possibly absent) Ollama daemon.
/// </summary>
public interface ILocalDefaultChatModelResolver
{
    /// <summary>
    ///     Resolves the local-default chat model name, or <see langword="null" /> when no installed GGUF chat model is
    ///     available (the caller then surfaces a clear "no chat model installed" error rather than routing to a dead
    ///     provider). Enumerates the installed GGUF models, drops Embedding-classified ones (the same chat-capability
    ///     notion the chat picker uses — see <c>LocalModelsMapper.ToLlamaCppModelResponses</c> / <c>ModelKind</c>), and
    ///     applies the pick order: <paramref name="persistedDefault" /> iff it is an installed GGUF chat model, else the
    ///     first installed GGUF chat model (most-recently-modified, tie-break by name).
    /// </summary>
    Task<string?> ResolveAsync(string? persistedDefault, CancellationToken cancellationToken = default);
}
