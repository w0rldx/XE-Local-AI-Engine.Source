namespace XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     Thrown when a repository import is refused because of what the caller asked for: the registered repository is
///     unavailable/unsafe, or the repository is past a configured import bound (file count, aggregate bytes, per-file
///     bytes). The message is a fixed, content-free sentence that is safe to echo to the operator, so the endpoint maps
///     this to 400 — unlike <see cref="KnowledgeRepositoryReadException" />, which is the environment failing.
/// </summary>
public sealed class KnowledgeRepositoryImportRejectedException : Exception
{
    public KnowledgeRepositoryImportRejectedException(string message) : base(message)
    {
    }

    public KnowledgeRepositoryImportRejectedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
