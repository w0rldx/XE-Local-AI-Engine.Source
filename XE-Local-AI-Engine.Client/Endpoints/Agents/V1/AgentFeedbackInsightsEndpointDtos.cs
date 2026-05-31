namespace XE_Local_AI_Engine.Client.Endpoints.Agents.V1;

/// <summary>Request for one agent's read-only feedback insights. The agent id travels in the route.</summary>
public sealed class GetAgentFeedbackInsightsRequest
{
    public Guid AgentDefinitionId { get; init; }
}

/// <summary>
///     Wire projection of the per-agent feedback aggregate (Playbook P2, read-only analytics). All fields serialize
///     camelCase; <see cref="Exemplars" /> are node-local, capped and truncated by the application service.
/// </summary>
public sealed class AgentFeedbackInsightsResponse
{
    public required Guid AgentDefinitionId { get; init; }

    public required string AgentName { get; init; }

    public required long GeneratedAtUtc { get; init; }

    public required int MinOccurrenceThreshold { get; init; }

    public required OverallFeedbackResponse Overall { get; init; }

    public required IReadOnlyList<ToolFeedbackResponse> ByTool { get; init; }

    public required IReadOnlyList<FeedbackExemplarResponse> Exemplars { get; init; }
}

public sealed class OverallFeedbackResponse
{
    public required int Total { get; init; }

    public required int Up { get; init; }

    public required int Down { get; init; }

    public required double DownRate { get; init; }

    public required bool MeetsThreshold { get; init; }
}

public sealed class ToolFeedbackResponse
{
    public required string ToolName { get; init; }

    public required int Total { get; init; }

    public required int Up { get; init; }

    public required int Down { get; init; }

    public required double DownRate { get; init; }

    public required bool MeetsThreshold { get; init; }
}

public sealed class FeedbackExemplarResponse
{
    public required string Rating { get; init; }

    public required string Comment { get; init; }

    public required Guid MessageId { get; init; }

    public required Guid ConversationId { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required bool Truncated { get; init; }
}
