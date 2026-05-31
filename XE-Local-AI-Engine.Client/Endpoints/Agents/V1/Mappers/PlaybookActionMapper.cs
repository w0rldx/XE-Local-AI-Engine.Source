namespace XE_Local_AI_Engine.Client.Endpoints.Agents.V1.Mappers;

using XE_Local_AI_Engine.Client.Persistence;

internal static class PlaybookActionMapper
{
    public static PlaybookActionResponse ToResponse(this PlaybookActionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        return new PlaybookActionResponse
        {
            Id = record.Id,
            AgentDefinitionId = record.AgentDefinitionId,
            State = record.State,
            Source = record.Source,
            TriggerCondition = record.TriggerCondition,
            Behavior = record.Behavior,
            Scope = record.Scope,
            Priority = record.Priority,
            Version = record.Version,
            CreatedAtUtc = record.CreatedAtUtc,
            UpdatedAtUtc = record.UpdatedAtUtc
        };
    }

    public static PlaybookActionInput ToInput(this CreatePlaybookActionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // P1 pins Source = Manual: provenance is never client-supplied. Analysis is reserved for the deferred phase.
        return new PlaybookActionInput(request.AgentDefinitionId,
            request.State,
            PlaybookActionSource.Manual,
            request.TriggerCondition,
            request.Behavior ?? string.Empty,
            request.Scope,
            request.Priority);
    }

    public static PlaybookActionInput ToInput(this UpdatePlaybookActionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // P1 pins Source = Manual: provenance is never client-supplied. Analysis is reserved for the deferred phase.
        return new PlaybookActionInput(request.AgentDefinitionId,
            request.State,
            PlaybookActionSource.Manual,
            request.TriggerCondition,
            request.Behavior ?? string.Empty,
            request.Scope,
            request.Priority);
    }
}
