namespace XE_Local_AI_Engine.Client.Services.WebAccess;

/// <summary>
///     What one <c>web_fetch</c> call produced: a <see cref="Page" />, or a refusal the model can read.
/// </summary>
/// <remarks>
///     Typed on purpose and serialized in ONE later step (<c>WebFetchService.Serialize</c>), so nothing the page supplied
///     reaches model context before whatever sits between the fetch and that step has seen it.
/// </remarks>
internal sealed class WebFetchOutcome
{
    public WebFetchPage? Page { get; private init; }

    public string? ErrorCode { get; private init; }

    public string? ErrorMessage { get; private init; }

    public static WebFetchOutcome Fetched(WebFetchPage page) =>
        new()
        {
            Page = page ?? throw new ArgumentNullException(nameof(page))
        };

    public static WebFetchOutcome Refused(string code, string message) =>
        new()
        {
            ErrorCode = code,
            ErrorMessage = message
        };
}
