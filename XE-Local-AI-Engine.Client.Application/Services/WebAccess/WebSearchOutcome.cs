namespace XE_Local_AI_Engine.Client.Services.WebAccess;

/// <summary>
///     What one <c>web_search</c> call produced: the <see cref="Backend" /> and its <see cref="Results" />, or a refusal
///     the model can read.
/// </summary>
/// <remarks>
///     Typed and serialized in ONE later step (<c>WebSearchService.Serialize</c>), as <see cref="WebFetchOutcome" />.
/// </remarks>
internal sealed class WebSearchOutcome
{
    /// <summary>The backend that answered: <c>duckduckgo</c> or <c>searxng</c>; <see langword="null" /> on a refusal before any backend ran.</summary>
    public string? Backend { get; private init; }

    public IReadOnlyList<WebSearchResult>? Results { get; private init; }

    public string? ErrorCode { get; private init; }

    public string? ErrorMessage { get; private init; }

    public static WebSearchOutcome Found(string backend, IReadOnlyList<WebSearchResult> results) =>
        new()
        {
            Backend = backend ?? throw new ArgumentNullException(nameof(backend)),
            Results = results ?? throw new ArgumentNullException(nameof(results))
        };

    public static WebSearchOutcome Refused(string code, string message, string? backend = null) =>
        new()
        {
            Backend = backend,
            ErrorCode = code,
            ErrorMessage = message
        };
}
