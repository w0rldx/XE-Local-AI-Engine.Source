namespace XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>
///     Resolves a node-local GGUF (llama.cpp) model's advertised <c>thinking</c>/<c>tools</c> capabilities from its
///     installed-model descriptor (detected offline from the GGUF chat template, NOT via an Ollama <c>/api/show</c>
///     probe). The chat send / regenerate paths consult this so a GGUF model is offered tools and a graded reasoning
///     effort exactly when its template supports them — without ever probing the (absent, in desktop mode) Ollama
///     daemon.
/// </summary>
public interface IGgufModelCapabilityResolver
{
    /// <summary>
    ///     Resolves the GGUF capabilities for <paramref name="modelName" />, or <see langword="null" /> when no installed
    ///     GGUF carries that name (the model is served by another runtime — Ollama or Codex — and capabilities must be
    ///     resolved through that runtime's path instead). The lookup reuses the store's per-file header cache, so a hit
    ///     reads no file.
    /// </summary>
    Task<GgufModelCapabilities?> TryResolveAsync(string modelName, CancellationToken cancellationToken = default);
}

/// <summary>The thinking / tools / vision capabilities advertised by an installed GGUF model.</summary>
public readonly record struct GgufModelCapabilities(bool SupportsThinking, bool SupportsTools, bool SupportsVision);
