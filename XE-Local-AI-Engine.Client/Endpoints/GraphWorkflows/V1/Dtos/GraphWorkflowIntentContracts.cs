namespace XE_Local_AI_Engine.Client.Endpoints.GraphWorkflows.V1;

/// <summary>A command the user confirmed with Enter: the text, its idempotency key, and when it was said.</summary>
public sealed class InvokeGraphWorkflowIntentRequest
{
    public string Utterance { get; init; } = string.Empty;

    /// <summary>The caller's idempotency key: a retry answers the same run.</summary>
    public Guid RequestId { get; init; }

    /// <summary>ISO 8601 with an offset. Older than the node's <c>intentMaxUtteranceAgeSeconds</c> answers 409.</summary>
    public DateTimeOffset UtteredAt { get; init; }
}

/// <summary>
///     <c>status</c> is <c>Started</c> (with the run) or <c>NotMatched</c> (with a structural <c>reason</c>);
///     <c>matchedBy</c> names the stage that decided: <c>Alias</c>, <c>Router</c> or <c>None</c>.
/// </summary>
public sealed class GraphWorkflowIntentResponse
{
    public required string Status { get; init; }

    public required string MatchedBy { get; init; }

    public required Guid? DefinitionId { get; init; }

    public required string? DefinitionName { get; init; }

    public required string? Reason { get; init; }

    public required GraphWorkflowRunResponse? Run { get; init; }
}
