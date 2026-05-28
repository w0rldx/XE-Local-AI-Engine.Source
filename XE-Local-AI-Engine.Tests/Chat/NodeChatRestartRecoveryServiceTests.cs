namespace XE_Local_AI_Engine.Tests.Chat;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class NodeChatRestartRecoveryServiceTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, true);
        }
    }

    [Test]
    public async Task RecoverInterruptedMessagesAsync_MarksPendingAndStreamingAssistantMessagesInterrupted()
    {
        await using var provider = await BuildProviderAsync("restart-recovery.sqlite").ConfigureAwait(false);
        var persistence = CreatePersistenceService(provider);
        var recovery = CreateRecoveryService(provider);
        var conversation = await persistence.CreateConversationAsync(new NodeChatCreateConversationRequest("Restart", "node", 10)).ConfigureAwait(false);
        var pendingCorrelation = await CreateAssistantPlaceholderAsync(persistence, conversation.ConversationId, 11).ConfigureAwait(false);
        var streamingCorrelation = await CreateAssistantPlaceholderAsync(persistence, conversation.ConversationId, 12).ConfigureAwait(false);
        var completedCorrelation = await CreateAssistantPlaceholderAsync(persistence, conversation.ConversationId, 13).ConfigureAwait(false);
        var cancelledCorrelation = await CreateAssistantPlaceholderAsync(persistence, conversation.ConversationId, 14).ConfigureAwait(false);
        var failedCorrelation = await CreateAssistantPlaceholderAsync(persistence, conversation.ConversationId, 15).ConfigureAwait(false);
        var interruptedCorrelation = await CreateAssistantPlaceholderAsync(persistence, conversation.ConversationId, 16).ConfigureAwait(false);

        await persistence.MarkAssistantStreamingAsync(streamingCorrelation, 20).ConfigureAwait(false);
        await persistence.FlushAssistantPartialAsync(new NodeChatPartialFlushRequest(streamingCorrelation, "partial answer", "partial reasoning", 21)).ConfigureAwait(false);
        await persistence.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest(completedCorrelation, NodeChatMessageStatusValues.Completed, 22, "done")).ConfigureAwait(false);
        await persistence.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest(cancelledCorrelation, NodeChatMessageStatusValues.Cancelled, 23)).ConfigureAwait(false);
        await persistence.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest(failedCorrelation, NodeChatMessageStatusValues.Failed, 24, Error: "provider failed"))
                         .ConfigureAwait(false);
        await persistence.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest(interruptedCorrelation, NodeChatMessageStatusValues.Interrupted, 25, Error: "already interrupted"))
                         .ConfigureAwait(false);

        var recoveredCount = await recovery.RecoverInterruptedMessagesAsync(99).ConfigureAwait(false);

        var loaded = AssertEx.NotNull(await persistence.GetConversationAsync(conversation.ConversationId).ConfigureAwait(false));
        var pending = loaded.Messages.Single(message => message.MessageId == pendingCorrelation.MessageId);
        var streaming = loaded.Messages.Single(message => message.MessageId == streamingCorrelation.MessageId);
        var completed = loaded.Messages.Single(message => message.MessageId == completedCorrelation.MessageId);
        var cancelled = loaded.Messages.Single(message => message.MessageId == cancelledCorrelation.MessageId);
        var failed = loaded.Messages.Single(message => message.MessageId == failedCorrelation.MessageId);
        var interrupted = loaded.Messages.Single(message => message.MessageId == interruptedCorrelation.MessageId);

        AssertEx.Equal(2, recoveredCount);
        AssertEx.Equal(NodeChatMessageStatusValues.Interrupted, pending.Status);
        AssertEx.Equal(99L, pending.UpdatedAtUtc);
        AssertEx.Equal(NodeChatRestartRecoveryService.RestartInterruptedError, pending.Error);
        AssertEx.Equal(NodeChatMessageStatusValues.Interrupted, streaming.Status);
        AssertEx.Equal("partial answer", streaming.Content);
        AssertEx.Equal("partial reasoning", streaming.Reasoning);
        AssertEx.Equal(99L, streaming.UpdatedAtUtc);
        AssertEx.Equal(NodeChatRestartRecoveryService.RestartInterruptedError, streaming.Error);
        AssertEx.Equal(NodeChatMessageStatusValues.Completed, completed.Status);
        AssertEx.Equal(22L, completed.UpdatedAtUtc);
        AssertEx.Equal(NodeChatMessageStatusValues.Cancelled, cancelled.Status);
        AssertEx.Equal(23L, cancelled.UpdatedAtUtc);
        AssertEx.Equal(NodeChatMessageStatusValues.Failed, failed.Status);
        AssertEx.Equal("provider failed", failed.Error);
        AssertEx.Equal(24L, failed.UpdatedAtUtc);
        AssertEx.Equal(NodeChatMessageStatusValues.Interrupted, interrupted.Status);
        AssertEx.Equal("already interrupted", interrupted.Error);
        AssertEx.Equal(25L, interrupted.UpdatedAtUtc);
    }

    [Test]
    public async Task RecoverInterruptedMessagesAsync_TerminalizesQueuedAndRemoteOriginAssistantMessages()
    {
        await using var provider = await BuildProviderAsync("restart-recovery-remote-queued.sqlite").ConfigureAwait(false);
        var persistence = CreatePersistenceService(provider);
        var recovery = CreateRecoveryService(provider);

        // An Origin=Remote conversation whose assistant placeholder is stuck in `queued` (the state held before
        // the collision lease is acquired) plus a Remote streaming row — both must be terminalized. A Remote
        // completed row must be left alone. Recovery filters by role+status only, so Origin never excludes a row.
        var conversation = await persistence.CreateConversationAsync(new NodeChatCreateConversationRequest("Remote", "node", 40, Origin: NodeChatOriginValues.Remote)).ConfigureAwait(false);
        var queuedCorrelation = await CreateRemoteAssistantPlaceholderAsync(persistence, conversation.ConversationId, 41).ConfigureAwait(false);
        var streamingCorrelation = await CreateRemoteAssistantPlaceholderAsync(persistence, conversation.ConversationId, 42).ConfigureAwait(false);
        var completedCorrelation = await CreateRemoteAssistantPlaceholderAsync(persistence, conversation.ConversationId, 43).ConfigureAwait(false);

        await persistence.MarkAssistantQueuedAsync(queuedCorrelation, 44).ConfigureAwait(false);
        await persistence.MarkAssistantStreamingAsync(streamingCorrelation, 45).ConfigureAwait(false);
        await persistence.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest(completedCorrelation, NodeChatMessageStatusValues.Completed, 46, "done")).ConfigureAwait(false);

        var recoveredCount = await recovery.RecoverInterruptedMessagesAsync(99).ConfigureAwait(false);

        var loaded = AssertEx.NotNull(await persistence.GetConversationAsync(conversation.ConversationId).ConfigureAwait(false));
        var queued = loaded.Messages.Single(message => message.MessageId == queuedCorrelation.MessageId);
        var streaming = loaded.Messages.Single(message => message.MessageId == streamingCorrelation.MessageId);
        var completed = loaded.Messages.Single(message => message.MessageId == completedCorrelation.MessageId);

        AssertEx.Equal(2, recoveredCount);
        AssertEx.Equal(NodeChatMessageStatusValues.Interrupted, queued.Status);
        AssertEx.Equal(NodeChatOriginValues.Remote, queued.Origin);
        AssertEx.Equal(99L, queued.UpdatedAtUtc);
        AssertEx.Equal(NodeChatRestartRecoveryService.RestartInterruptedError, queued.Error);
        AssertEx.Equal(NodeChatMessageStatusValues.Interrupted, streaming.Status);
        AssertEx.Equal(NodeChatOriginValues.Remote, streaming.Origin);
        AssertEx.Equal(99L, streaming.UpdatedAtUtc);
        AssertEx.Equal(NodeChatMessageStatusValues.Completed, completed.Status);
        AssertEx.Equal(46L, completed.UpdatedAtUtc);
    }

    [Test]
    public async Task RecoverInterruptedMessagesAsync_ReturnsZeroWhenNoNonterminalMessagesExist()
    {
        await using var provider = await BuildProviderAsync("restart-recovery-empty.sqlite").ConfigureAwait(false);
        var persistence = CreatePersistenceService(provider);
        var recovery = CreateRecoveryService(provider);
        var conversation = await persistence.CreateConversationAsync(new NodeChatCreateConversationRequest("No recovery", null, 30)).ConfigureAwait(false);
        var userMessage = await persistence.PersistUserMessageAsync(new NodeChatPersistUserMessageRequest(conversation.ConversationId, Guid.NewGuid(), "hello", 31)).ConfigureAwait(false);

        var recoveredCount = await recovery.RecoverInterruptedMessagesAsync(100).ConfigureAwait(false);

        var loaded = AssertEx.NotNull(await persistence.GetConversationAsync(conversation.ConversationId).ConfigureAwait(false));
        var loadedUserMessage = loaded.Messages.Single(message => message.MessageId == userMessage.MessageId);

        AssertEx.Equal(0, recoveredCount);
        AssertEx.Equal(NodeChatMessageStatusValues.Completed, loadedUserMessage.Status);
        AssertEx.Equal(31L, loadedUserMessage.UpdatedAtUtc);
    }

    private async Task<ServiceProvider> BuildProviderAsync(string fileName)
    {
        var databasePath = GetDatabasePath(fileName);
        var services = new ServiceCollection();
        services.AddScoped<INodeSqliteKeyHolder, NullNodeSqliteKeyHolder>();
        services.AddDbContext<NodeChatDbContext>(options => options.UseSqlite($"Data Source={databasePath}"));
        services.AddSingleton<NodeChatPersistenceWriter>();

        var provider = services.BuildServiceProvider(true);
        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
        await dbContext.Database.EnsureDeletedAsync().ConfigureAwait(false);
        await dbContext.Database.EnsureCreatedAsync().ConfigureAwait(false);

        return provider;
    }

    private static NodeChatPersistenceService CreatePersistenceService(ServiceProvider provider)
    {
        return new NodeChatPersistenceService(provider.GetRequiredService<NodeChatPersistenceWriter>());
    }

    private static NodeChatRestartRecoveryService CreateRecoveryService(ServiceProvider provider)
    {
        return new NodeChatRestartRecoveryService(provider.GetRequiredService<NodeChatPersistenceWriter>());
    }

    private static async Task<NodeChatMessageCorrelation> CreateAssistantPlaceholderAsync(NodeChatPersistenceService persistence,
        Guid conversationId,
        long createdAtUtc)
    {
        var messageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        await persistence.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(conversationId, messageId, requestId, createdAtUtc)).ConfigureAwait(false);
        return new NodeChatMessageCorrelation(conversationId, messageId, requestId);
    }

    private static async Task<NodeChatMessageCorrelation> CreateRemoteAssistantPlaceholderAsync(NodeChatPersistenceService persistence,
        Guid conversationId,
        long createdAtUtc)
    {
        var messageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        await persistence.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(conversationId,
            messageId,
            requestId,
            createdAtUtc,
            Origin: NodeChatOriginValues.Remote)).ConfigureAwait(false);
        return new NodeChatMessageCorrelation(conversationId, messageId, requestId);
    }

    private string GetDatabasePath(string fileName)
    {
        Directory.CreateDirectory(_rootPath);
        return Path.Combine(_rootPath, fileName);
    }
}
