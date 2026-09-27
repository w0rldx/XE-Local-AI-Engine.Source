namespace XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1.Mappers;

using XE_Local_AI_Engine.Client.Common.Telemetry;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;

/// <summary>
///     Maps the run view and the node-run drill-down that <see cref="DevWorkflowRunQueryService" /> composes onto the
///     wire. Every derived field arrives already derived; this only re-shapes, and labels each node off the run's pinned
///     wire graph.
/// </summary>
internal static class DevWorkflowRunResponseMapper
{
    public static DevWorkflowRunResponse ToResponse(this DevWorkflowRunView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        var run = view.Run;
        var graph = DevWorkflowContractMapper.ToWireGraph(run.GraphJson);
        var nodesByKey = graph.Nodes.ToDictionary(static node => node.NodeKey, StringComparer.Ordinal);
        return new DevWorkflowRunResponse
        {
            Id = run.Id,
            WorkItemId = run.WorkItemId,
            DefinitionId = run.DefinitionId,
            DefinitionVersion = run.DefinitionVersion,
            DefinitionName = view.DefinitionName,
            GraphRevision = run.GraphRevision,
            Graph = graph,
            Status = run.Status.ToString(),
            Nodes = [.. view.Nodes.Select(node => ToSummary(node, nodesByKey.GetValueOrDefault(node.NodeRun.NodeKey)))],
            QueuedNodeCount = view.QueuedNodeCount,
            RunningNodeCount = view.RunningNodeCount,
            PendingDecisionCount = view.PendingDecisionCount,
            BlockingGateNodeRunId = view.BlockingGateNodeRunId,
            FailureClass = run.FailureClass,
            TerminalReason = run.TerminalReason,
            StartedAtUtc = run.StartedAtUtc,
            CompletedAtUtc = run.EndedAtUtc,
            Version = run.Version,
            LastSequence = run.LastSequence,
            Cost = new DevWorkflowRunCostResponse
            {
                InputTokens = view.Cost.InputTokens,
                OutputTokens = view.Cost.OutputTokens,
                ToolCalls = view.Cost.ToolCalls,
                ProviderCalls = view.Cost.ProviderCalls,
                AgentTurnMs = view.Cost.AgentTurnMs
            }
        };
    }

    public static DevWorkflowNodeRunDetailResponse ToResponse(this DevWorkflowNodeRunDetailView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        var nodeRun = view.NodeRun;
        var node = DevWorkflowContractMapper.ToWireGraph(view.Run.GraphJson)
                                            .Nodes.FirstOrDefault(entry => string.Equals(entry.NodeKey, nodeRun.NodeKey, StringComparison.Ordinal));
        return new DevWorkflowNodeRunDetailResponse
        {
            Id = nodeRun.Id,
            RunId = nodeRun.RunId,
            NodeKey = nodeRun.NodeKey,
            NodeType = nodeRun.NodeType.ToString(),
            Label = node?.Label ?? nodeRun.NodeKey,
            Status = nodeRun.Status.ToString(),
            Attempt = nodeRun.Attempt,
            MaxAttempts = nodeRun.MaxAttempts,
            SessionResumes = nodeRun.SessionResumes,
            QueueReason = nodeRun.QueueReason,
            QueuedAtUtc = nodeRun.QueuedAtUtc,
            AgentDefinitionId = nodeRun.AgentDefinitionId,
            AgentDisplayName = view.AgentDisplayName,
            ModelLabel = view.ModelLabel,
            WorkSessionId = nodeRun.WorkSessionId,
            ConversationId = view.ConversationId,
            WorkSessionAvailable = nodeRun.WorkSessionAvailable,
            DevelopmentProjectId = nodeRun.DevelopmentProjectId,
            DevelopmentTaskId = nodeRun.DevelopmentTaskId,
            PrimaryArtifactId = view.PrimaryArtifactId,
            Instructions = node?.Instructions,
            InputJson = nodeRun.InputJson,
            OutputJson = nodeRun.OutputJson,
            ProducedArtifactIds = view.ProducedArtifactIds,
            ConsumedArtifactIds = view.ConsumedArtifactIds,
            AppliedRuleSets =
            [
                .. view.AppliedRuleSets.Select(static applied => new DevWorkflowAppliedRuleSetResponse
                {
                    Id = applied.Id,
                    Name = applied.Name,
                    ContentSha256 = applied.ContentSha256,
                    CurrentContentSha256 = applied.CurrentContentSha256
                })
            ],
            PendingDecisionKind = nodeRun.PendingDecisionKind?.ToString(),
            AllowedDecisions = view.AllowedDecisions,
            HasRejectBranch = view.HasRejectBranch,
            FailureClass = nodeRun.FailureClass,
            TerminalReason = nodeRun.TerminalReason,
            Decisions = [.. view.Decisions.Select(DevWorkflowContractMapper.ToResponse)],
            OperatorRetries = view.OperatorRetries,
            StartedAtUtc = nodeRun.StartedAtUtc,
            CompletedAtUtc = nodeRun.EndedAtUtc,
            Sequence = nodeRun.Sequence,
            // Named from here on: the tail is a run of same-typed optional slots, so a positional call would compile
            // silently misaligned if a field is spliced in ahead of them.
            InputTokens = nodeRun.InputTokens,
            OutputTokens = nodeRun.OutputTokens,
            ReasoningTokens = nodeRun.ReasoningTokens,
            EstimatedInputTokens = nodeRun.EstimatedInputTokens,
            ProviderCalls = nodeRun.ProviderCalls,
            ToolCalls = nodeRun.ToolCalls,
            ToolSchemaTokens = nodeRun.ToolSchemaTokens,
            ToolNames = DevWorkflowNodeRunDocuments.ToolNames(nodeRun.ToolNamesJson),
            AgentTurnMs = nodeRun.AgentTurnMs,
            ServedModelName = nodeRun.ServedModelName,
            Route = Route(nodeRun.RouteJson),
            WorkSessionSteps = nodeRun.WorkSessionSteps,
            FailureClassGroup = AgentUnitFailureClass.FromDevWorkflowFailureClass(nodeRun.FailureClass),
            ModelReadinessMs = nodeRun.ModelReadinessMs,
            VramFreeAtLoadBytes = nodeRun.VramFreeAtLoadBytes,
            VramAdmittedBytes = nodeRun.VramAdmittedBytes
        };
    }

