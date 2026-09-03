namespace XE_Local_AI_Engine.Client.Endpoints.Agents.V1.Mappers;

using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;

internal static class AgentDefinitionMapper
{
    /// <summary>
    ///     Projects a definition onto the wire. Agents have no separate list DTO, so
    ///     <paramref name="includeGenerationMetadata" /> is what keeps the list lean: the list endpoint passes
    ///     <c>false</c>, every single-item read leaves the default.
    /// </summary>
    public static AgentDefinitionResponse ToResponse(this AgentDefinitionRecord record, bool includeGenerationMetadata = true)
    {
        ArgumentNullException.ThrowIfNull(record);

        return new AgentDefinitionResponse
        {
            Id = record.Id,
            Name = record.Name,
            Description = record.Description,
            Instructions = record.Instructions,
            ModelProfile = record.ModelProfile,
            ReasoningEffort = record.ReasoningEffort,
            Kind = record.Kind,
            AllowedToolNames = record.AllowedToolNames,
            ToolApprovals = record.ToolApprovals,
            OrchestrationTopologyJson = record.OrchestrationTopologyJson,
            PlaybookEnabled = record.PlaybookEnabled,
            DefaultTemporaryChat = record.DefaultTemporaryChat,
            MemoryExtractionEnabled = record.MemoryExtractionEnabled,
            DisableBaseScaffold = record.DisableBaseScaffold,
            DisableToolRelevanceFilter = record.DisableToolRelevanceFilter,
            AllowedSkillIds = record.AllowedSkillIds ?? [],
            Version = record.Version,
            CreatedAtUtc = record.CreatedAtUtc,
            UpdatedAtUtc = record.UpdatedAtUtc,
            GenerationMetadata = includeGenerationMetadata
                ? GenerationProvenance.FromPersistedJson(record.GenerationMetadataJson)
                : null
        };
    }

    public static AgentDefinitionInput ToInput(this CreateAgentDefinitionRequest request, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new AgentDefinitionInput(request.Name ?? string.Empty,
            request.Description,
            request.Instructions ?? string.Empty,
            request.ModelProfile,
            request.ReasoningEffort,
            request.Kind,
            request.AllowedToolNames ?? [],
            request.ToolApprovals ?? new Dictionary<string, bool>(StringComparer.Ordinal),
            request.OrchestrationTopologyJson,
            request.PlaybookEnabled,
            request.AllowedSkillIds ?? [],
            request.DefaultTemporaryChat,
            request.MemoryExtractionEnabled,
            request.DisableBaseScaffold,
            GenerationProvenance.ToPersistedJson(request.GenerationMetadata, request.Name, request.Description, request.Instructions, now),
            request.DisableToolRelevanceFilter);
    }

    public static AgentDefinitionInput ToInput(this UpdateAgentDefinitionRequest request, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new AgentDefinitionInput(request.Name ?? string.Empty,
            request.Description,
            request.Instructions ?? string.Empty,
            request.ModelProfile,
            request.ReasoningEffort,
            request.Kind,
            request.AllowedToolNames ?? [],
            request.ToolApprovals ?? new Dictionary<string, bool>(StringComparer.Ordinal),
            request.OrchestrationTopologyJson,
            request.PlaybookEnabled,
            request.AllowedSkillIds ?? [],
            request.DefaultTemporaryChat,
            request.MemoryExtractionEnabled,
            request.DisableBaseScaffold,
            GenerationProvenance.ToPersistedJson(request.GenerationMetadata, request.Name, request.Description, request.Instructions, now),
            request.DisableToolRelevanceFilter);
    }
}
