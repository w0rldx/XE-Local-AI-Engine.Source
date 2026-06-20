namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     FastEndpoints handler for GGUF repo discovery (GET model-fit/gguf/browse). Thin transport over the Hugging Face
///     GGUF discovery seam <see cref="IHuggingFaceGgufDiscovery.SearchAsync" />: a free-text query + breadth + sort maps
///     to candidate GGUF repos (non-GGUF repos are filtered out by the discovery seam). Returns the sanitized repo
///     summaries only — no token, no internal URL. A discovery/network failure surfaces a 200 OK-empty list (never a
///     500) so the browse panel degrades gracefully.
/// </summary>
public sealed class BrowseGgufRepositoriesEndpoint(
    IHuggingFaceGgufDiscovery discovery,
    ILogger<BrowseGgufRepositoriesEndpoint> logger)
    : Endpoint<BrowseGgufRepositoriesRequest, BrowseGgufRepositoriesResponse>
{
    /// <summary>The maximum repos a single browse may return (bounds the discovery search breadth).</summary>
    private const int MaxLimit = 50;

    /// <summary>The default repos returned when no limit is supplied.</summary>
    private const int DefaultLimit = 20;

    private readonly IHuggingFaceGgufDiscovery _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
    private readonly ILogger<BrowseGgufRepositoriesEndpoint> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public override void Configure()
    {
        Get(LocalApiRoutes.ModelFit.GgufBrowse);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(BrowseGgufRepositoriesRequest req, CancellationToken ct)
    {
        var limit = req.Limit is { } requested ? Math.Clamp(requested, 1, MaxLimit) : DefaultLimit;

        var query = new GgufSearchQuery
        {
            SearchText = string.IsNullOrWhiteSpace(req.Query) ? null : req.Query.Trim(),
            Limit = limit,
            Sort = ParseSort(req.Sort)
        };

        try
        {
            var repos = await _discovery.SearchAsync(query, ct).ConfigureAwait(false);
            await Send.OkAsync(new BrowseGgufRepositoriesResponse
                {
                    Items = [.. repos.Select(static repo => repo.ToResponse())]
                },
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or HuggingFaceDownloadException or TimeoutException or InvalidOperationException or OperationCanceledException)
        {
            // A discovery/network failure must not 500 the browse panel — surface an empty list (no raw reason). The
            // OperationCanceledException arm (after the ct-cancellation rethrow above) covers an HttpClient request
            // TIMEOUT (TaskCanceledException, not caller cancellation), which would otherwise escape and 500.
            _logger.LogWarning(exception, "GGUF repo discovery failed for a browse request.");
            await Send.OkAsync(new BrowseGgufRepositoriesResponse
                {
                    Items = []
                },
                ct).ConfigureAwait(false);
        }
    }

    /// <summary>Maps the wire sort string to <see cref="GgufSearchSort" />; an unknown/empty value defaults to downloads.</summary>
    private static GgufSearchSort ParseSort(string? sort)
    {
        // Upper-invariant (CA1308: upper-casing round-trips safely) for case-insensitive matching of the wire tokens.
        return sort?.Trim().ToUpperInvariant() switch
        {
            "LIKES" => GgufSearchSort.Likes,
            "LASTMODIFIED" => GgufSearchSort.LastModified,
            _ => GgufSearchSort.Downloads
        };
    }
}
