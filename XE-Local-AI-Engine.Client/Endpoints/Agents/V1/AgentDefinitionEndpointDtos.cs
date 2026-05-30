namespace XE_Local_AI_Engine.Client.Endpoints.Agents.V1;

using XE_Local_AI_Engine.Client.Persistence;

/// <summary>Create request for an agent definition. The editable fields mirror <see cref="AgentDefinitionInput" />.</summary>
public sealed class CreateAgentDefinitionRequest
{
    public string? Name { get; init; }

    public string? Description { get; init; }

    public string? Instructions { get; init; }

    public string? ModelProfile { get; init; }

    public string? ReasoningEffort { get; init; }

    public AgentDefinitionKind Kind { get; init; } = AgentDefinitionKind.Single;

    public IReadOnlyList<string>? AllowedToolNames { get; init; }

    public IReadOnlyDictionary<string, bool>? ToolApprovals { get; init; }

    public string? OrchestrationTopologyJson { get; init; }
}

/// <summary>Update request for an agent definition. The id travels in the route; the body carries the new field values.</summary>
public sealed class UpdateAgentDefinitionRequest
{
    public Guid AgentDefinitionId { get; init; }

    public string? Name { get; init; }

    public string? Description { get; init; }

    public string? Instructions { get; init; }

    public string? ModelProfile { get; init; }

    public string? ReasoningEffort { get; init; }

    public AgentDefinitionKind Kind { get; init; } = AgentDefinitionKind.Single;

    public IReadOnlyList<string>? AllowedToolNames { get; init; }

    public IReadOnlyDictionary<string, bool>? ToolApprovals { get; init; }

    public string? OrchestrationTopologyJson { get; init; }
}

public sealed class GetAgentDefinitionRequest
{
    public Guid AgentDefinitionId { get; init; }
}

public sealed class DeleteAgentDefinitionRequest
{
    public Guid AgentDefinitionId { get; init; }
}

/// <summary>
///     Wire projection of a stored definition. <see cref="Kind" /> serializes as the string "Single"/"Orchestrator"
///     via the globally registered <c>JsonStringEnumConverter</c>; the remaining fields serialize camelCase.
/// </summary>
public sealed class AgentDefinitionResponse
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    public required string Instructions { get; init; }

    public string? ModelProfile { get; init; }

    public string? ReasoningEffort { get; init; }

    public required AgentDefinitionKind Kind { get; init; }

    public required IReadOnlyList<string> AllowedToolNames { get; init; }

    public required IReadOnlyDictionary<string, bool> ToolApprovals { get; init; }

    public string? OrchestrationTopologyJson { get; init; }

    public required int Version { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required long UpdatedAtUtc { get; init; }
}

public sealed class ListAgentDefinitionsResponse
{
    public required IReadOnlyList<AgentDefinitionResponse> Items { get; init; }
}

/// <summary>The node's tool-capable model ids — the FE's source for the model-tool-capability warning.</summary>
public sealed class ToolCapableModelsResponse
{
    public required IReadOnlyList<string> Models { get; init; }
}
