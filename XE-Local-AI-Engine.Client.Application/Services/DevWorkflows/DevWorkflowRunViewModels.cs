namespace XE_Local_AI_Engine.Client.Services.DevWorkflows;

using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     A run as the run view draws it: the run row, every node run with what the server derives for it, and the
///     run-wide counts and spend. Built by <see cref="DevWorkflowRunQueryService.GetRunViewAsync" />.
/// </summary>
public sealed record DevWorkflowRunView
{
    public required DevWorkflowRunSnapshot Run { get; init; }

    public required string? DefinitionName { get; init; }

    public required IReadOnlyList<DevWorkflowNodeRunView> Nodes { get; init; }

    public required int QueuedNodeCount { get; init; }

    public required int RunningNodeCount { get; init; }

    public required int PendingDecisionCount { get; init; }

    public required Guid? BlockingGateNodeRunId { get; init; }

    public required DevWorkflowRunCost Cost { get; init; }
}

/// <summary>One node run's row in <see cref="DevWorkflowRunView" />, with the fields derived for it.</summary>
public sealed record DevWorkflowNodeRunView
{
    public required DevWorkflowNodeRunSnapshot NodeRun { get; init; }

    public required IReadOnlyList<string>? WaitingOnNodeKeys { get; init; }

    public required string? MaterializedFromNodeKey { get; init; }

    public required int? MaterializationCount { get; init; }

    public required string? AgentDisplayName { get; init; }

    public required string? ModelLabel { get; init; }

    public required bool HasStaleInputs { get; init; }

    public required int OperatorRetries { get; init; }

    public required bool? SkipWaived { get; init; }

    public required bool ValidationNotApplicable { get; init; }
}

/// <summary>
///     The run's spend, summed member by member over its node runs. A member stays null until some row reports it.
/// </summary>
public sealed record DevWorkflowRunCost
{
    public required long? InputTokens { get; init; }

    public required long? OutputTokens { get; init; }

    public required int? ToolCalls { get; init; }

    public required int? ProviderCalls { get; init; }

    public required long? AgentTurnMs { get; init; }
}

/// <summary>One node run's drill-down. Built by <see cref="DevWorkflowRunQueryService.GetNodeRunViewAsync" />.</summary>
public sealed record DevWorkflowNodeRunDetailView
{
    public required DevWorkflowRunSnapshot Run { get; init; }

    public required DevWorkflowNodeRunSnapshot NodeRun { get; init; }

    public required Guid? ConversationId { get; init; }

    public required string? AgentDisplayName { get; init; }

    public required string? ModelLabel { get; init; }

    public required Guid? PrimaryArtifactId { get; init; }

    public required IReadOnlyList<Guid> ProducedArtifactIds { get; init; }

    public required IReadOnlyList<Guid> ConsumedArtifactIds { get; init; }

    public required IReadOnlyList<DevWorkflowAppliedRuleSetView> AppliedRuleSets { get; init; }

    public required IReadOnlyList<string> AllowedDecisions { get; init; }

    public required bool HasRejectBranch { get; init; }

    public required IReadOnlyList<DevWorkflowDecisionSnapshot> Decisions { get; init; }

    public required int OperatorRetries { get; init; }
}

/// <summary>A rule set as recorded at materialization, beside the hash of its CURRENT text (null once deleted).</summary>
public sealed record DevWorkflowAppliedRuleSetView
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required string ContentSha256 { get; init; }

    public required string? CurrentContentSha256 { get; init; }
}
