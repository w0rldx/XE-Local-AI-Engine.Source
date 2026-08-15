namespace XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     Thrown when the host environment, not the request, defeats a repository import: the Git file index could not be
///     read, a file could not be opened or read safely, or a file changed underneath the reader. The caller did nothing
///     wrong and cannot fix it by sending a different request, so the import endpoint deliberately does NOT catch this
///     — it falls through to the global handler and becomes a 500, instead of being echoed as a client error.
///     <para>The message is fixed and content-free; the underlying failure travels as the inner exception, for logs only.</para>
/// </summary>
public sealed class KnowledgeRepositoryReadException : Exception
{
    public KnowledgeRepositoryReadException(string message) : base(message)
    {
    }

    public KnowledgeRepositoryReadException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