    private static DevWorkflowNodeRunSummaryResponse ToSummary(DevWorkflowNodeRunView view, DevWorkflowGraphNode? node)
    {
        var nodeRun = view.NodeRun;
        return new DevWorkflowNodeRunSummaryResponse
        {
            Id = nodeRun.Id,
            NodeKey = nodeRun.NodeKey,
            NodeType = nodeRun.NodeType.ToString(),
            Label = node?.Label ?? nodeRun.NodeKey,
            Status = nodeRun.Status.ToString(),
            Attempt = nodeRun.Attempt,
            MaxAttempts = nodeRun.MaxAttempts,
            QueueReason = nodeRun.QueueReason,
            QueuedAtUtc = nodeRun.QueuedAtUtc,
            WaitingOnNodeKeys = view.WaitingOnNodeKeys,
            PendingDecisionKind = nodeRun.PendingDecisionKind?.ToString(),
            IsMaterialized = nodeRun.MaterializedFromNodeRunId is not null,
            MaterializedFromNodeKey = view.MaterializedFromNodeKey,
            MaterializationIndex = nodeRun.MaterializationIndex,
            MaterializationGroupId = nodeRun.MaterializedFromNodeRunId,
            MaterializationCount = view.MaterializationCount,
            DevelopmentProjectId = nodeRun.DevelopmentProjectId,
            DevelopmentTaskId = nodeRun.DevelopmentTaskId,
            AgentDefinitionId = nodeRun.AgentDefinitionId,
            AgentDisplayName = view.AgentDisplayName,
            ModelLabel = view.ModelLabel,
            HasStaleInputs = view.HasStaleInputs,
            StartedAtUtc = nodeRun.StartedAtUtc,
            CompletedAtUtc = nodeRun.EndedAtUtc,
            Sequence = nodeRun.Sequence,
            OperatorRetries = view.OperatorRetries,
            SkipWaived = view.SkipWaived,
            InputTokens = nodeRun.InputTokens,
            OutputTokens = nodeRun.OutputTokens,
            ToolCalls = nodeRun.ToolCalls,
            ValidationNotApplicable = view.ValidationNotApplicable
        };
    }

    /// <summary>
    ///     The stored route on the wire, parsed by the runtime's own reader — the one that owns the document — and only
    ///     re-shaped here.
    /// </summary>
    /// <remarks>
    ///     An unreadable column costs this node its route rather than costing the drill-down a 500, exactly as an
    ///     unreadable policy resolution does.
    /// </remarks>
    private static DevWorkflowNodeRouteResponse? Route(string? routeJson) =>
        DevWorkflowNodeRunDocuments.TryParseRoute(routeJson) is { } route
            ? new DevWorkflowNodeRouteResponse
            {
                Satisfied = route.Satisfied,
                Dead = route.Dead,
                Waived = route.Waived,
                GateAnswer = route.GateAnswer,
                Truncated = route.Truncated
            }
            : null;
}
