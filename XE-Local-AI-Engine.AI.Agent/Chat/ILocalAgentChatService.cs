namespace XE_Local_AI_Engine.AI.Agent.Chat;

public interface ILocalAgentChatService : IAsyncDisposable
{
    string SelectedModel { get; }

    Task SetModelAsync(string modelId, CancellationToken cancellationToken = default);

    IAsyncEnumerable<string> SendMessageAsync(string userMessage, CancellationToken cancellationToken = default);

    Task ResetSessionAsync(CancellationToken cancellationToken = default);
}
