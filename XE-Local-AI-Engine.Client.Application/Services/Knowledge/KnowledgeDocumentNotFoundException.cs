namespace XE_Local_AI_Engine.Client.Services.Knowledge;

using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>Identifies a knowledge-base document id the client supplied that names no document.</summary>
/// <remarks>
///     <c>ResourceNotFoundExceptionHandler</c> answers it with the surface's bodyless 404, so the Knowledge endpoints do
///     not branch on a null lookup themselves. The collection-scoped read the agent tools use stays nullable: there an
///     unknown id is an answer the tool reports, not a failed request.
/// </remarks>
public sealed class KnowledgeDocumentNotFoundException : ResourceNotFoundException
{
    public KnowledgeDocumentNotFoundException(string message) : base(message)
    {
    }
}
