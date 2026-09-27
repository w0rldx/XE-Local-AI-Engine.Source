namespace XE_Local_AI_Engine.Client.Services.WebAccess;

/// <summary>What a web result review card shows the user: the retrieved content as plain display data, never fenced model text.</summary>
/// <remarks>
///     A <c>web_fetch</c> preview fills the page fields; a <c>web_search</c> preview fills <see cref="Backend" /> and
///     <see cref="Results" />. Every value except <see cref="ToolName" /> and <see cref="Url" /> is server-controlled.
/// </remarks>
public sealed class WebReviewPreview
{
    public required string ToolName { get; init; }

    public string? Url { get; init; }

    public string? FinalUrl { get; init; }

    public string? Title { get; init; }

    public string? ContentType { get; init; }

    public bool? Truncated { get; init; }

    public string? Text { get; init; }

    public string? Backend { get; init; }

    public IReadOnlyList<WebSearchResult>? Results { get; init; }
}
