namespace XE_Local_AI_Engine.Client.Endpoints.Knowledge.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     FastEndpoints handler for deleting one knowledge-base document (DELETE by id). Delegates to the purge service,
///     which issues the explicit ordered raw-SQL deletes (vectors → chunks → sections → document row) in one transaction
///     — the schema cascade cannot be relied upon because foreign-key enforcement is off on the runtime connection — and
///     then removes the on-disk encrypted bytes. Returns 404 when the id is unknown, otherwise 204.
/// </summary>
public sealed class DeleteKnowledgeDocumentEndpoint(IKnowledgeDocumentPurgeService purgeService)
    : Endpoint<KnowledgeDocumentRouteRequest>
{
    private readonly IKnowledgeDocumentPurgeService _purgeService = purgeService ?? throw new ArgumentNullException(nameof(purgeService));

    public override void Configure()
    {
        Delete(LocalApiRoutes.KnowledgeBase.DocumentById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(KnowledgeDocumentRouteRequest req, CancellationToken ct)
    {
        var deleted = await _purgeService.PurgeAsync(req.DocumentId, ct).ConfigureAwait(false);
        if (!deleted)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
            return;
        }

        await Send.NoContentAsync(ct).ConfigureAwait(false);
    }
}
