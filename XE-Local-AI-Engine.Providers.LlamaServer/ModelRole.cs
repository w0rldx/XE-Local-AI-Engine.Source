namespace XE_Local_AI_Engine.Providers.LlamaServer;

/// <summary>
///     Role a llama-server process serves. A distinct <c>(model, role)</c> pair is always a distinct process
///     because chat and embeddings require mutually exclusive launch flags (chat needs <c>--jinja</c>; embeddings
///     need a non-<c>none</c> pooling type to expose <c>/v1/embeddings</c>). Each role-process counts against the
///     shared loaded-cap.
/// </summary>
public enum ModelRole
{
    /// <summary>Chat / tool-calling process launched with <c>--jinja</c>.</summary>
    Chat = 0,

    /// <summary>Embedding process launched with a non-<c>none</c> pooling type.</summary>
    Embedding = 1
}
