namespace XE_Local_AI_Engine.Client.Endpoints.Agents.V1.Mappers;

using XE_Local_AI_Engine.Client.Persistence;

internal static class AgentDefinitionMapper
{
    public static AgentDefinitionResponse ToResponse(this AgentDefinitionRecord record)
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
            AllowedSkillIds = record.AllowedSkillIds ?? [],
            Version = record.Version,
            CreatedAtUtc = record.CreatedAtUtc,
            UpdatedAtUtc = record.UpdatedAtUtc
        };
    }

    public static AgentDefinitionInput ToInput(this CreateAgentDefinitionRequest request)
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
            request.MemoryExtractionEnabled);
    }

    public static AgentDefinitionInput ToInput(this UpdateAgentDefinitionRequest request)
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
            request.MemoryExtractionEnabled);
    }
}
