namespace XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     Role a GGUF model file is intended to serve. Mirrors the chat/embedding process split (epic §7.8): the same
///     repo can ship a chat model and an embedding model, and the runtime spawns a distinct <c>llama-server</c> per
///     role. <see cref="Unknown" /> is the default until a caller hint classifies the file.
/// </summary>
public enum GgufRole
{
    /// <summary>Role not yet classified.</summary>
    Unknown = 0,

    /// <summary>Chat / text-generation model.</summary>
    Chat = 1,

    /// <summary>Embedding model.</summary>
    Embedding = 2
}
