namespace XE_Local_AI_Engine.Client.Services.WebAccess;

/// <summary>A fetched page, already extracted and truncated but not yet fenced or serialized.</summary>
internal sealed class WebFetchPage
{
    /// <summary>The URL the model asked for.</summary>
    public required string Url { get; init; }

    /// <summary>The URL the content came from after redirects; server-controlled.</summary>
    public required string FinalUrl { get; init; }

    /// <summary>The page title; server-controlled, <see langword="null" /> when the page has none.</summary>
    public string? Title { get; init; }

    /// <summary>The allow-listed media type, in canonical lower case.</summary>
    public required string ContentType { get; init; }

    /// <summary>The extracted readable text, at most the node's <c>WebFetchMaxContentChars</c> characters.</summary>
    public required string Text { get; init; }

    public required bool Truncated { get; init; }
}
