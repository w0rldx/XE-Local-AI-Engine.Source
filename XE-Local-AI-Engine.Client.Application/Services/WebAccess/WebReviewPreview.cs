namespace XE_Local_AI_Engine.Client.Services.WebAccess;

/// <summary>What a web consent or result review card shows the user, as plain display data, never fenced model text.</summary>
/// <remarks>
///     A <see cref="RequestStage" /> preview carries only <see cref="Url" /> or <see cref="Query" />, what is about to be
///     sent. A <see cref="ResultStage" /> <c>web_fetch</c> preview fills the page fields, a <c>web_search</c> one
///     <see cref="Backend" /> and <see cref="Results" />. <see cref="Url" /> and <see cref="Query" /> are model-chosen.
/// </remarks>
public sealed class WebReviewPreview
{
    public const string RequestStage = "request";

    public const string ResultStage = "result";

    public required string ToolName { get; init; }

    /// <summary><see cref="RequestStage" /> before the request is sent, <see cref="ResultStage" /> once its result is back.</summary>
    public required string Stage { get; init; }

    public string? Query { get; init; }

    public string? Url { get; init; }

    public string? FinalUrl { get; init; }

    public string? Title { get; init; }

    public string? ContentType { get; init; }

    public bool? Truncated { get; init; }

    public string? Text { get; init; }

    public string? Backend { get; init; }

    public IReadOnlyList<WebSearchResult>? Results { get; init; }
}
