namespace XE_Local_AI_Engine.Client.Endpoints.Agents.V1.Mappers;

using XE_Local_AI_Engine.Client.Persistence.Stores;

internal static class RunEnvelopeMapper
{
    public static AgentRunEnvelopeResponse ToResponse(this AgentRunEnvelopeRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        // Pass-through of metadata-only fields. FailureCategory is already a category enum name by the store contract —
        // never an exception message — so no further redaction is needed; the projection just forwards it.
        return new AgentRunEnvelopeResponse
        {
            Id = record.Id,
            SchemaVersion = record.SchemaVersion,
            AgentDefinitionId = record.AgentDefinitionId,
            ConversationId = record.ConversationId,
            MessageId = record.MessageId,
            InvocationId = record.InvocationId,
            RequestId = record.RequestId,
            ModelName = record.ModelName,
            TerminalStatus = record.TerminalStatus,
            Success = record.Success,
            FailureCategory = record.FailureCategory,
            DurationMs = record.DurationMs,
            PromptTokens = record.PromptTokens,
            CompletionTokens = record.CompletionTokens,
            ReasoningTokens = record.ReasoningTokens,
            TotalTokens = record.TotalTokens,
            ToolSchemaTokens = record.ToolSchemaTokens,
            MaxToolSchemaTokens = record.MaxToolSchemaTokens,
            DispatchedTier = record.DispatchedTier,
            AuthoredEffort = record.AuthoredEffort,
            ModelReadinessMs = record.ModelReadinessMs,
            ContentChunkCount = record.ContentChunkCount,
            ReasoningChunkCount = record.ReasoningChunkCount,
            TraceId = record.TraceId,
            StartedAtUtc = record.StartedAtUtc,
            CreatedAtUtc = record.CreatedAtUtc
        };
    }
}
