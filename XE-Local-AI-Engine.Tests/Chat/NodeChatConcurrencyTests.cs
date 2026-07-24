namespace XE_Local_AI_Engine.Tests.Chat;

using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Real-SQLite concurrency coverage: the per-conversation lock hierarchy plus the unique
///     <c>(conversation_id, sequence)</c> index must keep sequences distinct under concurrent inserts, keep a delete
///     from stranding partial rows, and keep the writer's lock map bounded.
/// </summary>
public sealed class NodeChatConcurrencyTests : IDisposable
{
    private const int ConcurrentInsertsPerConversation = 8;
    private const int SequenceRaceIterations = 40;

    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    [Test]
    public async Task ConcurrentInserts_OnOneConversation_AllocateDistinctContiguousSequences()
    {
        await using var provider = await BuildProviderAsync("concurrent-sequences.sqlite").ConfigureAwait(false);
        var service = CreateService(provider);

        // Run many iterations: a dropped lock or a lost MAX(sequence)+1 read shows up as a duplicate or a gap in at
        // least one iteration, so the loop is the detector.
        for (var iteration = 0; iteration < SequenceRaceIterations; iteration++)
        {
            var conversation = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Race", "node", CreatedAtUtc: iteration)).ConfigureAwait(false);

            var inserts = Enumerable.Range(0, ConcurrentInsertsPerConversation)
                                    .Select(index => Task.Run(() => service.PersistUserMessageAsync(
                                        new NodeChatPersistUserMessageRequest(conversation.ConversationId, Guid.NewGuid(), $"m{index}", CreatedAtUtc: 100 + index))))
                                    .ToArray();
            await Task.WhenAll(inserts).ConfigureAwait(false);

            var loaded = AssertEx.NotNull(await service.GetConversationAsync(conversation.ConversationId).ConfigureAwait(false));
            var sequences = loaded.Messages.Select(message => message.Sequence).OrderBy(sequence => sequence).ToArray();

            AssertEx.Equal(ConcurrentInsertsPerConversation, loaded.Messages.Count);
            AssertEx.Equal(ConcurrentInsertsPerConversation, sequences.Distinct().Count());
            for (var expected = 0; expected < ConcurrentInsertsPerConversation; expected++)
            {
                AssertEx.Equal(expected, sequences[expected], "Concurrent inserts must produce contiguous, gap-free sequences.");
            }
        }
    }

    [Test]
    public async Task PurgeDelete_RacingPayloadWrites_LeavesNoRows()
    {
        await using var provider = await BuildProviderAsync("delete-race.sqlite").ConfigureAwait(false);
        var service = CreateService(provider);

        for (var iteration = 0; iteration < SequenceRaceIterations; iteration++)
        {
            var conversation = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Delete race", "node", CreatedAtUtc: iteration)).ConfigureAwait(false);
            var assistantMessageId = Guid.NewGuid();
            var correlation = new NodeChatMessageCorrelation(conversation.ConversationId, assistantMessageId, Guid.NewGuid());
            await service.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(conversation.ConversationId, assistantMessageId, correlation.RequestId, CreatedAtUtc: 1))
                         .ConfigureAwait(false);
            await service.MarkAssistantStreamingAsync(correlation, updatedAtUtc: 2).ConfigureAwait(false);

            // Fire streaming flushes concurrently with a hard purge. A flush that loses the race to the delete finds no
            // row and throws — expected; what must never happen is a surviving/partial row after the purge commits.
            var writes = Enumerable.Range(0, 4)
                                   .Select(index => Task.Run(async () =>
                                   {
                                       try
                                       {
                                           await service.FlushAssistantPartialAsync(new NodeChatPartialFlushRequest(correlation, $"chunk{index}", Reasoning: null, UpdatedAtUtc: 10 + index,
                                                            ReplaceContent: false))
                                                        .ConfigureAwait(false);
                                       }
                                       catch (InvalidOperationException)
                                       {
                                           // The row was purged before this flush acquired its locks.
                                       }
                                   }))
                                   .ToList();
            writes.Add(Task.Run(() => service.DeleteConversationAsync(new NodeChatDeleteConversationRequest(conversation.ConversationId, DeletedAtUtc: 20, PurgeImmediately: true))));
            await Task.WhenAll(writes).ConfigureAwait(false);

            AssertEx.Null(await service.GetConversationAsync(conversation.ConversationId).ConfigureAwait(false));
            AssertEx.Equal(expected: 0, await CountMessagesAsync(provider, conversation.ConversationId).ConfigureAwait(false));
        }
    }

    [Test]
    public async Task LockMap_IsReleasedAfterOperationsComplete()
    {
        await using var provider = await BuildProviderAsync("bounded-locks.sqlite").ConfigureAwait(false);
        var writer = provider.GetRequiredService<NodeChatPersistenceWriter>();
        var service = new NodeChatPersistenceService(writer);

        // Touch many distinct conversations and messages; the refcounted gate map must drop every gate once idle rather
        // than growing with history.
        for (var index = 0; index < 100; index++)
        {
            var conversation = await service.CreateConversationAsync(new NodeChatCreateConversationRequest($"c{index}", "node", CreatedAtUtc: index)).ConfigureAwait(false);
            var correlation = new NodeChatMessageCorrelation(conversation.ConversationId, Guid.NewGuid(), Guid.NewGuid());
            await service.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(conversation.ConversationId, correlation.MessageId, correlation.RequestId, CreatedAtUtc: 1))
                         .ConfigureAwait(false);
            await service.MarkAssistantStreamingAsync(correlation, updatedAtUtc: 2).ConfigureAwait(false);
        }

        AssertEx.Equal(expected: 0, writer.ActiveConversationLockCount);
    }

    private async Task<ServiceProvider> BuildProviderAsync(string fileName)
    {
        Directory.CreateDirectory(_rootPath);
        var databasePath = Path.Combine(_rootPath, fileName);
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

    private static NodeChatPersistenceService CreateService(ServiceProvider provider)
    {
        return new NodeChatPersistenceService(provider.GetRequiredService<NodeChatPersistenceWriter>());
    }

    private static async Task<long> CountMessagesAsync(ServiceProvider provider, Guid conversationId)
    {
        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
        var connection = dbContext.Database.GetDbConnection();
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM messages WHERE conversation_id = $conversation_id;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$conversation_id";
        parameter.Value = conversationId;
        command.Parameters.Add(parameter);
        var count = await command.ExecuteScalarAsync().ConfigureAwait(false);
        return Convert.ToInt64(count, CultureInfo.InvariantCulture);
    }
}
