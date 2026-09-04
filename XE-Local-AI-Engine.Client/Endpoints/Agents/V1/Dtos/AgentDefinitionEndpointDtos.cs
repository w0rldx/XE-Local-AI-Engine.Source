namespace XE_Local_AI_Engine.Client.Endpoints.Agents.V1;

using XE_Local_AI_Engine.Client.Endpoints.Common;
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

    public bool PlaybookEnabled { get; init; }

    /// <summary>
    ///     Per-agent default for the temporary-chat (memory-excluded) flag new conversations inherit (adaptive memory).
    ///     Additive and non-config-affecting — like <see cref="PlaybookEnabled" />, it never enters the runtime config hash.
    /// </summary>
    public bool DefaultTemporaryChat { get; init; }

    /// <summary>
    ///     Whether this agent mines its completed runs into new candidate memories (adaptive memory). Defaults to
    ///     <c>true</c>; set <c>false</c> for a retrieval-only agent that uses existing memory but learns nothing new.
    ///     Additive and non-config-affecting — like <see cref="PlaybookEnabled" />, it never enters the runtime config hash.
    /// </summary>
    public bool MemoryExtractionEnabled { get; init; } = true;

    /// <summary>
    ///     Opts this definition OUT of the versioned base instruction scaffold normally prepended ahead of
    ///     <see cref="Instructions" /> when composing the resolved prompt. Defaults to <c>false</c> (scaffold ON).
    ///     Additive; changing it is NOT config-affecting for this definition's own version — the resulting prompt
    ///     change already drives the runtime config hash directly.
    /// </summary>
    public bool DisableBaseScaffold { get; init; }

    /// <summary>
    ///     Whether this agent opts out of the node's send-time tool-relevance filter, so every offered tool is shown to
    ///     the model on every round. Additive; NOT config-affecting — the filter narrows only the array handed to the
    ///     provider, never the offer or the resolved prompt, so toggling it leaves the runtime config hash unmoved.
    /// </summary>
    public bool DisableToolRelevanceFilter { get; init; }

    /// <summary>The per-agent skill picklist — skill ids (Guids) selected into this agent for MAF progressive disclosure.</summary>
    public IReadOnlyList<Guid>? AllowedSkillIds { get; init; }

    /// <summary>The draft response's provenance block, echoed back unchanged. Optional; informational (see the type).</summary>
    public GenerationMetadata? GenerationMetadata { get; init; }
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

    public bool PlaybookEnabled { get; init; }

    /// <summary>
    ///     Per-agent default for the temporary-chat (memory-excluded) flag new conversations inherit (adaptive memory).
    ///     Additive and non-config-affecting — like <see cref="PlaybookEnabled" />, it never enters the runtime config hash.
    /// </summary>
    public bool DefaultTemporaryChat { get; init; }

    /// <summary>
    ///     Whether this agent mines its completed runs into new candidate memories (adaptive memory). Defaults to
    ///     <c>true</c>; set <c>false</c> for a retrieval-only agent that uses existing memory but learns nothing new.
    ///     Additive and non-config-affecting — like <see cref="PlaybookEnabled" />, it never enters the runtime config hash.
    /// </summary>
    public bool MemoryExtractionEnabled { get; init; } = true;

    /// <summary>
    ///     Opts this definition OUT of the versioned base instruction scaffold normally prepended ahead of
    ///     <see cref="Instructions" /> when composing the resolved prompt. Defaults to <c>false</c> (scaffold ON).
    ///     Additive; changing it is NOT config-affecting for this definition's own version — the resulting prompt
    ///     change already drives the runtime config hash directly.
    /// </summary>
    public bool DisableBaseScaffold { get; init; }

    /// <summary>
    ///     Whether this agent opts out of the node's send-time tool-relevance filter, so every offered tool is shown to
    ///     the model on every round. Additive; NOT config-affecting — the filter narrows only the array handed to the
    ///     provider, never the offer or the resolved prompt, so toggling it leaves the runtime config hash unmoved.
    /// </summary>
    public bool DisableToolRelevanceFilter { get; init; }

    /// <summary>The per-agent skill picklist — skill ids (Guids) selected into this agent for MAF progressive disclosure.</summary>
    public IReadOnlyList<Guid>? AllowedSkillIds { get; init; }

    /// <summary>
    ///     The draft response's provenance block, echoed back unchanged. Optional, and <b>set-if-present</b>: omitting
    ///     it leaves any stored provenance alone rather than clearing it, so an ordinary edit cannot erase the record of
    ///     how the definition was originally drafted.
    /// </summary>
    public GenerationMetadata? GenerationMetadata { get; init; }
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

    public required bool PlaybookEnabled { get; init; }

    /// <summary>Per-agent default for the temporary-chat (memory-excluded) flag new conversations inherit (adaptive memory).</summary>
    public required bool DefaultTemporaryChat { get; init; }

    /// <summary>Whether this agent mines its completed runs into new candidate memories; false = retrieval-only (adaptive memory).</summary>
    public required bool MemoryExtractionEnabled { get; init; }

    /// <summary>Whether this definition opts out of the versioned base instruction scaffold. False (the default) means the scaffold is prepended.</summary>
    public required bool DisableBaseScaffold { get; init; }

    /// <summary>Whether this definition opts out of the node's send-time tool-relevance filter. False (the default) means it follows the node setting.</summary>
    public required bool DisableToolRelevanceFilter { get; init; }

    /// <summary>The per-agent skill picklist (skill ids). Always present; empty when no skills are assigned.</summary>
    public required IReadOnlyList<Guid> AllowedSkillIds { get; init; }

    public required int Version { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required long UpdatedAtUtc { get; init; }

    /// <summary>
    ///     AI-drafting provenance when this definition came from a draft, otherwise null. Agents share one response
    ///     type between the single-item and list surfaces, so this is populated by the single-item reads (get, create,
    ///     update) and left null by <see cref="ListAgentDefinitionsResponse" /> — the list would otherwise carry a
    ///     rationale and brief per row for no reader.
    /// </summary>
    public GenerationMetadataResponse? GenerationMetadata { get; init; }
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
