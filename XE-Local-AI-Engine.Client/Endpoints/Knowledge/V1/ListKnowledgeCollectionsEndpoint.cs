namespace XE_Local_AI_Engine.Client.Endpoints.Knowledge.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     Lists the knowledge collections that hold documents, with their document counts, so a client can offer them as
///     choices. <c>DEFAULT</c> is always listed first, even when it is empty.
/// </summary>
public sealed class ListKnowledgeCollectionsEndpoint : EndpointWithoutRequest<ListKnowledgeCollectionsResponse>
{
    private readonly IKnowledgeDocumentCatalogService _catalogService;

    public ListKnowledgeCollectionsEndpoint(IKnowledgeDocumentCatalogService catalogService)
    {
        ArgumentNullException.ThrowIfNull(catalogService);
        _catalogService = catalogService;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.KnowledgeBase.Collections);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var collections = await _catalogService.ListCollectionsAsync(ct);
        await Send.OkAsync(new ListKnowledgeCollectionsResponse
            {
                Items = [.. collections.Select(static collection => new KnowledgeCollectionResponse
                {
                    CollectionId = collection.CollectionId,
                    DocumentCount = collection.DocumentCount
                })]
            },
            ct);
    }
}
