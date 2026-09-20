namespace XE_Local_AI_Engine.Client.Endpoints.Knowledge.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     Hybrid knowledge-base retrieval.
/// </summary>
/// <remarks>
///     Delegates to the search service, which embeds the query, runs the lexical FTS and model-scoped semantic arms,
///     fuses them and hydrates the hits. Titles and sections derive from non-sensitive heading and storage references,
///     so a result never exposes the encrypted file name.
/// </remarks>
public sealed class SearchKnowledgeEndpoint : Endpoint<SearchKnowledgeRequest, SearchKnowledgeResponse>
{
    private const int MinLimit = 1;
    private const int MaxLimit = 50;
    private const int DefaultLimit = 10;

    private readonly IKnowledgeSearchService _searchService;

    public SearchKnowledgeEndpoint(IKnowledgeSearchService searchService)
    {
        ArgumentNullException.ThrowIfNull(searchService);
        _searchService = searchService;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.KnowledgeBase.Search);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(SearchKnowledgeRequest req, CancellationToken ct)
    {
        var validation = KnowledgeQueryLimits.ValidateAndNormalize(req.Query, out var normalizedQuery);
        if (validation == KnowledgeQueryValidation.Empty)
        {
            AddError("A search query is required.");
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        if (validation == KnowledgeQueryValidation.TooLong)
        {
            AddError($"The search query must be {KnowledgeQueryLimits.MaxQueryLength} characters or fewer.");
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        var limit = req.Limit <= 0 ? DefaultLimit : Math.Clamp(req.Limit, MinLimit, MaxLimit);
        if (!KnowledgeCollectionScope.TryNormalize(req.CollectionId, out var collectionId))
        {
            AddError("The collection id is invalid.");
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        var request = new KnowledgeSearchRequest { Query = normalizedQuery, Limit = limit, DocumentId = req.DocumentId, ExpandNeighbors = req.ExpandNeighbors, CollectionId = collectionId };
        var result = await _searchService.SearchAsync(request, ct);

        await Send.OkAsync(new SearchKnowledgeResponse
            {
                Results = [.. result.Results.Select(ToResponse)]
            },
            ct);
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
            ServingLastKnownGood = hit.ServingLastKnownGood,
            CollectionId = hit.CollectionId,
            SourcePath = hit.SourcePath,
            ContentKind = hit.ContentKind,
            Language = hit.Language,
            Symbol = hit.Symbol,
            PageNumber = hit.PageNumber,
            StartOffset = hit.StartOffset,
            EndOffset = hit.EndOffset
        };
    }
}
