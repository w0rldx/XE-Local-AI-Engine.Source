namespace XE_Local_AI_Engine.Client.Endpoints.Knowledge.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     FastEndpoints handler for hybrid knowledge-base retrieval (POST). Delegates to the search service, which embeds the
///     query, runs the lexical FTS and model-scoped semantic arms, fuses them, and hydrates the hits. Titles and sections
///     derive from non-sensitive heading/storage references, so a result never exposes the encrypted file name.
/// </summary>
public sealed class SearchKnowledgeEndpoint(IKnowledgeSearchService searchService)
    : Endpoint<SearchKnowledgeRequest, SearchKnowledgeResponse>
{
    private const int MinLimit = 1;
    private const int MaxLimit = 50;
    private const int DefaultLimit = 10;
    private const int MaxQueryLength = 1000;

    private readonly IKnowledgeSearchService _searchService = searchService ?? throw new ArgumentNullException(nameof(searchService));

    public override void Configure()
    {
        Post(LocalApiRoutes.KnowledgeBase.Search);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(SearchKnowledgeRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Query))
        {
            AddError("A search query is required.");
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
            return;
        }

        if (req.Query.Trim().Length > MaxQueryLength)
        {
            AddError($"The search query must be {MaxQueryLength} characters or fewer.");
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
            return;
        }

        var limit = req.Limit <= 0 ? DefaultLimit : Math.Clamp(req.Limit, MinLimit, MaxLimit);
        var request = new KnowledgeSearchRequest(req.Query, limit, req.DocumentId, req.ExpandNeighbors);
        var result = await _searchService.SearchAsync(request, ct).ConfigureAwait(false);

        await Send.OkAsync(new SearchKnowledgeResponse
            {
                Results = [.. result.Results.Select(ToResponse)]
            },
            ct).ConfigureAwait(false);
    }

    private static KnowledgeSearchHitResponse ToResponse(KnowledgeSearchHit hit)
    {
        return new KnowledgeSearchHitResponse
        {
            DocumentId = hit.DocumentId,
            ChunkId = hit.ChunkId,
            Title = hit.Title,
            Section = hit.Section,
            Content = hit.Content,
            Source = hit.Source,
            Score = hit.Score,
            ChunkIndex = hit.ChunkIndex,
            DocumentStatus = hit.DocumentStatus,
            ServingLastKnownGood = hit.ServingLastKnownGood
        };
    }
}
