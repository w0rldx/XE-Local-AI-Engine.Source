namespace XE_Local_AI_Engine.Client.Endpoints.Images.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.Abstractions.Image;

/// <summary>
///     FastEndpoints handler for image-model repo discovery (GET images/models/browse). Thin transport over
///     <see cref="IImageModelDiscovery.SearchAsync" />: a free-text query + breadth + sort maps to candidate
///     text-to-image repos. Returns sanitized summaries only — no token, no internal URL. A discovery/network failure
///     surfaces a 200 OK with an empty list (never a 500) so the browse panel degrades gracefully, following the
///     precedent set by the GGUF browse/inspect endpoints.
/// </summary>
public sealed class BrowseImageRepositoriesEndpoint(
    IImageModelDiscovery discovery,
    ILogger<BrowseImageRepositoriesEndpoint> logger)
    : Endpoint<BrowseImageRepositoriesRequest, BrowseImageRepositoriesResponse>
{
    /// <summary>The maximum repos a single browse may return (bounds the discovery search breadth).</summary>
    private const int MaxLimit = 50;

    /// <summary>The default repos returned when no limit is supplied.</summary>
    private const int DefaultLimit = 20;

    private readonly IImageModelDiscovery _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
    private readonly ILogger<BrowseImageRepositoriesEndpoint> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public override void Configure()
    {
        Get(LocalApiRoutes.Images.ModelBrowse);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(BrowseImageRepositoriesRequest req, CancellationToken ct)
    {
        var query = new ImageModelSearchQuery
        {
            SearchText = string.IsNullOrWhiteSpace(req.Query) ? null : req.Query.Trim(),
            Limit = req.Limit is { } requested ? Math.Clamp(requested, min: 1, MaxLimit) : DefaultLimit,
            Sort = ParseSort(req.Sort),
            GgufOnly = req.GgufOnly ?? false
        };

        try
        {
            var repos = await _discovery.SearchAsync(query, ct).ConfigureAwait(false);
            await Send.OkAsync(new BrowseImageRepositoriesResponse
                {
                    Items = [.. repos.Select(ToResponse)]
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
            _logger.LogWarning(exception, "Image model repo discovery failed for a browse request.");
            await Send.OkAsync(new BrowseImageRepositoriesResponse
                {
                    Items = []
                },
                ct).ConfigureAwait(false);
        }
    }

    private static ImageRepositoryResponse ToResponse(ImageRepoSummary summary)
    {
        return new ImageRepositoryResponse
        {
            RepoId = summary.RepoId,
            IsGated = summary.IsGated,
            Downloads = summary.Downloads,
            Likes = summary.Likes,
            LastModifiedAtUtc = summary.LastModified.ToUnixTimeMilliseconds(),
            License = summary.License,
            HasUsableWeights = summary.HasUsableWeights,
            IsTrustedPublisher = summary.IsTrustedPublisher
        };
    }

    /// <summary>Maps the wire sort string to <see cref="ImageModelSearchSort" />; an unknown/empty value is trending.</summary>
    private static ImageModelSearchSort ParseSort(string? sort)
    {
        // Upper-invariant (CA1308: upper-casing round-trips safely) for case-insensitive matching of the wire tokens.
        return sort?.Trim().ToUpperInvariant() switch
        {
            "DOWNLOADS" => ImageModelSearchSort.Downloads,
            "LIKES" => ImageModelSearchSort.Likes,
            "LASTMODIFIED" => ImageModelSearchSort.LastModified,
            _ => ImageModelSearchSort.Trending
        };
    }
}
