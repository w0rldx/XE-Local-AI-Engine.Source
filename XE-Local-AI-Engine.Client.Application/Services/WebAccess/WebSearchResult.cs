namespace XE_Local_AI_Engine.Client.Services.WebAccess;

/// <summary>One search hit; every field is backend-controlled, so all three travel inside the fence.</summary>
public sealed class WebSearchResult
{
    public required string Title { get; init; }

    /// <summary>An absolute http(s) URL, already unwrapped from any search-engine redirect.</summary>
    public required string Url { get; init; }

    /// <summary>The snippet text, at most <c>WebSearchService.MaxSnippetChars</c> characters; empty when the backend sent none.</summary>
    public required string Snippet { get; init; }
}
