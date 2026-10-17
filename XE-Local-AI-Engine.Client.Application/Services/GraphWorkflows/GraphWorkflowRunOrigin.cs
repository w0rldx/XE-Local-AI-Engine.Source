namespace XE_Local_AI_Engine.Client.Services.GraphWorkflows;

using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>Who starts a run, recorded on the run and checked against the definition's accepted sources.</summary>
/// <remarks>
///     <see cref="PrincipalId" /> and <see cref="KeyPrefix" /> are audit metadata for a run an integration key started; they
///     never authorise anything (ADR 0021). The two shared instances cover the callers that carry no principal.
/// </remarks>
public sealed class GraphWorkflowRunOrigin
{
    /// <summary>The operator, from the Graph Workflows page.</summary>
    public static GraphWorkflowRunOrigin Manual { get; } = new()
    {
        Source = GraphWorkflowTriggerSource.Manual
    };

    /// <summary>A chat conversation.</summary>
    public static GraphWorkflowRunOrigin Chat { get; } = new()
    {
        Source = GraphWorkflowTriggerSource.Chat
    };

    /// <summary>Exactly one member of <see cref="GraphWorkflowTriggerSource" />.</summary>
    public required GraphWorkflowTriggerSource Source { get; init; }

    public Guid? PrincipalId { get; init; }

    public string? KeyPrefix { get; init; }
}
