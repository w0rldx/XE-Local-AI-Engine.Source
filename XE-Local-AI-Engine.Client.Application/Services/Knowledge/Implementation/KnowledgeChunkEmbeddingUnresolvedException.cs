namespace XE_Local_AI_Engine.Client.Services.Knowledge.Implementation;

/// <summary>
///     A chunk vector this run waited on stayed unresolved because the run that owned computing it failed; the owner's
///     cause is not carried over.
/// </summary>
internal sealed class KnowledgeChunkEmbeddingUnresolvedException : InvalidOperationException
{
    public KnowledgeChunkEmbeddingUnresolvedException(string message)
        : base(message)
    {
    }
}
