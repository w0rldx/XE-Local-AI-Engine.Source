namespace XE_Local_AI_Engine.Tests.Chat;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Regression guard for the raw SQL run by <c>NodeChatTitleEncryptionBackfillService</c>. EF's
///     <c>SqlQueryRaw</c> wraps the supplied SQL in <c>SELECT ... FROM (&lt;rawsql&gt;) AS x</c>, so a trailing
///     <c>;</c> in the query text produces <c>SQLite Error 1: near ";": syntax error</c> at startup. These tests
///     execute the exact query strings against a real SQLite context so a malformed-SQL regression fails here
///     instead of silently failing the backfill at boot. The service swallows exceptions by design, so the query
///     text is exercised directly.
/// </summary>
public sealed class NodeChatTitleEncryptionBackfillQueryTests : IDisposable
{
    // Mirrors the private projection in NodeChatTitleEncryptionBackfillService so the second query can be
    // materialized through the public Database.SqlQueryRaw surface.
    private sealed record MessageIdAndContent(Guid MessageId, byte[] Content);

    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, true);
        }
    }

    [Test]
    public async Task NullTitleConversationQuery_ExecutesWithoutSqlSyntaxError()
    {
        await using var provider = await BuildProviderAsync("backfill-null-title.sqlite").ConfigureAwait(false);
        var service = CreateService(provider);

        var conversation = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Local chat", "node", 10)).ConfigureAwait(false);
        await service.PersistUserMessageAsync(new NodeChatPersistUserMessageRequest(conversation.ConversationId, Guid.NewGuid(), "hello world", 11)).ConfigureAwait(false);

        await using (var scope = provider.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();

            // Reproduce the EncryptConversationTitle migration's effect: NULL the title so the backfill query has a row.
            await dbContext.Database
                .ExecuteSqlRawAsync("UPDATE conversations SET title = NULL WHERE conversation_id = {0}", conversation.ConversationId)
                .ConfigureAwait(false);

            // The exact query from NodeChatTitleEncryptionBackfillService — a trailing ';' breaks EF subquery wrapping.
            var conversationIds = await dbContext.Database
                .SqlQueryRaw<Guid>("SELECT conversation_id FROM conversations WHERE purged = 0 AND title IS NULL")
                .ToListAsync()
                .ConfigureAwait(false);

            AssertEx.Contains(conversationIds, conversation.ConversationId);
        }
    }

    [Test]
    public async Task FirstUserMessageQuery_ExecutesWithoutSqlSyntaxError()
    {
        await using var provider = await BuildProviderAsync("backfill-first-user-message.sqlite").ConfigureAwait(false);
        var service = CreateService(provider);

        var conversation = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Local chat", "node", 10)).ConfigureAwait(false);
        var messageId = Guid.NewGuid();
        await service.PersistUserMessageAsync(new NodeChatPersistUserMessageRequest(conversation.ConversationId, messageId, "hello world", 11)).ConfigureAwait(false);

        await using (var scope = provider.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();

            // The exact query from NodeChatTitleEncryptionBackfillService.
            var row = await dbContext.Database
                .SqlQueryRaw<MessageIdAndContent>(
                    "SELECT message_id AS MessageId, content AS Content FROM messages WHERE conversation_id = {0} AND role = 'user' ORDER BY sequence ASC LIMIT 1",
                    conversation.ConversationId)
                .FirstOrDefaultAsync()
                .ConfigureAwait(false);

            AssertEx.Equal(messageId, AssertEx.NotNull(row).MessageId);
        }
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
