namespace XE_Local_AI_Engine.Client.Services.Connection;

using XE_Local_AI_Engine.Client.Models.Events;

public sealed class ConversationPurgedReceivedEventArgs : EventArgs
{
    public ConversationPurgedReceivedEventArgs(ConversationPurgedEvent conversationPurged)
    {
        ConversationPurged = conversationPurged ?? throw new ArgumentNullException(nameof(conversationPurged));
    }

    public ConversationPurgedEvent ConversationPurged { get; }
}
