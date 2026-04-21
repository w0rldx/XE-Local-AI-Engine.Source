namespace XE_Local_AI_Engine.Client.Services.Chat;

public interface ILocalChatInvocationService
{
    int AgentDefinitionVersion { get; }

    Guid ConversationId { get; }

    string SelectedModel { get; }

    bool ToolsEnabled { get; }

    ValueTask<LocalChatInvocationSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);

    Task<Guid> SendMessageAsync(string userMessage, CancellationToken cancellationToken = default);

    Task ResetConversationAsync(CancellationToken cancellationToken = default);

    Task SetModelAsync(string modelId, CancellationToken cancellationToken = default);
}
