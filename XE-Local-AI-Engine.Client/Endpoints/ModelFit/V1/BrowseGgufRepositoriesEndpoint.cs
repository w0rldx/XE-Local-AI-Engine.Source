namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

/// <summary>
///     FastEndpoints handler for GGUF repo discovery (GET model-fit/gguf/browse): thin transport over the Hugging Face
///     discovery seam <see cref="IHuggingFaceGgufDiscovery.SearchAsync" />, returning sanitized repo summaries only.
/// </summary>
/// <remarks>
///     A free-text query plus breadth and sort maps to candidate GGUF repos; the discovery seam filters out non-GGUF
///     repos. No token and no internal URL are returned, and a discovery/network failure surfaces a 200 OK-empty list
///     (never a 500) so the browse panel degrades gracefully.
/// </remarks>
public sealed class BrowseGgufRepositoriesEndpoint : Endpoint<BrowseGgufRepositoriesRequest, BrowseGgufRepositoriesResponse>
{
    /// <summary>The maximum repos a single browse may return (bounds the discovery search breadth).</summary>
    private const int MaxLimit = 50;

    /// <summary>The default repos returned when no limit is supplied.</summary>
    private const int DefaultLimit = 20;

    private readonly IHuggingFaceGgufDiscovery _discovery;
    private readonly ILogger<BrowseGgufRepositoriesEndpoint> _logger;

    public BrowseGgufRepositoriesEndpoint(IHuggingFaceGgufDiscovery discovery,
        ILogger<BrowseGgufRepositoriesEndpoint> logger)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(logger);
        _discovery = discovery;
        _logger = logger;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.ModelFit.GgufBrowse);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(BrowseGgufRepositoriesRequest req, CancellationToken ct)
    {
        var limit = req.Limit is { } requested ? Math.Clamp(requested, min: 1, MaxLimit) : DefaultLimit;

        var query = new GgufSearchQuery
        {
            SearchText = string.IsNullOrWhiteSpace(req.Query) ? null : req.Query.Trim(),
            Limit = limit,
            Sort = ParseSort(req.Sort)
        };

        try
        {
            var repos = await _discovery.SearchAsync(query, ct);
            await Send.OkAsync(new BrowseGgufRepositoriesResponse
                {
                    Items = [.. repos.Select(static repo => repo.ToResponse())]
                },
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or HuggingFaceDownloadException or TimeoutException or InvalidOperationException or OperationCanceledException)
        {
            // A discovery/network failure must not 500 the browse panel — surface an empty list with no raw reason.
            // The OperationCanceledException arm covers an HttpClient request TIMEOUT (TaskCanceledException, not the caller cancellation rethrown above), which would otherwise escape and 500.
            _logger.LogWarning(exception, "GGUF repo discovery failed for a browse request.");
            await Send.OkAsync(new BrowseGgufRepositoriesResponse
                {
                    Items = []
                },
                ct);
        }
    }

    /// <summary>Maps the wire sort string to <see cref="GgufSearchSort" />; an unknown/empty value defaults to trending.</summary>
    private static GgufSearchSort ParseSort(string? sort)
    {
        // Upper-invariant (CA1308: upper-casing round-trips safely) for case-insensitive matching of the wire tokens.
        return sort?.Trim().ToUpperInvariant() switch
        {
            "DOWNLOADS" => GgufSearchSort.Downloads,
            "LIKES" => GgufSearchSort.Likes,
            "LASTMODIFIED" => GgufSearchSort.LastModified,
            _ => GgufSearchSort.Trending
        };
    }
}
