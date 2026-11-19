namespace XE_Local_AI_Engine.Tests.Chat;

using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The chat import's write, on a real database: one chat commits whole or not at all, its rows match what the
///     per-message writer produces, and its assistant replies are not counted as runs of this node.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class NodeChatImportPersistenceTests : IDisposable
{
    private const string ImportedModel = "imported-model";

    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    [Test]
    public async Task ImportedReplies_AreNoUsageRun_AfterTheRestartReconcile_WhileAGenuineZeroTokenRunCounts()
    {
        await using var provider = await BuildProviderAsync("import-usage.sqlite");
        var persistence = CreateService(provider);
        var imported = await persistence.ImportConversationAsync(Chat(archived: false, User("question", 11), Assistant("imported answer", 12)));
        await InsertGenuineRunAsync(persistence, createdAtUtc: 21);

        AssertEx.Equal("genuine-model:1:0", await UsageAsync(provider), "Only the reply written on this node is a run; the imported one is not.");

        _ = await CreateRecoveryService(provider).RecoverInterruptedMessagesAsync(recoveredAtUtc: 99);

        AssertEx.Equal("genuine-model:1:0", await UsageAsync(provider), "The restart reconcile adds no counted run for the imported reply.");
        AssertEx.Equal(expected: 0, await EnvelopeCountAsync(provider, imported), "The import writes no envelope and the reconcile backfills none for it.");
    }

    [Test]
    public async Task CappedSweep_AfterALargeImport_KeepsAGenuineRunsTokenTotals()
    {
        await using var provider = await BuildProviderAsync("import-capped-sweep.sqlite");
        var persistence = CreateService(provider);
        await InsertGenuineRunWithTokensAsync(persistence, createdAtUtc: 20, totalTokens: 125);
        // More imported replies than the cap, all newer than the genuine run: none may take a place in its retention bucket.
        _ = await persistence.ImportConversationAsync(Chat(archived: false, User("question", 29), Assistant("one", 30), Assistant("two", 31), Assistant("three", 32)));

        await using (var scope = provider.CreateAsyncScope())
        {
            var store = new AgentExecutionLogStore(scope.ServiceProvider.GetRequiredService<NodeChatDbContext>(), TimeProvider.System);
            AssertEx.Equal(expected: 0, await store.TrimToMaxPerAgentAsync(maxPerAgent: 1), "The agentless bucket holds only the genuine run's envelope.");
        }

        _ = await CreateRecoveryService(provider).RecoverInterruptedMessagesAsync(recoveredAtUtc: 99);

        AssertEx.Equal("genuine-model:1:125", await UsageAsync(provider), "The genuine run keeps its envelope and its token totals; the imported replies are no run.");
    }

    [Test]
    public async Task ABranchOfAnImportedChat_StaysNoUsageRun_AfterTheRestartReconcile()
    {
        await using var provider = await BuildProviderAsync("import-branch.sqlite");
        var persistence = CreateService(provider);
        var imported = await persistence.ImportConversationAsync(Chat(archived: false, User("question", 11), Assistant("imported answer", 12)));
        await InsertGenuineRunAsync(persistence, createdAtUtc: 21);
        var source = AssertEx.NotNull(await persistence.GetConversationAsync(imported));

        var branch = await persistence.BranchConversationAsync(new NodeChatBranchConversationRequest
        {
            ConversationId = imported,
            MessageId = source.Messages[^1].MessageId,
            CreatedAtUtc = 40
        });
        _ = await CreateRecoveryService(provider).RecoverInterruptedMessagesAsync(recoveredAtUtc: 99);

        AssertEx.Equal(expected: 2, branch.CopiedMessageCount);
        AssertEx.Equal(expected: 0, await EnvelopeCountAsync(provider, branch.BranchedConversationId), "The branch copy keeps the imported flag, so the reconcile backfills no envelope for it.");
        AssertEx.Equal("genuine-model:1:0", await UsageAsync(provider), "Only the genuine run counts; the branched copy of an imported reply is no run.");
    }

    [Test]
    public async Task Import_WhenAWriteFailsInsideAChat_LeavesNothingOfThatChat_AndKeepsTheOneCommittedBefore()
    {
        var databasePath = DatabasePath("import-atomic.sqlite");
        await using var provider = await BuildProviderAsync("import-atomic.sqlite");
        var persistence = CreateService(provider);
        _ = await persistence.ImportConversationAsync(Chat(archived: false, User("kept question", 11), Assistant("kept answer", 12)));
        // A real write failure after two messages of the chat are written.
        await CreateFailingThirdMessageTriggerAsync(databasePath);

        var failure = await AssertEx.ThrowsAsync<SqliteException>(() =>
            persistence.ImportConversationAsync(Chat(archived: true, User("lost question", 21), Assistant("lost answer", 22), User("lost follow-up", 23))));

        AssertEx.Contains(failure.Message, "injected import failure");
        AssertEx.Equal("conversations=1 messages=2 envelopes=0", await CountRowsAsync(databasePath),
            "A fresh connection sees the chat committed before and nothing of the one whose write failed.");
    }

    [Test]
    public async Task Import_WritesTheRowsThePerMessageWriterWrites_ExceptTheRunEnvelope()
    {
        var databasePath = DatabasePath("import-parity.sqlite");
        await using var provider = await BuildProviderAsync("import-parity.sqlite");
        var persistence = CreateService(provider);
        var request = Chat(archived: true, User("  paritymarker question  ", 11), Assistant(" parity answer ", 12), User("follow-up", 13));

        var bulk = await persistence.ImportConversationAsync(request);
        var perMessage = await WritePerMessageAsync(persistence, request);

        // Every column but the ids and the ciphertext matches the per-message writer.
        await AssertSameRowsAsync(databasePath, "conversations", perMessage, bulk, "conversation_id, title");
        await AssertSameRowsAsync(databasePath, "messages", perMessage, bulk, "message_id, conversation_id, content, metadata_json");
        AssertEx.Equal("1|0", $"{await EnvelopeCountAsync(provider, perMessage)}|{await EnvelopeCountAsync(provider, bulk)}",
            "The per-message writer gives the assistant reply a run envelope; the import, where no run happened, gives none.");

        AssertEx.Equal("FE01,FE01,FE01", await CiphertextHeadersAsync(databasePath, bulk), "Every imported message is stored as a content envelope, not plaintext.");
        // Each blob decrypts only under the ids of the row it sits in, so this read proves the binding as well as the text.
        AssertEx.Equal(ConversationText(AssertEx.NotNull(await persistence.GetConversationAsync(perMessage))), ConversationText(AssertEx.NotNull(await persistence.GetConversationAsync(bulk))),
            "The decrypted title and messages match.");
        var hits = await persistence.SearchConversationsAsync(new NodeChatSearchConversationsRequest
        {
            Query = "paritymarker",
            IncludeArchived = true,
            Limit = 10
        });
        AssertEx.Equal(string.Join(',', new[] { bulk, perMessage }.Order()), string.Join(',', hits.Items.Select(static hit => hit.ConversationId).Order()),
            "The import marks its chat stale like any write, so the next search finds it.");
    }

    // A finished reply written on this node, in a chat of its own: a thin envelope with no tokens, and still a run.
    private static async Task InsertGenuineRunAsync(NodeChatPersistenceService persistence, long createdAtUtc)
    {
        var genuine = await persistence.CreateConversationAsync(new NodeChatCreateConversationRequest
        {
            Title = "Published",
            UserId = null,
            CreatedAtUtc = createdAtUtc - 1
        });
        _ = await persistence.InsertMessageIfAbsentAsync(new NodeChatInsertMessageIfAbsentRequest
        {
            ConversationId = genuine.ConversationId,
            MessageId = Guid.NewGuid(),
            Role = "assistant",
            Content = "published answer",
            CreatedAtUtc = createdAtUtc,
            Model = "genuine-model"
        });
    }

    // A run on this node that reported its tokens, written through the pump's own terminalize path.
    private static async Task InsertGenuineRunWithTokensAsync(NodeChatPersistenceService persistence, long createdAtUtc, int totalTokens)
    {
        var genuine = await persistence.CreateConversationAsync(new NodeChatCreateConversationRequest
        {
            Title = "Run",
            UserId = null,
            CreatedAtUtc = createdAtUtc - 1
        });
        var correlation = new NodeChatMessageCorrelation
        {
            ConversationId = genuine.ConversationId,
            MessageId = Guid.NewGuid(),
            RequestId = Guid.NewGuid()
        };
        _ = await persistence.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest
        {
            ConversationId = correlation.ConversationId,
            MessageId = correlation.MessageId,
            RequestId = correlation.RequestId,
            CreatedAtUtc = createdAtUtc
        });
        _ = await persistence.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest
        {
            Correlation = correlation,
            Status = NodeChatMessageStatusValues.Completed,
            UpdatedAtUtc = createdAtUtc,
            Content = "answer",
            Model = "genuine-model",
            TotalCount = totalTokens,
            Envelope = new AgentRunEnvelopeMetadata
            {
                InvocationId = Guid.NewGuid(),
                DurationMs = 100L
            }
        });
    }

    private static async Task<int> EnvelopeCountAsync(ServiceProvider provider, Guid conversationId)
    {
        await using var scope = provider.CreateAsyncScope();
        var store = new AgentExecutionLogStore(scope.ServiceProvider.GetRequiredService<NodeChatDbContext>(), TimeProvider.System);
        return (await store.ListRunEnvelopesAsync(conversationId, limit: 100)).Count;
    }

    private static NodeChatRestartRecoveryService CreateRecoveryService(ServiceProvider provider)
    {
        var settings = Substitute.For<INodeSettingsStore>();
        settings.LoadAsync(Arg.Any<CancellationToken>()).Returns(new StoredNodeSettings());
        return new NodeChatRestartRecoveryService(provider.GetRequiredService<NodeChatPersistenceWriter>(), settings, UnknownUsageProviderResolver.Instance);
    }

    // The usage summary as "model:runs:total tokens" buckets.
    private static async Task<string> UsageAsync(ServiceProvider provider)
    {
        await using var scope = provider.CreateAsyncScope();
        var store = new AgentExecutionLogStore(scope.ServiceProvider.GetRequiredService<NodeChatDbContext>(), TimeProvider.System);
        var usage = await store.SummarizeTokenUsageAsync(fromEpochMsInclusive: null, toEpochMsExclusive: null);
        return string.Join('|', usage.Select(static bucket => $"{bucket.ModelName}:{bucket.RunCount}:{bucket.TotalTokens}"));
    }

    // The path the import took before it was one transaction: create, one if-absent insert per message, archive.
    private static async Task<Guid> WritePerMessageAsync(NodeChatPersistenceService persistence, NodeChatImportConversationRequest request)
    {
        var created = await persistence.CreateConversationAsync(new NodeChatCreateConversationRequest
        {
            Title = request.Title,
            UserId = null,
            CreatedAtUtc = request.CreatedAtUtc
        });
        foreach (var message in request.Messages)
        {
            _ = await persistence.InsertMessageIfAbsentAsync(new NodeChatInsertMessageIfAbsentRequest
            {
                ConversationId = created.ConversationId,
                MessageId = Guid.NewGuid(),
                Role = message.Role,
                Content = message.Content,
                CreatedAtUtc = message.CreatedAtUtc,
                Model = message.Model
            });
        }

        _ = await persistence.SetConversationArchivedAsync(new NodeChatSetConversationArchivedRequest
        {
            ConversationId = created.ConversationId,
            Archived = request.Archived,
            UpdatedAtUtc = request.Messages[^1].CreatedAtUtc
        });
        return created.ConversationId;
    }

    private static string ConversationText(NodeChatConversationDto conversation) =>
        $"{conversation.Title}|{conversation.Archived}|{conversation.LastSeenUtc}|"
        + string.Join('|', conversation.Messages.Select(static message =>
            $"{message.Sequence}:{message.Role}:{message.Content}:{message.Model}:{message.Status}:{message.CreatedAtUtc}:{message.UpdatedAtUtc}:{message.Origin}:{message.MetadataJson}"));

    private static NodeChatImportConversationRequest Chat(bool archived, params NodeChatImportMessage[] messages) =>
        new()
        {
            Title = "Imported chat",
            CreatedAtUtc = 10,
            Archived = archived,
            Messages = messages
        };

    private static NodeChatImportMessage User(string content, long createdAtUtc) =>
        new()
        {
            Role = "user",
            Content = content,
            CreatedAtUtc = createdAtUtc
        };

    private static NodeChatImportMessage Assistant(string content, long createdAtUtc) =>
        new()
        {
            Role = "assistant",
            Content = content,
            CreatedAtUtc = createdAtUtc,
            Model = ImportedModel
        };

    private static async Task AssertSameRowsAsync(string databasePath, string table, Guid expected, Guid actual, string excludedColumns) =>
        AssertEx.Equal(await DumpRowsAsync(databasePath, table, expected, excludedColumns), await DumpRowsAsync(databasePath, table, actual, excludedColumns),
            $"The {table} rows match the per-message writer's.");

    // Every column of the conversation's rows in sequence order, minus the named ones, as "column=value" text.
    private static async Task<string> DumpRowsAsync(string databasePath, string table, Guid conversationId, string excludedColumns)
    {
        var excluded = excludedColumns.Split(',', StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
        await using var connection = await OpenAsync(databasePath);
        await using var command = connection.CreateCommand();
        // One constant statement per table, so no SQL text is ever built from a value.
        if (table == "conversations")
        {
            command.CommandText = "SELECT * FROM conversations WHERE conversation_id = $id;";
        }
        else
        {
            command.CommandText = "SELECT * FROM messages WHERE conversation_id = $id ORDER BY sequence;";
        }

        command.Parameters.AddWithValue("$id", conversationId);
        await using var reader = await command.ExecuteReaderAsync();
        var text = new StringBuilder();
        while (await reader.ReadAsync())
        {
            for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
            {
                if (!excluded.Contains(reader.GetName(ordinal)))
                {
                    text.Append(CultureInfo.InvariantCulture, $"{reader.GetName(ordinal)}={Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture)};");
                }
            }

            text.Append('\n');
        }

        AssertEx.True(text.Length > 0, $"The chat must have {table} rows to compare.");
        return text.ToString();
    }

    private static async Task<string?> CountRowsAsync(string databasePath)
    {
        await using var connection = await OpenAsync(databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT 'conversations=' || (SELECT COUNT(*) FROM conversations)
                                  || ' messages=' || (SELECT COUNT(*) FROM messages)
                                  || ' envelopes=' || (SELECT COUNT(*) FROM agent_execution_logs);
                              """;
        return (string?)await command.ExecuteScalarAsync();
    }

    private static async Task<string?> CiphertextHeadersAsync(string databasePath, Guid conversationId)
    {
        await using var connection = await OpenAsync(databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT group_concat(header, ',') FROM (SELECT hex(substr(content, 1, 2)) AS header FROM messages WHERE conversation_id = $id ORDER BY sequence);";
        command.Parameters.AddWithValue("$id", conversationId);
        return (string?)await command.ExecuteScalarAsync();
    }

    // Fails the insert of any chat's third message, inside that chat's write.
    private static async Task CreateFailingThirdMessageTriggerAsync(string databasePath)
    {
        await using var connection = await OpenAsync(databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER fail_third_message BEFORE INSERT ON messages WHEN NEW.sequence = 2 BEGIN SELECT RAISE(ABORT, 'injected import failure'); END;";
        _ = await command.ExecuteNonQueryAsync();
    }

    // A connection of its own, so a read sees only what committed.
    private static async Task<SqliteConnection> OpenAsync(string databasePath)
    {
        var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        return connection;
    }

    private async Task<ServiceProvider> BuildProviderAsync(string fileName)
    {
        var databasePath = DatabasePath(fileName);
        var services = new ServiceCollection();
        // A real key, so content is ciphertext and a blob bound to the wrong row ids fails to decrypt on read.
        services.AddSingleton<INodeSqliteKeyHolder>(NodeChatStreamServiceTestSupport.FenceKeyHolder);
        services.AddDbContext<NodeChatDbContext>(options => options.UseSqlite($"Data Source={databasePath}"));
        services.AddSingleton<NodeChatPersistenceWriter>();

        var provider = services.BuildServiceProvider(true);
        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
        await dbContext.Database.EnsureDeletedAsync();
        await dbContext.Database.EnsureCreatedAsync();

        return provider;
    }

    // Attributes the imported model to a real provider, so the comparison covers the envelope's provider too.
    private static NodeChatPersistenceService CreateService(ServiceProvider provider)
    {
        var resolver = Substitute.For<IUsageProviderResolver>();
        resolver.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(call => call.ArgAt<string?>(0) == ImportedModel ? AgentUsageProviders.Local : AgentUsageProviders.Unknown);
        return new NodeChatPersistenceService(provider.GetRequiredService<NodeChatPersistenceWriter>(), new Lazy<IUsageProviderResolver>(resolver));
    }

    private string DatabasePath(string fileName)
    {
        Directory.CreateDirectory(_rootPath);
        return Path.Combine(_rootPath, fileName);
    }
}
