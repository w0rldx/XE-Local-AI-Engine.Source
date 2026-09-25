namespace XE_Local_AI_Engine.Client.Endpoints.LocalChat.V1.Mappers;

using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Compaction.State;

internal static class ConversationContextStateMapper
{
    public static NodeChatConversationContextStateResponse ToContextStateResponse(this NodeChatConversationDto conversation)
    {
        ArgumentNullException.ThrowIfNull(conversation);

        var document = ConversationStateSerializer.Deserialize(conversation.ConversationState) ?? new ConversationStateDocument();

        return new NodeChatConversationContextStateResponse
        {
            Entries = document.Entries.Select(static entry => new NodeChatConversationContextStateEntryResponse
                              {
                                  Id = entry.Id,
                                  Category = entry.Category.ToString(),
                                  Value = entry.Value,
                                  SourceSequences = entry.SourceSequences,
                                  SupersededById = entry.SupersededById,
                                  RetiredAtSequence = entry.RetiredAtSequence,
                                  CreatedAtSequence = entry.CreatedAtSequence,
                                  IsLive = entry.IsLive
                              })
                              .ToArray(),
            StateCoversToSequence = conversation.ConversationStateCoversToSequence,
            StateUpdatedAtUtc = conversation.ConversationStateUpdatedAtUtc,
            Synopsis = conversation.CompactionSummary,
            SynopsisCoversToSequence = conversation.CompactionSummaryCoversToSequence,
            // A cleared synopsis keeps its stamp (the compare-and-set token), so the stamp is only reported beside a synopsis.
            SynopsisUpdatedAtUtc = conversation.CompactionSummary is null ? null : conversation.CompactionSummaryUpdatedAtUtc,
            NextEntryNumber = document.NextEntryNumber
        };
    }
}
