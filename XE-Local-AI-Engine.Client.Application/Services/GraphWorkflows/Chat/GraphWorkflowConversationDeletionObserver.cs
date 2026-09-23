namespace XE_Local_AI_Engine.Client.Services.GraphWorkflows.Chat;

using XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>
///     Cancels a conversation's live bound run before the conversation is deleted: a run parked on a <c>ChatInput</c> has
///     nobody left to answer it. A singleton over a fresh scope, because the chat service it drives is scoped.
/// </summary>
internal sealed class GraphWorkflowConversationDeletionObserver : IConversationDeletionObserver
{
    private readonly IServiceScopeFactory _scopeFactory;

    public GraphWorkflowConversationDeletionObserver(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    }

    public async Task OnDeletingAsync(Guid conversationId, bool purge, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IGraphWorkflowChatService>().CancelBoundRunAsync(conversationId, cancellationToken);
    }
}
