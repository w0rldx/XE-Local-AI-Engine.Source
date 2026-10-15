namespace XE_Local_AI_Engine.Client.Services.GraphWorkflows;

/// <summary>Whether an intent started a workflow. <see cref="NotMatched" /> starts nothing; the caller falls through to chat.</summary>
public enum GraphWorkflowIntentStatus
{
    Started,
    NotMatched
}

/// <summary>What chose the workflow: an exact alias or name, the router graph, or nothing.</summary>
public enum GraphWorkflowIntentMatch
{
    None,
    Alias,
    Router
}

/// <summary>One spoken or typed command, confirmed by the user (Enter) when it was sent.</summary>
public sealed class GraphWorkflowIntentRequest
{
    public required string Utterance { get; init; }

    /// <summary>The caller-minted idempotency key: a retry answers the same run.</summary>
    public required Guid RequestId { get; init; }

    /// <summary>When the user said or typed it; older than <c>IntentMaxUtteranceAgeSeconds</c> is refused as a replay.</summary>
    public required DateTimeOffset UtteredAt { get; init; }

    /// <summary>Audit metadata recorded on the run; never authority.</summary>
    public Guid? PrincipalId { get; init; }
}

public sealed class GraphWorkflowIntentResult
{
    public required GraphWorkflowIntentStatus Status { get; init; }

    public required GraphWorkflowIntentMatch MatchedBy { get; init; }

    public Guid? DefinitionId { get; init; }

    public string? DefinitionName { get; init; }

    public GraphWorkflowRunDetail? Run { get; init; }

    /// <summary>Why nothing started, in structural terms; never the router model's own text.</summary>
    public string? Reason { get; init; }
}

/// <summary>An utterance older than the node's window. Maps to 409 <c>GraphWorkflowIntentExpired</c>: say it again.</summary>
public sealed class GraphWorkflowIntentExpiredException : InvalidOperationException
{
    public GraphWorkflowIntentExpiredException(string message) : base(message)
    {
    }
}
