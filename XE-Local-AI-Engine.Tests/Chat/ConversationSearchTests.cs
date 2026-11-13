namespace XE_Local_AI_Engine.Tests.Chat;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The conversation content search end to end through the real raw-SQL chat paths: every write path keeps the
///     in-memory index current, the start-up backfill finds rows stored before the index existed, and only chats match.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class ConversationSearchTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    private int _failingContextResolutions;

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    [Test]
    public async Task Search_FindsTitleAndMessageText_CaseInsensitively_WithASnippetAroundTheHit()
    {
        await using var provider = await BuildProviderAsync("search-basic.sqlite");
        var service = CreateService(provider);
        var conversation = await CreateChatAsync(service, "Holiday planning", createdAtUtc: 10);
        await PersistUserMessageAsync(service, conversation, "We should visit the Alhambra in Granada next spring.", createdAtUtc: 11);

        var byContent = await SearchAsync(service, "alhambra GRANADA");
        var byTitle = await SearchAsync(service, "holiday");

        AssertEx.Equal(Expected(conversation), Found(byContent));
        var contentHit = byContent.Items[0];
        AssertEx.Equal(NodeChatSearchMatchValues.Content, contentHit.MatchedIn);
        AssertEx.Equal("Holiday planning", contentHit.Title);
        AssertEx.Contains(contentHit.Snippet, "Alhambra in Granada");
        AssertEx.Equal(Expected(conversation), Found(byTitle));
        AssertEx.Equal(NodeChatSearchMatchValues.Title, byTitle.Items[0].MatchedIn);
        AssertEx.Empty((await SearchAsync(service, "alhambra seville")).Items, "Every term must occur.");
    }

    [Test]
    public async Task Search_ExcludesWorkSessionAndIntegrationTranscripts()
    {
        await using var provider = await BuildProviderAsync("search-kind.sqlite");
        var service = CreateService(provider);
        var chat = await CreateChatAsync(service, "kindmarker chat", createdAtUtc: 10);
        var workSession = await service.CreateConversationAsync(new NodeChatCreateConversationRequest
        {
            Title = "kindmarker work session",
            UserId = "node",
            CreatedAtUtc = 20,
            Kind = NodeConversationKind.WorkSession
        });
        var integration = await service.CreateConversationAsync(new NodeChatCreateConversationRequest
        {
            Title = "kindmarker integration",
            UserId = "node",
            CreatedAtUtc = 30,
            Kind = NodeConversationKind.Integration
        });
        await PersistUserMessageAsync(service, workSession.ConversationId, "kindmarker inside a work session", createdAtUtc: 21);
        await PersistUserMessageAsync(service, integration.ConversationId, "kindmarker inside an integration", createdAtUtc: 31);

        var found = await SearchAsync(service, "kindmarker", includeArchived: true);

        AssertEx.Equal(Expected(chat), Found(found),
            "A work-session or integration transcript is not a chat the operator started; search must not surface it, exactly like the list.");
    }

    [Test]
    public async Task Search_FollowsEveryMessageWrite_SendStreamTerminalAndVariant()
    {
        await using var provider = await BuildProviderAsync("search-messages.sqlite");
        var service = CreateService(provider);
        var conversation = await CreateChatAsync(service, "Messages", createdAtUtc: 10);
        await PersistUserMessageAsync(service, conversation, "first question", createdAtUtc: 11);
        AssertEx.Equal(Expected(conversation), Found(await SearchAsync(service, "first question")));

        var correlation = new NodeChatMessageCorrelation
        {
            ConversationId = conversation,
            MessageId = Guid.NewGuid(),
            RequestId = Guid.NewGuid()
        };
        await service.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest
        {
            ConversationId = conversation,
            MessageId = correlation.MessageId,
            RequestId = correlation.RequestId,
            CreatedAtUtc = 12
        });
        await service.MarkAssistantStreamingAsync(correlation, updatedAtUtc: 13);
        await service.FlushAssistantPartialAsync(new NodeChatPartialFlushRequest
        {
            Correlation = correlation,
            Content = "streamedpartial",
            Reasoning = null,
            UpdatedAtUtc = 14
        });
        AssertEx.Equal(Expected(conversation), Found(await SearchAsync(service, "streamedpartial")), "A partial flush must be searchable before the turn ends.");

        await service.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest
        {
            Correlation = correlation,
            Status = NodeChatMessageStatusValues.Completed,
            UpdatedAtUtc = 15,
            Content = "finalanswer"
        });
        AssertEx.Equal(Expected(conversation), Found(await SearchAsync(service, "finalanswer")));
        AssertEx.Empty((await SearchAsync(service, "streamedpartial")).Items, "The terminal content replaced the partial, so the old text must stop matching.");

        var variant = await service.CreateMessageVariantAsync(new NodeChatCreateMessageVariantRequest
        {
            ConversationId = conversation,
            OriginalMessageId = correlation.MessageId,
            NewMessageId = Guid.NewGuid(),
            RequestId = Guid.NewGuid(),
            CreatedAtUtc = 16
        });
        await service.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest
        {
            Correlation = new NodeChatMessageCorrelation
            {
                ConversationId = conversation,
                MessageId = variant.Variant.MessageId,
                RequestId = variant.Variant.RequestId ?? throw new InvalidOperationException("A regenerate variant carries its own request id.")
            },
            Status = NodeChatMessageStatusValues.Completed,
            UpdatedAtUtc = 17,
            Content = "regeneratedanswer"
        });
        AssertEx.Equal(Expected(conversation), Found(await SearchAsync(service, "regeneratedanswer")),
            "A regenerated or revised message is a variant row; it must be searchable.");
    }

    [Test]
    public async Task Search_FollowsConversationWrites_RenameArchiveBranchDeleteAndPurge()
    {
        await using var provider = await BuildProviderAsync("search-lifecycle.sqlite");
        var service = CreateService(provider);
        var conversation = await CreateChatAsync(service, "oldtitle", createdAtUtc: 10);
        var userMessage = await PersistUserMessageAsync(service, conversation, "lifecyclemarker text", createdAtUtc: 11);

        await service.RenameConversationAsync(new NodeChatRenameConversationRequest { ConversationId = conversation, Title = "newtitle", UpdatedAtUtc = 12 });
        AssertEx.Empty((await SearchAsync(service, "oldtitle")).Items);
        AssertEx.Equal(Expected(conversation), Found(await SearchAsync(service, "newtitle")));

        await service.SetConversationArchivedAsync(new NodeChatSetConversationArchivedRequest { ConversationId = conversation, Archived = true, UpdatedAtUtc = 13 });
        AssertEx.Empty((await SearchAsync(service, "lifecyclemarker")).Items, "An archived chat is hidden unless archived ones are included, as in the list.");
        var archived = await SearchAsync(service, "lifecyclemarker", includeArchived: true);
        AssertEx.Equal(Expected(conversation), Found(archived));
        AssertEx.True(archived.Items[0].Archived);
        await service.SetConversationArchivedAsync(new NodeChatSetConversationArchivedRequest { ConversationId = conversation, Archived = false, UpdatedAtUtc = 14 });
        AssertEx.Equal(Expected(conversation), Found(await SearchAsync(service, "lifecyclemarker")));

        var branch = await service.BranchConversationAsync(new NodeChatBranchConversationRequest
        {
            ConversationId = conversation,
            MessageId = userMessage.MessageId,
            CreatedAtUtc = 15
        });
        AssertEx.Contains(Found(await SearchAsync(service, "lifecyclemarker")), branch.BranchedConversationId.ToString(), StringComparison.Ordinal,
            "A branch copies the messages into a new conversation, which must be searchable at once.");

        await service.DeleteConversationAsync(new NodeChatDeleteConversationRequest { ConversationId = conversation, DeletedAtUtc = 16 });
        AssertEx.Equal(Expected(branch.BranchedConversationId), Found(await SearchAsync(service, "lifecyclemarker", includeArchived: true)),
            "A deleted conversation must leave the index even though its rows remain until purge.");

        await service.DeleteConversationAsync(new NodeChatDeleteConversationRequest
        {
            ConversationId = branch.BranchedConversationId,
            DeletedAtUtc = 17,
            PurgeImmediately = true
        });
        AssertEx.Empty((await SearchAsync(service, "lifecyclemarker", includeArchived: true)).Items);
    }

    [Test]
    public async Task Backfill_FindsConversationsStoredBeforeTheIndexExisted()
    {
        await using var provider = await BuildProviderAsync("search-restart.sqlite");
        var beforeRestart = CreateService(provider);
        var older = await CreateChatAsync(beforeRestart, "older", createdAtUtc: 10);
        await PersistUserMessageAsync(beforeRestart, older, "restartmarker written before the restart", createdAtUtc: 11);
        var newer = await CreateChatAsync(beforeRestart, "newer", createdAtUtc: 20);
        await PersistUserMessageAsync(beforeRestart, newer, "restartmarker also before", createdAtUtc: 21);

        // A fresh writer is a fresh process: its index starts empty over the rows already on disk.
        var restartedWriter = new NodeChatPersistenceWriter(provider.GetRequiredService<IServiceScopeFactory>());
        var afterRestart = new NodeChatPersistenceService(restartedWriter, UnknownUsageProviderResolver.Instance);
        var beforeBackfill = await SearchAsync(afterRestart, "restartmarker");
        AssertEx.Empty(beforeBackfill.Items);
        AssertEx.True(beforeBackfill.Indexing, "Until the backfill finishes the response must say results may be incomplete.");

        await new NodeChatReadModel(restartedWriter).BackfillSearchIndexAsync(batchSize: 1, CancellationToken.None);

        var afterBackfill = await SearchAsync(afterRestart, "restartmarker");
        AssertEx.False(afterBackfill.Indexing);
        AssertEx.Equal(Expected(newer, older), Found(afterBackfill), "Every stored chat is found, newest first.");
    }

    [Test]
    public async Task Backfill_RetriesAFailedPassWithGrowingBackoff_UntilChatsStoredBeforeTheRestartAreFound()
    {
        await using var provider = await BuildProviderAsync("search-backfill-retry.sqlite");
        var beforeRestart = CreateService(provider);
        var conversation = await CreateChatAsync(beforeRestart, "older", createdAtUtc: 10);
        await PersistUserMessageAsync(beforeRestart, conversation, "retrymarker written before the restart", createdAtUtc: 11);
        var restartedWriter = new NodeChatPersistenceWriter(provider.GetRequiredService<IServiceScopeFactory>());
        var afterRestart = new NodeChatPersistenceService(restartedWriter, UnknownUsageProviderResolver.Instance);
        var clock = new ManualTimeProvider();
        var backfill = new ConversationSearchBackfillService(restartedWriter, clock, NullLogger<ConversationSearchBackfillService>.Instance);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Volatile.Write(ref _failingContextResolutions, 2);

        var run = BackgroundServiceTestHelper.RunExecuteAsync(backfill, stop.Token);
        try
        {
            await AssertEx.EventuallyAsync(() => clock.ArmedTimerCount == 1, TimeSpan.FromSeconds(10), "The first failed pass must wait for a retry, not give up.");
            clock.Advance(ConversationSearchBackfillService.FirstRetryDelay);
            await AssertEx.EventuallyAsync(() => Volatile.Read(ref _failingContextResolutions) <= 0 && clock.ArmedTimerCount == 1, TimeSpan.FromSeconds(10),
                "The second failed pass must wait for a retry too.");
            AssertEx.True((await SearchAsync(afterRestart, "retrymarker")).Indexing, "Until a pass succeeds the response must say results may be incomplete.");

            clock.Advance(ConversationSearchBackfillService.FirstRetryDelay);
            AssertEx.Equal(expected: 1, clock.ArmedTimerCount, "The second wait is twice the first, so one first-delay step must not end it.");
            clock.Advance(ConversationSearchBackfillService.FirstRetryDelay);
            await run.WaitAsync(stop.Token);
        }
        finally
        {
            await stop.CancelAsync();
            // The service ends quietly on cancellation; anything it throws is a real failure and surfaces here.
            await run;
            backfill.Dispose();
        }

        var afterBackfill = await SearchAsync(afterRestart, "retrymarker");
        AssertEx.False(afterBackfill.Indexing, "A retried pass that succeeds completes the backfill.");
        AssertEx.Equal(Expected(conversation), Found(afterBackfill), "A chat written before the restart must be findable once a retry succeeds.");
    }

    [Test]
    public async Task Backfill_SkipsAChatItCannotRead_CompletesForTheRest_AndRetriesOnlyThatChat()
    {
        await using var provider = await BuildProviderAsync("search-backfill-skip.sqlite");
        var beforeRestart = CreateService(provider);
        var unreadable = await CreateChatAsync(beforeRestart, "unreadable", createdAtUtc: 10);
        await PersistUserMessageAsync(beforeRestart, unreadable, "skipmarker in the unreadable chat", createdAtUtc: 11);
        var healthy = await CreateChatAsync(beforeRestart, "healthy", createdAtUtc: 20);
        await PersistUserMessageAsync(beforeRestart, healthy, "skipmarker in the healthy chat", createdAtUtc: 21);
        await using (var scope = provider.CreateAsyncScope())
        {
            // A message id that is not a Guid fails the chat's read every time, unlike a transient failure.
            var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
            _ = await dbContext.Database.ExecuteSqlRawAsync("UPDATE messages SET message_id = 'not-a-guid' WHERE conversation_id = {0};", [unreadable]);
        }

        var restartedWriter = new NodeChatPersistenceWriter(provider.GetRequiredService<IServiceScopeFactory>());
        var afterRestart = new NodeChatPersistenceService(restartedWriter, UnknownUsageProviderResolver.Instance);
        var clock = new ManualTimeProvider();
        var backfill = new ConversationSearchBackfillService(restartedWriter, clock, NullLogger<ConversationSearchBackfillService>.Instance);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var run = BackgroundServiceTestHelper.RunExecuteAsync(backfill, stop.Token);
        try
        {
            await AssertEx.EventuallyAsync(() => clock.ArmedTimerCount == 1, TimeSpan.FromSeconds(10), "A pass that skipped a chat must schedule a retry for it.");
            var result = await SearchAsync(afterRestart, "skipmarker");
            AssertEx.False(result.Indexing, "One unreadable chat must not keep the whole index reported as incomplete.");
            AssertEx.Equal(Expected(healthy), Found(result), "Every readable chat is searchable after the pass.");
            AssertEx.False(restartedWriter.SearchIndex.Contains(unreadable));
        }
        finally
        {
            await stop.CancelAsync();
            await run;
            backfill.Dispose();
        }
    }

    [Test]
    public async Task Search_SkipsAStaleChatItCannotRead_AnswersWithTheRest_AndRetriesThatChatNextTime()
    {
        await using var provider = await BuildProviderAsync("search-refresh-skip.sqlite");
        var service = CreateService(provider);
        var healthy = await CreateChatAsync(service, "healthy", createdAtUtc: 10);
        await PersistUserMessageAsync(service, healthy, "refreshskip in the healthy chat", createdAtUtc: 11);
        var unreadable = await CreateChatAsync(service, "unreadable", createdAtUtc: 20);
        // The write marks the chat stale; no search has read it yet.
        var message = await PersistUserMessageAsync(service, unreadable, "refreshskip in the unreadable chat", createdAtUtc: 21);
        await SetMessageIdAsync(provider, unreadable, "not-a-guid");

        var during = await SearchAsync(service, "refreshskip");

        AssertEx.Equal(Expected(healthy), Found(during), "One chat whose read always fails must not fail the search for every other chat.");
        await SetMessageIdAsync(provider, unreadable, message.MessageId);
        var after = await SearchAsync(service, "refreshskip");
        AssertEx.Equal(Expected(unreadable, healthy), Found(after), "The skipped chat kept its stale mark, so the next search reads it again without a new write.");
    }

    [Test]
    public async Task Search_FindsAWriteThatLandedBetweenARefreshReadAndItsClear()
    {
        await using var provider = await BuildProviderAsync("search-race.sqlite");
        var service = CreateService(provider);
        var index = provider.GetRequiredService<NodeChatPersistenceWriter>().SearchIndex;
        var conversation = await CreateChatAsync(service, "Race", createdAtUtc: 10);
        await PersistUserMessageAsync(service, conversation, "written before the search", createdAtUtc: 11);
        index.AfterRefreshStore = async conversationId =>
        {
            index.AfterRefreshStore = null;
            await PersistUserMessageAsync(service, conversationId, "racemarker written mid-refresh", createdAtUtc: 12);
        };

        var during = await SearchAsync(service, "racemarker");
        var after = await SearchAsync(service, "racemarker");

        AssertEx.Empty(during.Items, "The refresh read the conversation before the write committed.");
        AssertEx.Equal(Expected(conversation), Found(after),
            "The write's own stale mark is newer than the one the refresh clears, so the next search must re-read it.");
    }

    [Test]
    public async Task Search_KeepsALongChatsNewestText_PastThePerConversationCap()
    {
        await using var provider = await BuildProviderAsync("search-cap.sqlite");
        var service = CreateService(provider);
        var conversation = await CreateChatAsync(service, "Long chat", createdAtUtc: 10);
        await PersistUserMessageAsync(service, conversation, "oldestmarker " + new string('x', ConversationSearchIndex.MaxIndexedCharsPerConversation), createdAtUtc: 11);
        await PersistUserMessageAsync(service, conversation, "newestmarker", createdAtUtc: 12);

        AssertEx.Equal(Expected(conversation), Found(await SearchAsync(service, "newestmarker")), "The latest message of a long chat must stay searchable.");
        AssertEx.Empty((await SearchAsync(service, "oldestmarker")).Items, "The cap drops the oldest text first.");
    }

    [Test]
    public async Task Search_SkipsOnlyTheUnreadableValues_WhenOneChatHoldsATruncatedOrTamperedEnvelope()
    {
        await using var provider = await BuildProviderAsync("search-unreadable.sqlite");
        var service = CreateService(provider);
        var broken = await CreateChatAsync(service, "brokentitlemarker", createdAtUtc: 10);
        await PersistUserMessageAsync(service, broken, "brokenmarker still readable", createdAtUtc: 11);
        var tampered = await PersistUserMessageAsync(service, broken, "tamperedmarker", createdAtUtc: 12);
        var healthy = await CreateChatAsync(service, "healthy", createdAtUtc: 20);
        await PersistUserMessageAsync(service, healthy, "healthymarker", createdAtUtc: 21);
        await using (var scope = provider.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
            // A title too short to hold a nonce and tag, and a content envelope whose tag no longer authenticates.
            _ = await dbContext.Database.ExecuteSqlRawAsync("UPDATE conversations SET title = {0} WHERE conversation_id = {1};", [new byte[] { 1, 2 }, broken]);
            _ = await dbContext.Database.ExecuteSqlRawAsync("UPDATE messages SET content = {0} WHERE message_id = {1};",
                [new byte[] { 0xFE, 0x01 }.Concat(new byte[40]).ToArray(), tampered.MessageId]);
        }

        // A restarted node meets the damaged rows in its backfill.
        var restartedWriter = new NodeChatPersistenceWriter(provider.GetRequiredService<IServiceScopeFactory>());
        var afterRestart = new NodeChatPersistenceService(restartedWriter, UnknownUsageProviderResolver.Instance);
        await new NodeChatReadModel(restartedWriter).BackfillSearchIndexAsync(batchSize: 10, CancellationToken.None);

        AssertEx.Equal(Expected(healthy), Found(await SearchAsync(afterRestart, "healthymarker")), "One unreadable chat must not fail every search.");
        AssertEx.Equal(Expected(broken), Found(await SearchAsync(afterRestart, "brokenmarker")), "The damaged chat's readable text stays searchable.");
        AssertEx.Empty((await SearchAsync(afterRestart, "tamperedmarker")).Items);
        AssertEx.Empty((await SearchAsync(afterRestart, "brokentitlemarker")).Items);
    }

    [Test]
    public async Task Delete_DropsTheIndexedPlaintextAtOnce_WithoutWaitingForASearch()
    {
        await using var provider = await BuildProviderAsync("search-eager-delete.sqlite");
        var service = CreateService(provider);
        var index = provider.GetRequiredService<NodeChatPersistenceWriter>().SearchIndex;
        var conversation = await CreateChatAsync(service, "Eager", createdAtUtc: 10);
        await PersistUserMessageAsync(service, conversation, "eagermarker", createdAtUtc: 11);
        AssertEx.Equal(Expected(conversation), Found(await SearchAsync(service, "eagermarker")));

        await service.DeleteConversationAsync(new NodeChatDeleteConversationRequest { ConversationId = conversation, DeletedAtUtc = 12 });

        AssertEx.False(index.Contains(conversation), "A deleted chat's decrypted text must leave memory when the delete commits.");
    }

    private static string Found(NodeChatConversationSearchResultDto result)
    {
        return string.Join(',', result.Items.Select(static hit => hit.ConversationId));
    }

    private static string Expected(params Guid[] conversationIds)
    {
        return string.Join(',', conversationIds);
    }

    private static Task<NodeChatConversationSearchResultDto> SearchAsync(NodeChatPersistenceService service, string query, bool includeArchived = false)
    {
        return service.SearchConversationsAsync(new NodeChatSearchConversationsRequest
        {
            Query = query,
            IncludeArchived = includeArchived,
            Limit = 50
        });
    }

    private static async Task<Guid> CreateChatAsync(NodeChatPersistenceService service, string title, long createdAtUtc)
    {
        var created = await service.CreateConversationAsync(new NodeChatCreateConversationRequest
        {
            Title = title,
            UserId = "node",
            CreatedAtUtc = createdAtUtc
        });
        return created.ConversationId;
    }

    private static Task<NodeChatPersistedMessageDto> PersistUserMessageAsync(NodeChatPersistenceService service, Guid conversationId, string content, long createdAtUtc)
    {
        return service.PersistUserMessageAsync(new NodeChatPersistUserMessageRequest
        {
            ConversationId = conversationId,
            MessageId = Guid.NewGuid(),
            Content = content,
            CreatedAtUtc = createdAtUtc
        });
    }

    private static async Task SetMessageIdAsync(ServiceProvider provider, Guid conversationId, object messageId)
    {
        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
        _ = await dbContext.Database.ExecuteSqlRawAsync("UPDATE messages SET message_id = {0} WHERE conversation_id = {1};", [messageId, conversationId]);
    }

    private async Task<ServiceProvider> BuildProviderAsync(string fileName)
    {
        Directory.CreateDirectory(_rootPath);
        var databasePath = Path.Combine(_rootPath, fileName);
        var services = new ServiceCollection();
        // While the test arms failures, each DbContext resolution throws once: a transient failure of a whole pass.
        services.AddScoped<INodeSqliteKeyHolder>(_ => Interlocked.Decrement(ref _failingContextResolutions) >= 0
            ? throw new InvalidOperationException("Simulated transient database failure.")
            : new NullNodeSqliteKeyHolder());
        services.AddDbContext<NodeChatDbContext>(options => options.UseSqlite($"Data Source={databasePath}"));
        services.AddSingleton<NodeChatPersistenceWriter>();

        var provider = services.BuildServiceProvider(true);
        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
        await dbContext.Database.EnsureDeletedAsync();
        await dbContext.Database.EnsureCreatedAsync();

        return provider;
    }

    private static NodeChatPersistenceService CreateService(ServiceProvider provider) =>
        new(provider.GetRequiredService<NodeChatPersistenceWriter>(), UnknownUsageProviderResolver.Instance);
}
