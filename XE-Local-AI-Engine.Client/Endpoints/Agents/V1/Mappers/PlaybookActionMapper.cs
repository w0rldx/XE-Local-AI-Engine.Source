namespace XE_Local_AI_Engine.Client.Endpoints.Agents.V1.Mappers;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence;

internal static class PlaybookActionMapper
{
    // Web defaults so the persisted camelCase EvalResult JSON binds to the positional response record (CA1869).
    private static readonly JsonSerializerOptions EvalResultSerializerOptions = new(JsonSerializerDefaults.Web);

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
            UpdatedAtUtc = record.UpdatedAtUtc,
            SourceFeedbackIds = record.SourceFeedbackIds,
            Confidence = record.Confidence,
            EvalResult = ToEvalResultResponse(record.EvalResult)
        };
    }

    private static PlaybookEvalResultResponse? ToEvalResultResponse(string? evalResultJson)
    {
        if (string.IsNullOrWhiteSpace(evalResultJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<PlaybookEvalResultResponse>(evalResultJson, EvalResultSerializerOptions);
        }
        catch (JsonException)
        {
            // A malformed eval column must not 500 the list/eval endpoint — degrade to "no eval recorded".
            return null;
        }
    }

    public static PlaybookActionInput ToInput(this CreatePlaybookActionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Manual authoring pins Source = Manual: provenance is never client-supplied. Analysis-sourced actions use the dedicated review routes.
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

        // Manual authoring pins Source = Manual: provenance is never client-supplied. Analysis-sourced actions use the dedicated review routes.
        return new PlaybookActionInput(request.AgentDefinitionId,
            request.State,
            PlaybookActionSource.Manual,
            request.TriggerCondition,
            request.Behavior ?? string.Empty,
            request.Scope,
            request.Priority);
    }
}
