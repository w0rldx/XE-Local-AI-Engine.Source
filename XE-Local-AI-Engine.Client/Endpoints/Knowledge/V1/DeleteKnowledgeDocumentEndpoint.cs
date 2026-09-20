namespace XE_Local_AI_Engine.Client.Endpoints.Knowledge.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     Deletes one knowledge-base document: 404 when the id is unknown, otherwise 204.
/// </summary>
/// <remarks>
///     Delegates to the purge service, which issues the explicit ordered raw-SQL deletes (vectors, chunks, sections,
///     document row) in one transaction — the schema cascade cannot be relied upon, because foreign-key enforcement is
///     off on the runtime connection — and then removes the on-disk encrypted bytes.
/// </remarks>
public sealed class DeleteKnowledgeDocumentEndpoint : Endpoint<KnowledgeDocumentRouteRequest>
{
    private readonly IKnowledgeDocumentPurgeService _purgeService;

    public DeleteKnowledgeDocumentEndpoint(IKnowledgeDocumentPurgeService purgeService)
    {
        ArgumentNullException.ThrowIfNull(purgeService);
        _purgeService = purgeService;
    }

    public override void Configure()
    {
        Delete(LocalApiRoutes.KnowledgeBase.DocumentById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(KnowledgeDocumentRouteRequest req, CancellationToken ct)
    {
        var deleted = await _purgeService.PurgeAsync(req.DocumentId, ct);
        if (!deleted)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.NoContentAsync(ct);
    }
}
