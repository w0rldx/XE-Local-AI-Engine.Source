namespace XE_Local_AI_Engine.Client.Services.Knowledge.Tools.Implementation;

using System.Text.Json;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Tools;

/// <summary>
///     <see cref="IClientLocalToolHandler" /> for <c>search_knowledge_base</c> (ClientLocal). Despite the
///     <c>ClientLocal</c> location label, this executes ENTIRELY on the node inside the agent's function-invocation
///     pipeline — JSON-in / JSON-out, no client round-trip. It resolves the scoped <see cref="IKnowledgeSearchService" />
///     from a FRESH DI scope per call (the handler is a Singleton captured by the tool registry, so it cannot hold a
///     scoped dependency directly). Read-only, so it auto-runs (<c>RequiresApproval => false</c>); gated by
///     <c>KnowledgeBase:AgentToolsEnabled</c>.
/// </summary>
internal sealed class SearchKnowledgeBaseToolHandler : IClientLocalToolHandler
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private const int DefaultLimit = 5;
    private const int MinLimit = 1;
    private const int MaxLimit = 20;

    /// <summary>
    ///     Upper bound on the total hit-content characters serialized into one response, so a wide search (up to
    ///     <see cref="MaxLimit" /> neighbor-expanded hits) cannot dump an unbounded payload into the model context.
    ///     Mirrors <c>ReadDocumentToolHandler.MaxContentChars</c>; truncation is flagged in the payload.
    /// </summary>
    private const int MaxContentChars = 50_000;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly bool _toolsEnabled;

    public SearchKnowledgeBaseToolHandler(IServiceScopeFactory scopeFactory, IOptions<KnowledgeBaseOptions> options)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        ArgumentNullException.ThrowIfNull(options);
        _toolsEnabled = options.Value.AgentToolsEnabled;
    }

    public string ToolName => SearchKnowledgeBaseToolDefinition.ToolName;

    public string Description => SearchKnowledgeBaseToolDefinition.Description;

    public string ParameterSchema => SearchKnowledgeBaseToolDefinition.ParameterSchema;

    public bool RequiresApproval => false;

    public async Task<string> ExecuteAsync(string jsonArguments, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jsonArguments);

        if (!_toolsEnabled)
        {
            return "The knowledge-base tools are disabled on this node (KnowledgeBase:AgentToolsEnabled=false).";
        }

        cancellationToken.ThrowIfCancellationRequested();

        SearchKnowledgeBaseToolRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<SearchKnowledgeBaseToolRequest>(jsonArguments, SerializerOptions);
        }
        catch (JsonException exception)
        {
            return $"search_knowledge_base arguments were not valid JSON: {exception.Message}";
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Query))
        {
            return "search_knowledge_base requires a non-empty 'query'.";
        }

        Guid? documentId = null;
        if (!string.IsNullOrWhiteSpace(request.DocumentId))
        {
            if (!Guid.TryParse(request.DocumentId, out var parsed))
            {
                return "search_knowledge_base 'documentId' is not a valid document identifier.";
            }

            documentId = parsed;
        }

        var limit = request.Limit is { } requested ? Math.Clamp(requested, MinLimit, MaxLimit) : DefaultLimit;
        var searchRequest = new KnowledgeSearchRequest(request.Query, limit, documentId, request.ExpandNeighbors ?? false);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var searchService = scope.ServiceProvider.GetRequiredService<IKnowledgeSearchService>();
        var result = await searchService.SearchAsync(searchRequest, cancellationToken).ConfigureAwait(false);

        if (result.Results.Count == 0)
        {
            return JsonSerializer.Serialize(new
                {
                    results = Array.Empty<object>(),
                    note = "The knowledge base has no information matching this query."
                },
                SerializerOptions);
        }

        // Results arrive ordered by descending fused score. Serialize hits while a running content-character budget
        // holds, so the lowest-scored (least relevant) hits are trimmed first and one wide search cannot flood context.
        var hits = new List<object>(result.Results.Count);
        var usedChars = 0;
        var truncated = false;
        foreach (var hit in result.Results)
        {
            if (hits.Count > 0 && usedChars + hit.Content.Length > MaxContentChars)
            {
                truncated = true;
                break;
            }

            usedChars += hit.Content.Length;
            hits.Add(new
            {
                documentId = hit.DocumentId,
                chunkId = hit.ChunkId,
                title = hit.Title,
                section = hit.Section,
                content = hit.Content,
                source = hit.Source,
                score = hit.Score,
                chunkIndex = hit.ChunkIndex,
                // Disclose staleness in the model-facing provenance: when the owning document is not currently
                // Indexed, this chunk is a last-known-good projection served during a pending/failed re-index.
                documentStatus = hit.DocumentStatus.ToString(),
                servingLastKnownGood = hit.ServingLastKnownGood
            });
        }

        var payload = new
        {
            results = hits,
            returnedResults = hits.Count,
            totalResults = result.Results.Count,
            truncated
        };

        return JsonSerializer.Serialize(payload, SerializerOptions);
    }
}
