namespace XE_Local_AI_Engine.Tests.Chat;

using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The <c>conversations.kind</c> discriminator, end to end through the real raw-SQL chat paths: the list is
///     chat-only, a by-id read is not, and a caller may both choose a kind and supply the conversation id.
/// </summary>
public sealed class ConversationKindTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ListConversations_ReturnsChatsAndExcludesWorkSessionAndIntegrationTranscripts(bool includeArchived)
    {
        await using var provider = await BuildProviderAsync($"kind-list-{includeArchived}.sqlite").ConfigureAwait(false);
        var service = CreateService(provider);

        var chat = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("A chat", "node", CreatedAtUtc: 10)).ConfigureAwait(false);
        var workSession = await service
                                .CreateConversationAsync(new NodeChatCreateConversationRequest("Work session", "node", CreatedAtUtc: 20,
                                    Kind: NodeConversationKind.WorkSession))
                                .ConfigureAwait(false);
        var integration = await service
                                .CreateConversationAsync(new NodeChatCreateConversationRequest("Integration", "node", CreatedAtUtc: 30,
                                    Kind: NodeConversationKind.Integration))
                                .ConfigureAwait(false);

        var listed = (await service.ListConversationsAsync(new NodeChatListConversationsRequest(includeArchived)).ConfigureAwait(false))
                     .Select(static summary => summary.ConversationId)
                     .ToArray();

        AssertEx.True(listed.Contains(chat.ConversationId), "An ordinary chat must still appear in the chat list.");
        AssertEx.False(listed.Contains(workSession.ConversationId),
            "A work session's owned transcript must not appear as a chat the operator did not start — that is the leak the kind column closes.");
        AssertEx.False(listed.Contains(integration.ConversationId), "An integration session's owned transcript must not appear in the chat list either.");
    }

    [Test]
    public async Task GetConversation_StillReturnsANonChatConversationById()
    {
        await using var provider = await BuildProviderAsync("kind-by-id.sqlite").ConfigureAwait(false);
        var service = CreateService(provider);

        var integration = await service
                                .CreateConversationAsync(new NodeChatCreateConversationRequest("Integration", "node", CreatedAtUtc: 10,
                                    Kind: NodeConversationKind.Integration))
                                .ConfigureAwait(false);

        var loaded = AssertEx.NotNull(await service.GetConversationAsync(integration.ConversationId).ConfigureAwait(false),
            "By-id reads stay unfiltered: the session transcript readers legitimately load these rows.");
        AssertEx.Equal(integration.ConversationId, loaded.ConversationId);
    }

    [Test]
    public async Task CreateConversation_DefaultsToChatAndHonoursAnExplicitKind()
    {
        await using var provider = await BuildProviderAsync("kind-default.sqlite").ConfigureAwait(false);
        var service = CreateService(provider);

        var defaulted = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Default", "node", CreatedAtUtc: 10)).ConfigureAwait(false);
        var explicitKind = await service
                                 .CreateConversationAsync(new NodeChatCreateConversationRequest("Explicit", "node", CreatedAtUtc: 20,
                                     Kind: NodeConversationKind.WorkSession))
                                 .ConfigureAwait(false);

        AssertEx.Equal(NodeConversationKind.Chat, await ReadKindAsync(provider, defaulted.ConversationId).ConfigureAwait(false));
        AssertEx.Equal(NodeConversationKind.WorkSession, await ReadKindAsync(provider, explicitKind.ConversationId).ConfigureAwait(false));
    }

    [Test]
    public async Task CreateConversation_UsesACallerSuppliedIdAndStillMintsOneWhenNoneIsGiven()
    {
        await using var provider = await BuildProviderAsync("kind-supplied-id.sqlite").ConfigureAwait(false);
        var service = CreateService(provider);
        var preMinted = Guid.NewGuid();

        var supplied = await service
                             .CreateConversationAsync(new NodeChatCreateConversationRequest("Pre-minted", "node", CreatedAtUtc: 10,
                                 Kind: NodeConversationKind.Integration,
                                 ConversationId: preMinted))
                             .ConfigureAwait(false);

        AssertEx.Equal(preMinted, supplied.ConversationId, "The integration accept path commits its rows first and creates the conversation at the id they carry.");
        var loaded = AssertEx.NotNull(await service.GetConversationAsync(preMinted).ConfigureAwait(false), "The stored row must be readable at the caller's id.");
        AssertEx.Equal(preMinted, loaded.ConversationId);

        // The regression guard: every existing caller passes nothing, and a botched `??` would mint over a supplied id
        // or, worse, insert Guid.Empty.
        var minted = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Minted", "node", CreatedAtUtc: 20)).ConfigureAwait(false);
        AssertEx.NotEqual(Guid.Empty, minted.ConversationId);
        AssertEx.NotEqual(preMinted, minted.ConversationId);
    }

    [Test]
    public async Task TheMigrationBackfill_MatchesAChatPathConversationAgainstAnEfWrittenWorkSessionRow()
    {
        // The backfill joins conversations.conversation_id (written by the raw-ADO chat path) to
        // agent_work_sessions.conversation_id (written by EF). Those are two different parameter-binding paths, so the
        // statement is only correct if both store the Guid the same way. Running the migration's exact SQL over rows
        // produced by both real writers is the only assertion that grades that.
        await using var provider = await BuildProviderAsync("kind-backfill-join.sqlite").ConfigureAwait(false);
        var service = CreateService(provider);

        var owned = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Owned", "node", CreatedAtUtc: 10)).ConfigureAwait(false);
        var plain = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Plain", "node", CreatedAtUtc: 20)).ConfigureAwait(false);

        await using (var scope = provider.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();

            // Written through EF's own Guid parameter binding, which is what AgentWorkSessionStore uses; the
            // conversation above went in through the chat path's raw-ADO binding. Those are the two encodings the
            // backfill has to agree on.
            _ = await dbContext.Database.ExecuteSqlRawAsync("""
                                                            INSERT INTO agent_work_sessions (
                                                                id, title, objective, kind, agent_definition_id, conversation_id, status,
                                                                step_count, last_sequence, config_version, created_at_utc, updated_at_utc, version)
                                                            VALUES ({0}, 'Seeded session', zeroblob(16), 'Research', {1}, {2}, 'Draft', 0, 0, 1, 10, 10, 0);
                                                            """,
                                   Guid.NewGuid(),
                                   Guid.NewGuid(),
                                   owned.ConversationId)
                               .ConfigureAwait(false);

            _ = await dbContext.Database.ExecuteSqlRawAsync("""
                                                            UPDATE conversations
                                                            SET kind = 'work-session'
                                                            WHERE conversation_id IN (SELECT conversation_id FROM agent_work_sessions);
                                                            """).ConfigureAwait(false);
        }

        AssertEx.Equal(NodeConversationKind.WorkSession, await ReadKindAsync(provider, owned.ConversationId).ConfigureAwait(false),
            "The backfill must reach a conversation the chat path wrote, or every pre-upgrade work session keeps leaking into the chat list.");
        AssertEx.Equal(NodeConversationKind.Chat, await ReadKindAsync(provider, plain.ConversationId).ConfigureAwait(false));
    }

    private static async Task<string?> ReadKindAsync(ServiceProvider provider, Guid conversationId)
    {
        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
        var connection = dbContext.Database.GetDbConnection();
        await dbContext.Database.OpenConnectionAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT kind FROM conversations WHERE conversation_id = $conversationId;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$conversationId";
        parameter.Value = conversationId;
        command.Parameters.Add(parameter);
        return Convert.ToString(await command.ExecuteScalarAsync().ConfigureAwait(false), CultureInfo.InvariantCulture);
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

    private static NodeChatPersistenceService CreateService(ServiceProvider provider) =>
        new(provider.GetRequiredService<NodeChatPersistenceWriter>());
}
