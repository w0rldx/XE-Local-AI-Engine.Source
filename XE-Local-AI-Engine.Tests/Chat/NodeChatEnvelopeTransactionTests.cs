namespace XE_Local_AI_Engine.Tests.Chat;

using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Real-SQLite transaction/rollback and purge-race coverage for the atomic run-envelope write (MED-007 / R4). The
///     terminalize message UPDATE and its content-free envelope INSERT commit or roll back together, and a conversation
///     purge racing a terminalize can never strand an orphaned envelope carrying the conversation's plaintext ids —
///     whichever operation the per-conversation lock hierarchy lets win.
/// </summary>
public sealed class NodeChatEnvelopeTransactionTests : IDisposable
{
    private const int RaceIterations = 64;
    private const int EnvelopeKind = (int)AgentExecutionLogRecordKind.ChatRunEnvelope;

    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    [Test]
    public async Task Terminalize_WhenEnvelopeInsertFails_RollsBackTheMessageUpdate()
    {
        await using var provider = await BuildProviderAsync("envelope-insert-rollback.sqlite").ConfigureAwait(false);
        var persistence = CreateService(provider);
        var conversation = await persistence.CreateConversationAsync(new NodeChatCreateConversationRequest("Rollback", "node", CreatedAtUtc: 1)).ConfigureAwait(false);
        var correlation = await CreatePlaceholderAsync(persistence, conversation.ConversationId).ConfigureAwait(false);
        await persistence.MarkAssistantStreamingAsync(correlation, updatedAtUtc: 2).ConfigureAwait(false);

        // Sabotage ONLY the envelope insert with a BEFORE INSERT trigger that aborts run-envelope rows (no production
        // seam; the trigger keys on the same record_kind the envelope write uses). The terminalize UPDATE and the
        // envelope INSERT share one transaction, so the abort must undo the terminal UPDATE as well.
        await InstallEnvelopeSabotageTriggerAsync(provider).ConfigureAwait(false);

        await AssertEx.ThrowsAsync<SqliteException>(async () =>
                _ = await persistence.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest(correlation,
                        NodeChatMessageStatusValues.Completed,
                        UpdatedAtUtc: 3,
                        "answer",
                        Envelope: new AgentRunEnvelopeMetadata(Guid.NewGuid(), DurationMs: 5L)))
                    .ConfigureAwait(false))
            .ConfigureAwait(false);

