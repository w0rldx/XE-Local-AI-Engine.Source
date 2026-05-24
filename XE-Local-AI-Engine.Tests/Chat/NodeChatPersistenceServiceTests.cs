namespace XE_Local_AI_Engine.Tests.Chat;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class NodeChatPersistenceServiceTests : IDisposable
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
    public async Task Messages_FollowAcceptedLifecycleAndPartialPersistence()
    {
        await using var provider = await BuildProviderAsync("lifecycle.sqlite").ConfigureAwait(false);
        var service = CreateService(provider);
        var conversation = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Local chat", "node", CreatedAtUtc: 10)).ConfigureAwait(false);
        var userMessageId = Guid.NewGuid();
        var assistantMessageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var correlation = new NodeChatMessageCorrelation(conversation.ConversationId, assistantMessageId, requestId);

        var user = await service.PersistUserMessageAsync(new NodeChatPersistUserMessageRequest(conversation.ConversationId, userMessageId, " hello ", CreatedAtUtc: 11)).ConfigureAwait(false);
        var placeholder = await service.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(conversation.ConversationId, assistantMessageId, requestId, CreatedAtUtc: 12, Model: "llama")).ConfigureAwait(false);
        var streaming = await service.MarkAssistantStreamingAsync(correlation, updatedAtUtc: 13).ConfigureAwait(false);
        var partial = await service.FlushAssistantPartialAsync(new NodeChatPartialFlushRequest(correlation, "Hello", Reasoning: "thinking", UpdatedAtUtc: 14)).ConfigureAwait(false);
        var appended = await service.FlushAssistantPartialAsync(new NodeChatPartialFlushRequest(correlation, " world", Reasoning: null, UpdatedAtUtc: 15, ReplaceContent: false)).ConfigureAwait(false);
        var completed = await service.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest(correlation, NodeChatMessageStatusValues.Completed, UpdatedAtUtc: 16, Content: appended.Content, Model: "llama")).ConfigureAwait(false);

        AssertEx.Equal(NodeChatMessageStatusValues.Completed, user.Status);
        AssertEx.Equal("hello", user.Content);
        AssertEx.Equal(NodeChatMessageStatusValues.Pending, placeholder.Status);
        AssertEx.Equal(requestId, placeholder.RequestId);
        AssertEx.Equal(NodeChatMessageStatusValues.Streaming, streaming.Status);
        AssertEx.Equal("Hello", partial.Content);
        AssertEx.Equal("thinking", partial.Reasoning);
        AssertEx.Equal("Hello world", appended.Content);
        AssertEx.Equal(NodeChatMessageStatusValues.Completed, completed.Status);
        AssertEx.Equal(16L, completed.UpdatedAtUtc);

        var loaded = await service.GetConversationAsync(conversation.ConversationId).ConfigureAwait(false);
        var messages = AssertEx.NotNull(loaded).Messages;
        AssertEx.Equal(2, messages.Count);
        AssertEx.Equal(userMessageId, messages[0].MessageId);
        AssertEx.Equal(assistantMessageId, messages[1].MessageId);
        AssertEx.Equal("Hello world", messages[1].Content);
    }

    [Test]
    public async Task CancelMessageAsync_TerminalizesOnlyMatchingCorrelation()
    {
        await using var provider = await BuildProviderAsync("cancel.sqlite").ConfigureAwait(false);
        var service = CreateService(provider);
        var conversation = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Cancel", null, CreatedAtUtc: 20)).ConfigureAwait(false);
        var targetMessageId = Guid.NewGuid();
        var otherMessageId = Guid.NewGuid();
        var targetCorrelation = new NodeChatMessageCorrelation(conversation.ConversationId, targetMessageId, Guid.NewGuid());
        var otherCorrelation = new NodeChatMessageCorrelation(conversation.ConversationId, otherMessageId, Guid.NewGuid());

        await service.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(conversation.ConversationId, targetMessageId, targetCorrelation.RequestId, CreatedAtUtc: 21)).ConfigureAwait(false);
        await service.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(conversation.ConversationId, otherMessageId, otherCorrelation.RequestId, CreatedAtUtc: 22)).ConfigureAwait(false);

        var cancel = await service.CancelMessageAsync(new NodeChatCancelRequest(targetCorrelation, CancelledAtUtc: 23)).ConfigureAwait(false);

        AssertEx.True(cancel.Cancelled);
        AssertEx.Equal(NodeChatMessageStatusValues.Cancelled, cancel.Status);

        var loaded = AssertEx.NotNull(await service.GetConversationAsync(conversation.ConversationId).ConfigureAwait(false));
        AssertEx.Equal(NodeChatMessageStatusValues.Cancelled, loaded.Messages.Single(message => message.MessageId == targetMessageId).Status);
        AssertEx.Equal(NodeChatMessageStatusValues.Pending, loaded.Messages.Single(message => message.MessageId == otherMessageId).Status);
    }

    [Test]
    public async Task TerminalizeAssistantMessageAsync_WhenFailed_PersistsPartialContentAndRedactedError()
    {
        await using var provider = await BuildProviderAsync("failed-terminal.sqlite").ConfigureAwait(false);
        var service = CreateService(provider);
        var conversation = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Failure", null, CreatedAtUtc: 24)).ConfigureAwait(false);
        var assistantMessageId = Guid.NewGuid();
        var correlation = new NodeChatMessageCorrelation(conversation.ConversationId, assistantMessageId, Guid.NewGuid());

        await service.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(conversation.ConversationId, assistantMessageId, correlation.RequestId, CreatedAtUtc: 25)).ConfigureAwait(false);
        await service.MarkAssistantStreamingAsync(correlation, updatedAtUtc: 26).ConfigureAwait(false);
        await service.FlushAssistantPartialAsync(new NodeChatPartialFlushRequest(correlation, "partial answer", Reasoning: "partial reasoning", UpdatedAtUtc: 27)).ConfigureAwait(false);

        var failed = await service.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest(
            correlation,
            NodeChatMessageStatusValues.Failed,
            UpdatedAtUtc: 28,
            Content: "partial answer",
            Reasoning: "partial reasoning",
            Error: "local-chat-stream-failed")).ConfigureAwait(false);

        var loaded = AssertEx.NotNull(await service.GetConversationAsync(conversation.ConversationId).ConfigureAwait(false));
        var loadedAssistant = loaded.Messages.Single(message => message.MessageId == assistantMessageId);
        AssertEx.Equal(NodeChatMessageStatusValues.Failed, failed.Status);
        AssertEx.Equal("partial answer", loadedAssistant.Content);
        AssertEx.Equal("partial reasoning", loadedAssistant.Reasoning);
        AssertEx.Equal("local-chat-stream-failed", loadedAssistant.Error);
        AssertEx.Equal(28L, loadedAssistant.UpdatedAtUtc);
    }

    [Test]
    public async Task ListAndGet_ExcludePurgedConversationsAndOrderMessagesBySequence()
    {
        await using var provider = await BuildProviderAsync("list.sqlite").ConfigureAwait(false);
        var service = CreateService(provider);
        var keep = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Keep", null, CreatedAtUtc: 30)).ConfigureAwait(false);
        var purge = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Purge", null, CreatedAtUtc: 31)).ConfigureAwait(false);

        var second = await service.PersistUserMessageAsync(new NodeChatPersistUserMessageRequest(keep.ConversationId, Guid.NewGuid(), "second visible", CreatedAtUtc: 33)).ConfigureAwait(false);
        var first = await service.PersistUserMessageAsync(new NodeChatPersistUserMessageRequest(keep.ConversationId, Guid.NewGuid(), "first visible", CreatedAtUtc: 32)).ConfigureAwait(false);
        await service.DeleteConversationAsync(new NodeChatDeleteConversationRequest(purge.ConversationId, DeletedAtUtc: 34)).ConfigureAwait(false);

        var summaries = await service.ListConversationsAsync(new NodeChatListConversationsRequest()).ConfigureAwait(false);
        var loaded = AssertEx.NotNull(await service.GetConversationAsync(keep.ConversationId).ConfigureAwait(false));
        var purged = await service.GetConversationAsync(purge.ConversationId).ConfigureAwait(false);

        AssertEx.Contains(summaries.Select(summary => summary.ConversationId), keep.ConversationId);
        AssertEx.False(summaries.Any(summary => summary.ConversationId == purge.ConversationId));
        AssertEx.Null(purged);
        AssertEx.Equal(second.MessageId, loaded.Messages[0].MessageId);
        AssertEx.Equal(first.MessageId, loaded.Messages[1].MessageId);
    }

    [Test]
    public async Task DeleteConversationAsync_CancelsActiveMessagesBeforeHidingConversation()
    {
        await using var provider = await BuildProviderAsync("delete.sqlite").ConfigureAwait(false);
        var service = CreateService(provider);
        var conversation = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Delete", null, CreatedAtUtc: 40)).ConfigureAwait(false);

        await service.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(conversation.ConversationId, Guid.NewGuid(), Guid.NewGuid(), CreatedAtUtc: 41)).ConfigureAwait(false);

        var result = await service.DeleteConversationAsync(new NodeChatDeleteConversationRequest(conversation.ConversationId, DeletedAtUtc: 42)).ConfigureAwait(false);
        var loaded = await service.GetConversationAsync(conversation.ConversationId).ConfigureAwait(false);

        AssertEx.True(result.CancelRequested);
        AssertEx.False(result.Purged);
        AssertEx.Null(loaded);
    }

    private async Task<ServiceProvider> BuildProviderAsync(string fileName)
    {
        var databasePath = GetDatabasePath(fileName);
        var services = new ServiceCollection();
        services.AddScoped<INodeSqliteKeyHolder, NullNodeSqliteKeyHolder>();
        services.AddDbContext<NodeChatDbContext>(options => options.UseSqlite($"Data Source={databasePath}"));
        services.AddSingleton<NodeChatPersistenceWriter>();

        var provider = services.BuildServiceProvider(validateScopes: true);
        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
        await dbContext.Database.EnsureDeletedAsync().ConfigureAwait(false);
        await dbContext.Database.EnsureCreatedAsync().ConfigureAwait(false);

        return provider;
    }

    private static NodeChatPersistenceService CreateService(ServiceProvider provider)
    {
        return new NodeChatPersistenceService(provider.GetRequiredService<NodeChatPersistenceWriter>());
    }

    private string GetDatabasePath(string fileName)
    {
        Directory.CreateDirectory(_rootPath);
        return Path.Combine(_rootPath, fileName);
    }
}
