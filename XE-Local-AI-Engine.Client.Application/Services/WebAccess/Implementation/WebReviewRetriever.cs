namespace XE_Local_AI_Engine.Client.Services.WebAccess.Implementation;

using System.Text.Json;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.Client.Services.CustomTools;

/// <summary>Runs one chat web call for the result review gate: validates the model's arguments, then fetches or searches.</summary>
/// <remarks>
///     Public with an internal constructor only because <c>ToolApprovalCoordinator</c>'s public constructor takes it; the
///     composition root builds it with a factory. The chat path fetches with no link allow-list (open fetch).
/// </remarks>
public sealed class WebReviewRetriever
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly WebFetchService _fetchService;
    private readonly ILogger<WebReviewRetriever> _logger;
    private readonly WebSearchService _searchService;

    internal WebReviewRetriever(WebFetchService fetchService, WebSearchService searchService, ILogger<WebReviewRetriever> logger)
    {
        _fetchService = fetchService ?? throw new ArgumentNullException(nameof(fetchService));
        _searchService = searchService ?? throw new ArgumentNullException(nameof(searchService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>What <see cref="RetrieveAsync" /> would send (normalised URL or trimmed query); null when it would refuse before sending.</summary>
    internal static WebReviewPreview? DescribeRequest(FunctionCallContent call)
    {
        ArgumentNullException.ThrowIfNull(call);

        try
        {
            var arguments = SerializeArguments(call);
            if (IsSearch(call))
            {
                var query = arguments.Deserialize<WebSearchToolRequest>(SerializerOptions)?.Query;
                return string.IsNullOrWhiteSpace(query) ? null : RequestPreview(call.Name, url: null, query.Trim());
            }

            if (!WebFetchService.TryParseRequestUrl(arguments.Deserialize<WebFetchToolRequest>(SerializerOptions)?.Url, out var requested))
            {
                return null;
            }

            WebFetchService.ValidatePublicUrl(requested);
            return RequestPreview(call.Name, requested.AbsoluteUri, query: null);
        }
        catch (Exception exception) when (exception is JsonException or CustomToolExecutionException)
        {
            return null;
        }
    }

    internal async Task<WebReviewRetrieval> RetrieveAsync(FunctionCallContent call, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);

        JsonElement arguments;
        try
        {
            arguments = SerializeArguments(call);
        }
        catch (JsonException exception)
        {
            return WebReviewRetrieval.Refusal("invalid-arguments", $"{call.Name} arguments were not valid JSON: {exception.Message}");
        }

        try
        {
            if (IsSearch(call))
            {
                var search = arguments.Deserialize<WebSearchToolRequest>(SerializerOptions);
                return ToRetrieval(await _searchService.SearchAsync(search?.Query, search?.MaxResults, cancellationToken));
            }

            var fetch = arguments.Deserialize<WebFetchToolRequest>(SerializerOptions);
            return ToRetrieval(await _fetchService.FetchAsync(fetch?.Url, allowedUrls: null, cancellationToken));
        }
        catch (JsonException exception)
        {
            return WebReviewRetrieval.Refusal("invalid-arguments", $"{call.Name} arguments did not match its schema: {exception.Message}");
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Whatever the services did not map is still one failed web call, never a faulted turn: the other calls of the
            // segment keep their reviews. The body is never logged, only the exception type.
            _logger.LogDebug("{ToolName} failed unexpectedly: {ExceptionType}.", call.Name, exception.GetType().Name);
            return WebReviewRetrieval.Refusal("request-failed", $"{call.Name} failed before a result arrived.");
        }
    }

    private static JsonElement SerializeArguments(FunctionCallContent call) =>
        JsonSerializer.SerializeToElement(call.Arguments ?? new Dictionary<string, object?>(StringComparer.Ordinal), SerializerOptions);

    private static bool IsSearch(FunctionCallContent call) =>
        string.Equals(call.Name, WebSearchToolDefinition.ToolName, StringComparison.Ordinal);

    private static WebReviewPreview RequestPreview(string toolName, string? url, string? query) =>
        new()
        {
            ToolName = toolName,
            Stage = WebReviewPreview.RequestStage,
            Url = url,
            Query = query
        };

    private static WebReviewRetrieval ToRetrieval(WebFetchOutcome outcome) =>
        new()
        {
            ModelText = WebFetchService.Serialize(outcome),
            Preview = outcome.Page is { } page
                ? new WebReviewPreview
                {
                    ToolName = WebFetchToolDefinition.ToolName,
                    Stage = WebReviewPreview.ResultStage,
                    Url = page.Url,
                    FinalUrl = page.FinalUrl,
                    Title = page.Title,
                    ContentType = page.ContentType,
                    Truncated = page.Truncated,
                    Text = page.Text
                }
                : null
        };

    // An empty hit list carries no web content, so it needs no review either.
    private static WebReviewRetrieval ToRetrieval(WebSearchOutcome outcome) =>
        new()
        {
            ModelText = WebSearchService.Serialize(outcome),
            Preview = outcome.Results is { Count: > 0 } results
                ? new WebReviewPreview
                {
                    ToolName = WebSearchToolDefinition.ToolName,
                    Stage = WebReviewPreview.ResultStage,
                    Backend = outcome.Backend,
                    Results = results
                }
                : null
        };
}