        // The whole transaction rolled back: the row is still non-terminal (streaming) and no envelope was written.
        AssertEx.Equal(NodeChatMessageStatusValues.Streaming, await ReadStatusAsync(provider, correlation).ConfigureAwait(false));
        AssertEx.Equal(expected: 0, await CountEnvelopesAsync(provider).ConfigureAwait(false));
    }

    [Test]
    public async Task Terminalize_AfterConversationPurged_ThrowsAndLeavesNoRows()
    {
        // Deterministic "purge wins" ordering: the conversation (and its message) is fully purged before terminalize
        // runs. Terminalize finds no row and throws — it can never resurrect an orphaned envelope for a purged conversation.
        await using var provider = await BuildProviderAsync("purge-then-terminalize.sqlite").ConfigureAwait(false);
        var persistence = CreateService(provider);
        var conversation = await persistence.CreateConversationAsync(new NodeChatCreateConversationRequest("PurgeWins", "node", CreatedAtUtc: 1)).ConfigureAwait(false);
        var correlation = await CreatePlaceholderAsync(persistence, conversation.ConversationId).ConfigureAwait(false);
        await persistence.MarkAssistantStreamingAsync(correlation, updatedAtUtc: 2).ConfigureAwait(false);

        await persistence.DeleteConversationAsync(new NodeChatDeleteConversationRequest(conversation.ConversationId, DeletedAtUtc: 3, PurgeImmediately: true)).ConfigureAwait(false);

        await AssertEx.ThrowsAsync<InvalidOperationException>(async () =>
                _ = await persistence.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest(correlation,
                        NodeChatMessageStatusValues.Completed,
                        UpdatedAtUtc: 4,
                        "answer",
                        Envelope: new AgentRunEnvelopeMetadata(Guid.NewGuid(), DurationMs: 5L)))
                    .ConfigureAwait(false))
            .ConfigureAwait(false);

        AssertEx.Null(await persistence.GetConversationAsync(conversation.ConversationId).ConfigureAwait(false));
        AssertEx.Equal(expected: 0, await CountMessagesAsync(provider, conversation.ConversationId).ConfigureAwait(false));
        AssertEx.Equal(expected: 0, await CountEnvelopesAsync(provider).ConfigureAwait(false));
    }

    [Test]
    public async Task Terminalize_ThenConversationPurged_RemovesTheEnvelope()
    {
        // Deterministic "terminalize wins" ordering: terminalize commits the terminal row AND its envelope, then the
        // purge deletes the conversation footprint — including the envelope — so nothing carrying plaintext ids survives.
        await using var provider = await BuildProviderAsync("terminalize-then-purge.sqlite").ConfigureAwait(false);
        var persistence = CreateService(provider);
        var conversation = await persistence.CreateConversationAsync(new NodeChatCreateConversationRequest("TerminalizeWins", "node", CreatedAtUtc: 1)).ConfigureAwait(false);
        var correlation = await CreatePlaceholderAsync(persistence, conversation.ConversationId).ConfigureAwait(false);
        await persistence.MarkAssistantStreamingAsync(correlation, updatedAtUtc: 2).ConfigureAwait(false);

        await persistence.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest(correlation,
                NodeChatMessageStatusValues.Completed,
                UpdatedAtUtc: 3,
                "answer",
                Envelope: new AgentRunEnvelopeMetadata(Guid.NewGuid(), DurationMs: 5L)))
            .ConfigureAwait(false);
        AssertEx.Equal(expected: 1, await CountEnvelopesAsync(provider).ConfigureAwait(false));

        await persistence.DeleteConversationAsync(new NodeChatDeleteConversationRequest(conversation.ConversationId, DeletedAtUtc: 4, PurgeImmediately: true)).ConfigureAwait(false);

        AssertEx.Null(await persistence.GetConversationAsync(conversation.ConversationId).ConfigureAwait(false));
        AssertEx.Equal(expected: 0, await CountMessagesAsync(provider, conversation.ConversationId).ConfigureAwait(false));
        AssertEx.Equal(expected: 0, await CountEnvelopesAsync(provider).ConfigureAwait(false));
    }

    [Test]
    public async Task PurgeVersusTerminalize_RacingUnderTheRealLock_NeverOrphansAnEnvelope()
    {
        // Coordinated race: a purge and an enveloped terminalize on the same conversation are gated on ONE
        // TaskCompletionSource and released together, so they genuinely contend for the per-conversation lock (purge
        // takes it exclusive, terminalize shared + per-message) with no sleeps deciding the winner. Across iterations the
        // lock arbitrates both orderings; the invariant must hold every time: the purge removes the whole footprint and
        // no envelope is ever left orphaned, whether terminalize committed first (then purge deleted it) or purge won
        // first (then terminalize found no row and threw).
        await using var provider = await BuildProviderAsync("purge-terminalize-race.sqlite").ConfigureAwait(false);
        var service = CreateService(provider);

        var terminalizeWon = 0;
        var purgeWon = 0;

        for (var iteration = 0; iteration < RaceIterations; iteration++)
        {
            var conversation = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Race", "node", CreatedAtUtc: iteration)).ConfigureAwait(false);
            var correlation = await CreatePlaceholderAsync(service, conversation.ConversationId).ConfigureAwait(false);
            await service.MarkAssistantStreamingAsync(correlation, updatedAtUtc: 2).ConfigureAwait(false);

            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var terminalize = Task.Run(async () =>
            {
                await gate.Task.ConfigureAwait(false);
                try
                {
                    await service.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest(correlation,
                            NodeChatMessageStatusValues.Completed,
                            UpdatedAtUtc: 10,
                            "answer",
                            Envelope: new AgentRunEnvelopeMetadata(Guid.NewGuid(), DurationMs: 5L)))
                        .ConfigureAwait(false);
                    return true;
                }
                catch (InvalidOperationException)
                {
                    // The purge deleted the message row before terminalize acquired its locks and read it.
                    return false;
                }
            });

            var purge = Task.Run(async () =>
            {
                await gate.Task.ConfigureAwait(false);
                await service.DeleteConversationAsync(new NodeChatDeleteConversationRequest(conversation.ConversationId, DeletedAtUtc: 20, PurgeImmediately: true)).ConfigureAwait(false);
            });

            gate.SetResult();
            await Task.WhenAll(terminalize, purge).ConfigureAwait(false);

            if (await terminalize.ConfigureAwait(false))
            {
                terminalizeWon++;
            }
            else
            {
                purgeWon++;
            }

            // Safety invariant, whichever operation won the lock: the conversation footprint is gone and NO envelope row
            // survives to orphan the plaintext conversation/message correlation.
            AssertEx.Null(await service.GetConversationAsync(conversation.ConversationId).ConfigureAwait(false));
            AssertEx.Equal(expected: 0, await CountMessagesAsync(provider, conversation.ConversationId).ConfigureAwait(false));
            AssertEx.Equal(expected: 0, await CountEnvelopesAsync(provider).ConfigureAwait(false));
        }

        // The gated race actually exercised BOTH lock-arbitration orderings, so the invariant above is proven for each.
        AssertEx.True(terminalizeWon > 0, "The race never let terminalize commit before the purge — increase iterations or investigate lock ordering.");
        AssertEx.True(purgeWon > 0, "The race never let the purge win before terminalize — increase iterations or investigate lock ordering.");
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

    private static async Task<NodeChatMessageCorrelation> CreatePlaceholderAsync(NodeChatPersistenceService persistence, Guid conversationId)
    {
        var correlation = new NodeChatMessageCorrelation(conversationId, Guid.NewGuid(), Guid.NewGuid());
        await persistence.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(conversationId, correlation.MessageId, correlation.RequestId, CreatedAtUtc: 1))
                         .ConfigureAwait(false);
        return correlation;
    }

    // Installs a BEFORE INSERT trigger that aborts any run-envelope row. Schema-level, so it applies to the envelope
    // INSERT regardless of which connection/transaction the writer runs it on.
    private static async Task InstallEnvelopeSabotageTriggerAsync(ServiceProvider provider)
    {
        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
        // EnvelopeKind is a trusted compile-time constant (not user input); build the DDL as a plain string so the
        // trigger's WHEN clause carries the literal record-kind value a run envelope uses.
        var triggerSql = "CREATE TRIGGER sabotage_envelope_insert BEFORE INSERT ON agent_execution_logs WHEN NEW.record_kind = "
                         + EnvelopeKind.ToString(CultureInfo.InvariantCulture)
                         + " BEGIN SELECT RAISE(ABORT, 'sabotaged envelope insert'); END;";
        await dbContext.Database.ExecuteSqlRawAsync(triggerSql).ConfigureAwait(false);
    }

    private static async Task<string> ReadStatusAsync(ServiceProvider provider, NodeChatMessageCorrelation correlation)
    {
        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
        var connection = dbContext.Database.GetDbConnection();
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT status FROM messages WHERE conversation_id = $conversation_id AND message_id = $message_id;";
        AddParameter(command, "$conversation_id", correlation.ConversationId);
        AddParameter(command, "$message_id", correlation.MessageId);
        var status = await command.ExecuteScalarAsync().ConfigureAwait(false);
        return status as string ?? throw new InvalidOperationException("The message row was not found.");
    }

    private static async Task<long> CountMessagesAsync(ServiceProvider provider, Guid conversationId)
    {
        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
        var connection = dbContext.Database.GetDbConnection();
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM messages WHERE conversation_id = $conversation_id;";
        AddParameter(command, "$conversation_id", conversationId);
        return Convert.ToInt64(await command.ExecuteScalarAsync().ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private static async Task<long> CountEnvelopesAsync(ServiceProvider provider)
    {
        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
        var connection = dbContext.Database.GetDbConnection();
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM agent_execution_logs WHERE record_kind = $record_kind;";
        AddParameter(command, "$record_kind", EnvelopeKind);
        return Convert.ToInt64(await command.ExecuteScalarAsync().ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
