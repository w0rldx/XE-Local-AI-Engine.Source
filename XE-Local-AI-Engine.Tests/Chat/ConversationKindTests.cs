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
[Category(TestCategories.Integration)]
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
        await using var provider = await BuildProviderAsync($"kind-list-{includeArchived}.sqlite");
        var service = CreateService(provider);

        var chat = await service.CreateConversationAsync(new NodeChatCreateConversationRequest
        {
            Title = "A chat",
            UserId = "node",
            CreatedAtUtc = 10
        });
        var workSession = await service
            .CreateConversationAsync(new NodeChatCreateConversationRequest
            {
                Title = "Work session",
                UserId = "node",
                CreatedAtUtc = 20,
                Kind = NodeConversationKind.WorkSession
            });
        var integration = await service
            .CreateConversationAsync(new NodeChatCreateConversationRequest
            {
                Title = "Integration",
                UserId = "node",
                CreatedAtUtc = 30,
                Kind = NodeConversationKind.Integration
            });
        var scheduled = await service
            .CreateConversationAsync(new NodeChatCreateConversationRequest
            {
                Title = "Scheduled answer",
                UserId = null,
                CreatedAtUtc = 40,
                Kind = NodeConversationKind.Scheduled
            });

        var listed = (await service.ListConversationsAsync(new NodeChatListConversationsRequest
                     {
                         IncludeArchived = includeArchived
                     }))
                     .Select(static summary => summary.ConversationId)
                     .ToArray();

        AssertEx.True(listed.Contains(chat.ConversationId), "An ordinary chat must still appear in the chat list.");
        AssertEx.False(listed.Contains(workSession.ConversationId),
            "A work session's owned transcript must not appear as a chat the operator did not start — that is the leak the kind column closes.");
        AssertEx.False(listed.Contains(integration.ConversationId), "An integration session's owned transcript must not appear in the chat list either.");
        AssertEx.False(listed.Contains(scheduled.ConversationId), "A scheduled run's answer opens from its run, never from the chat sidebar (ADR 0024).");
    }

    [Test]
    public async Task ScheduledAnswer_IsStoredEncryptedAndStillReadableById()
    {
        // The handler writes through this same service; what proves the answer is protected is the stored bytes.
        await using var provider = await BuildProviderAsync("kind-scheduled-encrypted.sqlite");
        var service = CreateService(provider);
        const string Answer = "Two disk warnings overnight, both on the backup volume.";
        var runId = Guid.NewGuid();
        _ = await service.CreateConversationAsync(new NodeChatCreateConversationRequest
        {
            Title = "Nightly log summary",
            UserId = null,
            CreatedAtUtc = 10,
            Kind = NodeConversationKind.Scheduled,
            ConversationId = runId
        });
        _ = await service.InsertMessageIfAbsentAsync(new NodeChatInsertMessageIfAbsentRequest
        {
            ConversationId = runId,
            MessageId = Guid.NewGuid(),
            Role = "assistant",
            Content = Answer,
            CreatedAtUtc = 11
        });

        await using (var scope = provider.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
            await dbContext.Database.OpenConnectionAsync();
            await using var command = dbContext.Database.GetDbConnection().CreateCommand();
            command.CommandText = "SELECT content FROM messages;";
            var stored = AssertEx.NotNull(await command.ExecuteScalarAsync() as byte[], "The message content column holds a blob.");
            AssertEx.False(stored.AsSpan().IndexOf(System.Text.Encoding.UTF8.GetBytes("disk warnings")) >= 0, "The answer must be encrypted at rest like any chat message.");
        }

        var loaded = AssertEx.NotNull(await service.GetConversationAsync(runId), "The run detail opens the answer by id.");
        AssertEx.Equal(Answer, loaded.Messages.Single().Content);
    }

    [Test]
    public async Task OperatorMutations_OnAScheduledAnswer_AreRefusedAsReadOnly_ButDeleteStillRemovesIt()
    {
        // ADR 0024: a scheduled answer is a record of what the run said, so nothing may edit it; the run viewer may delete it.
        await using var provider = await BuildProviderAsync("kind-scheduled-read-only.sqlite");
        var service = CreateService(provider);
        var runId = Guid.NewGuid();
        _ = await service.CreateConversationAsync(new NodeChatCreateConversationRequest
        {
            Title = "Nightly log summary",
            UserId = null,
            CreatedAtUtc = 10,
            Kind = NodeConversationKind.Scheduled,
            ConversationId = runId
        });
        var answerId = Guid.NewGuid();
        _ = await service.InsertMessageIfAbsentAsync(new NodeChatInsertMessageIfAbsentRequest
        {
            ConversationId = runId,
            MessageId = answerId,
            Role = "assistant",
            Content = "All quiet.",
            CreatedAtUtc = 11
        });

        var mutations = new Dictionary<string, Func<Task>>(StringComparer.Ordinal)
        {
            ["RenameConversation"] = () => service.RenameConversationAsync(new NodeChatRenameConversationRequest
            {
                ConversationId = runId,
                Title = "renamed",
                UpdatedAtUtc = 20
            }),
            ["SetConversationPinned"] = () => service.SetConversationPinnedAsync(new NodeChatSetConversationPinnedRequest
            {
                ConversationId = runId,
                IsPinned = true,
                UpdatedAtUtc = 20
            }),
            ["SetConversationArchived"] = () => service.SetConversationArchivedAsync(new NodeChatSetConversationArchivedRequest
            {
                ConversationId = runId,
                Archived = true,
                UpdatedAtUtc = 20
            }),
            ["SetConversationMemoryExcluded"] = () => service.SetConversationMemoryExcludedAsync(new NodeChatSetConversationMemoryExcludedRequest
            {
                ConversationId = runId,
                MemoryExcluded = true,
                UpdatedAtUtc = 20
            }),
            ["SetConversationPlain"] = () => service.SetConversationPlainAsync(new NodeChatSetConversationPlainRequest
            {
                ConversationId = runId,
                IsPlain = true,
                PlainSystemPrompt = null,
                UpdatedAtUtc = 20
            }),
            ["SetSelectedPath"] = () => service.SetSelectedPathAsync(new NodeChatSetSelectedPathRequest
            {
                ConversationId = runId,
                SelectedPath = null,
                UpdatedAtUtc = 20
            }),
            ["BranchConversation"] = () => service.BranchConversationAsync(new NodeChatBranchConversationRequest
            {
                ConversationId = runId,
                MessageId = answerId,
                CreatedAtUtc = 20
            }),
            ["CreateMessageVariant"] = () => service.CreateMessageVariantAsync(new NodeChatCreateMessageVariantRequest
            {
                ConversationId = runId,
                OriginalMessageId = answerId,
                NewMessageId = Guid.NewGuid(),
                RequestId = Guid.NewGuid(),
                CreatedAtUtc = 20
            }),
            ["SetMessageFeedback"] = () => service.SetMessageFeedbackAsync(new NodeChatSetMessageFeedbackRequest
            {
                ConversationId = runId,
                MessageId = answerId,
                Rating = "up",
                Comment = null,
                UpdatedAtUtc = 20
            })
        };

        foreach (var (name, call) in mutations)
        {
            var rejection = await AssertEx.ThrowsAsync<NodeChatReadOnlyConversationException>(call, $"{name} must refuse a scheduled answer.");
            AssertEx.Equal(runId, rejection.ConversationId);
        }

        var untouched = AssertEx.NotNull(await service.GetConversationAsync(runId));
        AssertEx.Equal("Nightly log summary", untouched.Title);
        AssertEx.False(untouched.IsPinned);

        _ = await service.DeleteConversationAsync(new NodeChatDeleteConversationRequest
        {
            ConversationId = runId,
            DeletedAtUtc = 30,
            PurgeImmediately = true
        });
        AssertEx.Null(await service.GetConversationAsync(runId), "The run viewer's delete must still remove the answer.");
    }

    [Test]
    public async Task Rename_OfAWorkSessionTranscript_IsNotRefusedByTheScheduledAnswerGuard()
    {
        // The read-only rule is ADR 0024's for scheduled answers only; work-session and integration rows keep their behavior.
        await using var provider = await BuildProviderAsync("kind-work-session-mutable.sqlite");
        var service = CreateService(provider);
        var workSession = await service.CreateConversationAsync(new NodeChatCreateConversationRequest
        {
            Title = "Work session",
            UserId = "node",
            CreatedAtUtc = 10,
            Kind = NodeConversationKind.WorkSession
        });

        var renamed = await service.RenameConversationAsync(new NodeChatRenameConversationRequest
        {
            ConversationId = workSession.ConversationId,
            Title = "renamed",
            UpdatedAtUtc = 20
        });

        AssertEx.Equal("renamed", AssertEx.NotNull(renamed).Title);
    }

    [Test]
    public async Task GetConversation_StillReturnsANonChatConversationById()
    {
        await using var provider = await BuildProviderAsync("kind-by-id.sqlite");
        var service = CreateService(provider);

        var integration = await service
            .CreateConversationAsync(new NodeChatCreateConversationRequest
            {
                Title = "Integration",
                UserId = "node",
                CreatedAtUtc = 10,
                Kind = NodeConversationKind.Integration
            });

        var loaded = AssertEx.NotNull(await service.GetConversationAsync(integration.ConversationId),
            "By-id reads stay unfiltered: the session transcript readers legitimately load these rows.");
        AssertEx.Equal(integration.ConversationId, loaded.ConversationId);
    }

    [Test]
    public async Task CreateConversation_DefaultsToChatAndHonoursAnExplicitKind()
    {
        await using var provider = await BuildProviderAsync("kind-default.sqlite");
        var service = CreateService(provider);

        var defaulted = await service.CreateConversationAsync(new NodeChatCreateConversationRequest
        {
            Title = "Default",
            UserId = "node",
            CreatedAtUtc = 10
        });
        var explicitKind = await service
            .CreateConversationAsync(new NodeChatCreateConversationRequest
            {
                Title = "Explicit",
                UserId = "node",
                CreatedAtUtc = 20,
                Kind = NodeConversationKind.WorkSession
            });

        AssertEx.Equal(NodeConversationKind.Chat, await ReadKindAsync(provider, defaulted.ConversationId));
        AssertEx.Equal(NodeConversationKind.WorkSession, await ReadKindAsync(provider, explicitKind.ConversationId));
    }

    [Test]
    public async Task CreateConversation_UsesACallerSuppliedIdAndStillMintsOneWhenNoneIsGiven()
    {
        await using var provider = await BuildProviderAsync("kind-supplied-id.sqlite");
        var service = CreateService(provider);
        var preMinted = Guid.NewGuid();

        var supplied = await service
            .CreateConversationAsync(new NodeChatCreateConversationRequest
            {
                Title = "Pre-minted",
                UserId = "node",
                CreatedAtUtc = 10,
                Kind = NodeConversationKind.Integration,
                ConversationId = preMinted
            });

        AssertEx.Equal(preMinted, supplied.ConversationId, "The integration accept path commits its rows first and creates the conversation at the id they carry.");
        var loaded = AssertEx.NotNull(await service.GetConversationAsync(preMinted), "The stored row must be readable at the caller's id.");
        AssertEx.Equal(preMinted, loaded.ConversationId);

        // The regression guard: every existing caller passes nothing, and a botched `??` would mint over a supplied id
        // or, worse, insert Guid.Empty.
        var minted = await service.CreateConversationAsync(new NodeChatCreateConversationRequest
        {
            Title = "Minted",
            UserId = "node",
            CreatedAtUtc = 20
        });
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
        await using var provider = await BuildProviderAsync("kind-backfill-join.sqlite");
        var service = CreateService(provider);

        var owned = await service.CreateConversationAsync(new NodeChatCreateConversationRequest
        {
            Title = "Owned",
            UserId = "node",
            CreatedAtUtc = 10
        });
        var plain = await service.CreateConversationAsync(new NodeChatCreateConversationRequest
        {
            Title = "Plain",
            UserId = "node",
            CreatedAtUtc = 20
        });

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
                owned.ConversationId);

            _ = await dbContext.Database.ExecuteSqlRawAsync("""
                                                            UPDATE conversations
                                                            SET kind = 'work-session'
                                                            WHERE conversation_id IN (SELECT conversation_id FROM agent_work_sessions);
                                                            """);
        }

        AssertEx.Equal(NodeConversationKind.WorkSession, await ReadKindAsync(provider, owned.ConversationId),
            "The backfill must reach a conversation the chat path wrote, or every pre-upgrade work session keeps leaking into the chat list.");
        AssertEx.Equal(NodeConversationKind.Chat, await ReadKindAsync(provider, plain.ConversationId));
    }

    private static async Task<string?> ReadKindAsync(ServiceProvider provider, Guid conversationId)
    {
        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
        var connection = dbContext.Database.GetDbConnection();
        await dbContext.Database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT kind FROM conversations WHERE conversation_id = $conversationId;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$conversationId";
        parameter.Value = conversationId;
        command.Parameters.Add(parameter);
        return Convert.ToString(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
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
        await dbContext.Database.EnsureDeletedAsync();
        await dbContext.Database.EnsureCreatedAsync();

        return provider;
    }

    private static NodeChatPersistenceService CreateService(ServiceProvider provider) =>
        new(provider.GetRequiredService<NodeChatPersistenceWriter>(), UnknownUsageProviderResolver.Instance);
}
