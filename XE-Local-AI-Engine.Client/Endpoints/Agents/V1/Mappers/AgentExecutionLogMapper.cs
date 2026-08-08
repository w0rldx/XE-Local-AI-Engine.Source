namespace XE_Local_AI_Engine.Client.Endpoints.Agents.V1.Mappers;

using XE_Local_AI_Engine.Client.Persistence.Stores;

internal static class AgentExecutionLogMapper
{
    public static AgentExecutionLogResponse ToResponse(this AgentExecutionLogRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        // Pass-through of metadata-only fields. ErrorClass is already an exception type name by the store contract —
        // never the exception message — so no further redaction is needed; the projection just forwards it.
        return new AgentExecutionLogResponse
        {
            Id = record.Id,
            AgentDefinitionId = record.AgentDefinitionId,
            ConversationId = record.ConversationId,
            MessageId = record.MessageId,
            ModelName = record.ModelName,
            ConfigHash = record.ConfigHash,
            LatencyMs = record.LatencyMs,
            PromptTokens = record.PromptTokens,
            CompletionTokens = record.CompletionTokens,
            Success = record.Success,
            ErrorClass = record.ErrorClass,
            CreatedAtUtc = record.CreatedAtUtc
        };
    }
}
