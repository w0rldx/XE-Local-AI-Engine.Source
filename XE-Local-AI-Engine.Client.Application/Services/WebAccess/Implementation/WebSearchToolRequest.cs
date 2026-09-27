namespace XE_Local_AI_Engine.Client.Services.WebAccess.Implementation;

/// <summary>The <c>web_search</c> tool arguments, as the model sends them.</summary>
internal sealed class WebSearchToolRequest
{
    public string? Query { get; init; }

    public int? MaxResults { get; init; }
}
