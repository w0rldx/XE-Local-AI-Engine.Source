namespace XE_Local_AI_Engine.Tests.Chat;

using System.Data.Common;
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
///     Real-SQLite transaction/rollback and purge-race coverage for the atomic run-envelope write. The
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
        // One invariant, proven two complementary ways: a purge and an enveloped terminalize on the same conversation
        // never leave an orphaned envelope, whichever wins the per-conversation lock (purge exclusive, terminalize shared
        // + per-message).
        //   1. Genuine contention: RaceIterations rounds gate a purge and a terminalize on ONE TaskCompletionSource and
        //      release them together, so they truly contend for the lock with no sleeps deciding the winner; the safety
        //      invariant is asserted every round under real concurrency.
        //   2. Deterministic ordering coverage: a scheduler can, on some machines/loads, arbitrate every gated round the
        //      SAME way, which used to flake the "both orderings observed" check. So the two orderings are ALSO forced
        //      explicitly — the intended winner is awaited to completion before the loser starts, so the lock arbitrates
        //      the intended branch instead of the scheduler — proving BOTH terminalize-then-purge and
        //      purge-then-terminalize on every run. There is no production seam to force the interleaving inside a single
        //      gated round, so forcing it across dedicated rounds is the deterministic equivalent.
        await using var provider = await BuildProviderAsync("purge-terminalize-race.sqlite").ConfigureAwait(false);
        var service = CreateService(provider);

        var terminalizeWon = 0;
        var purgeWon = 0;

        for (var iteration = 0; iteration < RaceIterations; iteration++)
        {
            var correlation = await SeedStreamingConversationAsync(service, iteration).ConfigureAwait(false);

            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var terminalize = Task.Run(async () =>
            {
                await gate.Task.ConfigureAwait(false);
                return await TryTerminalizeAsync(service, correlation).ConfigureAwait(false);
            });

            var purge = Task.Run(async () =>
            {
                await gate.Task.ConfigureAwait(false);
                await PurgeAsync(service, correlation.ConversationId).ConfigureAwait(false);
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
            await AssertNoOrphanedFootprintAsync(service, provider, correlation.ConversationId).ConfigureAwait(false);
        }

        // Deterministic backstop: force each ordering through the real lock (winner run to completion first) so both
        // lock-arbitration branches are exercised on every run regardless of how the scheduler happened to arbitrate the
        // gated rounds above. The same safety invariant must hold for each forced ordering.
        terminalizeWon += await AssertForcedOrderingAsync(service, provider, terminalizeFirst: true, iteration: RaceIterations).ConfigureAwait(false);
        purgeWon += await AssertForcedOrderingAsync(service, provider, terminalizeFirst: false, iteration: RaceIterations + 1).ConfigureAwait(false);

        // Both lock-arbitration orderings were exercised (the forced backstop guarantees this deterministically; the
        // gated rounds typically add more of each), so the invariant above is proven for each.
        AssertEx.True(terminalizeWon > 0, "Neither the gated race nor the forced backstop let terminalize commit before the purge.");
        AssertEx.True(purgeWon > 0, "Neither the gated race nor the forced backstop let the purge win before terminalize.");
    }

    [Test]
    public async Task Terminalize_WritesResolvedProviderOntoEnvelopeRow()
    {
        // Row-level round-trip of the fine-grained provider dimension: a terminalize carrying a resolved provider
        // must persist it onto the run-envelope row via WriteRunEnvelopeRowAsync — not fall back to the 'unknown' column
        // default. The pump resolves this label; here it rides in directly so the write path is proven end to end.
        await using var provider = await BuildProviderAsync("envelope-provider-roundtrip.sqlite").ConfigureAwait(false);
        var persistence = CreateService(provider);
        var conversation = await persistence.CreateConversationAsync(new NodeChatCreateConversationRequest("Provider", "node", CreatedAtUtc: 1)).ConfigureAwait(false);
        var correlation = await CreatePlaceholderAsync(persistence, conversation.ConversationId).ConfigureAwait(false);
        await persistence.MarkAssistantStreamingAsync(correlation, updatedAtUtc: 2).ConfigureAwait(false);

        await persistence.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest(correlation,
                             NodeChatMessageStatusValues.Completed,
                             UpdatedAtUtc: 3,
                             "answer",
                             Envelope: new AgentRunEnvelopeMetadata(Guid.NewGuid(), DurationMs: 5L, Provider: AgentUsageProviders.Codex)))
                         .ConfigureAwait(false);

        AssertEx.Equal(AgentUsageProviders.Codex, await ReadEnvelopeProviderAsync(provider, correlation).ConfigureAwait(false));
    }

    [Test]
    public async Task Terminalize_WhenNoProviderResolved_EnvelopeRowFallsBackToUnknownDefault()
    {
        // The interrupted/thin path resolves no provider: AgentRunEnvelopeMetadata defaults Provider to 'unknown', which
        // must land on the row (proving the column default + the metadata default agree).
        await using var provider = await BuildProviderAsync("envelope-provider-default.sqlite").ConfigureAwait(false);
        var persistence = CreateService(provider);
        var conversation = await persistence.CreateConversationAsync(new NodeChatCreateConversationRequest("Default", "node", CreatedAtUtc: 1)).ConfigureAwait(false);
        var correlation = await CreatePlaceholderAsync(persistence, conversation.ConversationId).ConfigureAwait(false);
        await persistence.MarkAssistantStreamingAsync(correlation, updatedAtUtc: 2).ConfigureAwait(false);

        await persistence.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest(correlation,
                             NodeChatMessageStatusValues.Interrupted,
                             UpdatedAtUtc: 3,
                             "partial",
                             Envelope: new AgentRunEnvelopeMetadata(Guid.NewGuid(), DurationMs: 0L)))
                         .ConfigureAwait(false);

        AssertEx.Equal(AgentUsageProviders.Unknown, await ReadEnvelopeProviderAsync(provider, correlation).ConfigureAwait(false));
    }

    [Test]
    public async Task TerminalizeAsync_WritesTheEnvelopeAtSchemaVersionFour()
    {
        // A row whose field set changed must never be emitted under the old version, or a reader cannot tell a v3 row
        // that predates the token columns from one that carries them. Asserted against the constant rather than the
        // literal 4, so a later slice's own bump does not break this test.
        await using var provider = await BuildProviderAsync("envelope-schema-version.sqlite").ConfigureAwait(false);
        var persistence = CreateService(provider);
        var conversation = await persistence.CreateConversationAsync(new NodeChatCreateConversationRequest("Version", "node", CreatedAtUtc: 1)).ConfigureAwait(false);
        var correlation = await CreatePlaceholderAsync(persistence, conversation.ConversationId).ConfigureAwait(false);
        await persistence.MarkAssistantStreamingAsync(correlation, updatedAtUtc: 2).ConfigureAwait(false);

        await persistence.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest(correlation,
                             NodeChatMessageStatusValues.Completed,
                             UpdatedAtUtc: 3,
                             "answer",
                             Envelope: new AgentRunEnvelopeMetadata(Guid.NewGuid(), DurationMs: 5L)))
                         .ConfigureAwait(false);

        var telemetry = await ReadEnvelopeTelemetryAsync(provider, correlation).ConfigureAwait(false);
        AssertEx.Equal(AgentRunEnvelope.CurrentSchemaVersion, telemetry.SchemaVersion);
    }

    [Test]
    public async Task TerminalizeAsync_WritesTheToolSchemaTokenEstimateOntoTheEnvelopeRow()
    {
        // The cumulative estimate is a long end to end — its source counter is one, and a whole session's worth of these
        // is summed downstream — so a value above int.MaxValue must survive the write and read back equal. This is the
        // one test that fails if any link in column -> record -> DTO narrows back to an int.
        await using var provider = await BuildProviderAsync("envelope-tool-schema-tokens.sqlite").ConfigureAwait(false);
        var persistence = CreateService(provider);
        var conversation = await persistence.CreateConversationAsync(new NodeChatCreateConversationRequest("Tokens", "node", CreatedAtUtc: 1)).ConfigureAwait(false);
        var correlation = await CreatePlaceholderAsync(persistence, conversation.ConversationId).ConfigureAwait(false);
        await persistence.MarkAssistantStreamingAsync(correlation, updatedAtUtc: 2).ConfigureAwait(false);
        const long wideEstimate = (long)int.MaxValue + 1;

        await persistence.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest(correlation,
                             NodeChatMessageStatusValues.Completed,
                             UpdatedAtUtc: 3,
                             "answer",
                             Envelope: new AgentRunEnvelopeMetadata(Guid.NewGuid(),
                                 DurationMs: 5L,
                                 ToolSchemaTokens: wideEstimate,
                                 MaxToolSchemaTokens: 4_096)))
                         .ConfigureAwait(false);

        var telemetry = await ReadEnvelopeTelemetryAsync(provider, correlation).ConfigureAwait(false);
        AssertEx.Equal(wideEstimate, telemetry.ToolSchemaTokens);
        AssertEx.Equal(expected: 4_096L, telemetry.MaxToolSchemaTokens);
    }

    [Test]
    public async Task TerminalizeAsync_WhenNoEstimateWasReported_LeavesTheTokenColumnsNull()
    {
        await using var provider = await BuildProviderAsync("envelope-tool-schema-null.sqlite").ConfigureAwait(false);
        var persistence = CreateService(provider);
        var conversation = await persistence.CreateConversationAsync(new NodeChatCreateConversationRequest("Null", "node", CreatedAtUtc: 1)).ConfigureAwait(false);
        var correlation = await CreatePlaceholderAsync(persistence, conversation.ConversationId).ConfigureAwait(false);
        await persistence.MarkAssistantStreamingAsync(correlation, updatedAtUtc: 2).ConfigureAwait(false);

        await persistence.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest(correlation,
                             NodeChatMessageStatusValues.Interrupted,
                             UpdatedAtUtc: 3,
                             "partial",
                             Envelope: new AgentRunEnvelopeMetadata(Guid.NewGuid(), DurationMs: 0L)))
                         .ConfigureAwait(false);

        var telemetry = await ReadEnvelopeTelemetryAsync(provider, correlation).ConfigureAwait(false);
        AssertEx.Null(telemetry.ToolSchemaTokens);
        AssertEx.Null(telemetry.MaxToolSchemaTokens);
    }

    [Test]
    public async Task TerminalizeAsync_WritesTheTurnTotalsOntoTheEnvelopeRowInsteadOfTheMessageTokens()
    {
        // The envelope is the COST ledger: a tool-calling turn's rounds add up there, while the message row keeps the
        // last round's counts because that is the context the model actually held. The two numbers are deliberately
        // different, and this is the write that has to prefer the turn totals.
        await using var provider = await BuildProviderAsync("envelope-turn-totals.sqlite").ConfigureAwait(false);
        var persistence = CreateService(provider);
        var conversation = await persistence.CreateConversationAsync(new NodeChatCreateConversationRequest("Turn", "node", CreatedAtUtc: 1)).ConfigureAwait(false);
        var correlation = await CreatePlaceholderAsync(persistence, conversation.ConversationId).ConfigureAwait(false);
        await persistence.MarkAssistantStreamingAsync(correlation, updatedAtUtc: 2).ConfigureAwait(false);

        await persistence.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest(correlation,
                             NodeChatMessageStatusValues.Completed,
                             UpdatedAtUtc: 3,
                             "answer",
                             InputCount: 3_000,
                             OutputCount: 30,
                             TotalCount: 3_038,
                             ReasoningCount: 8,
                             Envelope: new AgentRunEnvelopeMetadata(Guid.NewGuid(),
                                 DurationMs: 5L,
                                 TurnInputTokens: 6_000,
                                 TurnOutputTokens: 60,
                                 TurnTotalTokens: 6_078,
                                 TurnReasoningTokens: 18)))
                         .ConfigureAwait(false);

        var tokens = await ReadEnvelopeTokensAsync(provider, correlation).ConfigureAwait(false);
        AssertEx.Equal(expected: 6_000L, tokens.PromptTokens);
        AssertEx.Equal(expected: 60L, tokens.CompletionTokens);
        AssertEx.Equal(expected: 18L, tokens.ReasoningTokens);
        AssertEx.Equal(expected: 6_078L, tokens.TotalTokens);
    }

    [Test]
    public async Task TerminalizeAsync_WhenNoTurnTotalsWereSupplied_WritesTheMessageTokensOntoTheEnvelopeRow()
    {
        // The restart-recovery backfill and the platform path report no turn totals, so their envelope rows keep the
        // exact values they have always carried rather than silently becoming null.
        await using var provider = await BuildProviderAsync("envelope-turn-totals-fallback.sqlite").ConfigureAwait(false);
        var persistence = CreateService(provider);
        var conversation = await persistence.CreateConversationAsync(new NodeChatCreateConversationRequest("Fallback", "node", CreatedAtUtc: 1)).ConfigureAwait(false);
        var correlation = await CreatePlaceholderAsync(persistence, conversation.ConversationId).ConfigureAwait(false);
        await persistence.MarkAssistantStreamingAsync(correlation, updatedAtUtc: 2).ConfigureAwait(false);

        await persistence.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest(correlation,
                             NodeChatMessageStatusValues.Completed,
                             UpdatedAtUtc: 3,
                             "answer",
                             InputCount: 3_000,
                             OutputCount: 30,
                             TotalCount: 3_038,
                             ReasoningCount: 8,
                             Envelope: new AgentRunEnvelopeMetadata(Guid.NewGuid(), DurationMs: 5L)))
                         .ConfigureAwait(false);

        var tokens = await ReadEnvelopeTokensAsync(provider, correlation).ConfigureAwait(false);
        AssertEx.Equal(expected: 3_000L, tokens.PromptTokens);
        AssertEx.Equal(expected: 30L, tokens.CompletionTokens);
        AssertEx.Equal(expected: 8L, tokens.ReasoningTokens);
        AssertEx.Equal(expected: 3_038L, tokens.TotalTokens);
    }

    // Reads the run-envelope row's four token columns in ONE statement with a literal command text, for the same reason
    // as the helper below: no column name is ever interpolated into SQL.
    private static async Task<EnvelopeTokens> ReadEnvelopeTokensAsync(ServiceProvider provider, NodeChatMessageCorrelation correlation)
    {
        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
        var connection = dbContext.Database.GetDbConnection();
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT prompt_tokens, completion_tokens, reasoning_tokens, total_tokens FROM agent_execution_logs WHERE record_kind = $record_kind AND message_id = $message_id;";
        AddParameter(command, "$record_kind", EnvelopeKind);
        AddParameter(command, "$message_id", correlation.MessageId);

        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        if (!await reader.ReadAsync().ConfigureAwait(false))
        {
            throw new InvalidOperationException("The run-envelope row was not found.");
        }

        return new EnvelopeTokens(await reader.IsDBNullAsync(0).ConfigureAwait(false) ? null : reader.GetInt64(0),
            await reader.IsDBNullAsync(1).ConfigureAwait(false) ? null : reader.GetInt64(1),
            await reader.IsDBNullAsync(2).ConfigureAwait(false) ? null : reader.GetInt64(2),
            await reader.IsDBNullAsync(3).ConfigureAwait(false) ? null : reader.GetInt64(3));
    }

    private sealed record EnvelopeTokens(long? PromptTokens, long? CompletionTokens, long? ReasoningTokens, long? TotalTokens);

    // Reads the run-envelope row's schema version and both tool-schema token columns in ONE statement with a literal
    // command text, so no column name is ever interpolated into SQL.
    private static async Task<EnvelopeTelemetry> ReadEnvelopeTelemetryAsync(ServiceProvider provider, NodeChatMessageCorrelation correlation)
    {
        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
        var connection = dbContext.Database.GetDbConnection();
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT schema_version, tool_schema_tokens, max_tool_schema_tokens FROM agent_execution_logs WHERE record_kind = $record_kind AND message_id = $message_id;";
        AddParameter(command, "$record_kind", EnvelopeKind);
        AddParameter(command, "$message_id", correlation.MessageId);

        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        if (!await reader.ReadAsync().ConfigureAwait(false))
        {
            throw new InvalidOperationException("The run-envelope row was not found.");
        }

        return new EnvelopeTelemetry(reader.GetInt32(0),
            await reader.IsDBNullAsync(1).ConfigureAwait(false) ? null : reader.GetInt64(1),
            await reader.IsDBNullAsync(2).ConfigureAwait(false) ? null : reader.GetInt64(2));
    }

    private sealed record EnvelopeTelemetry(int SchemaVersion, long? ToolSchemaTokens, long? MaxToolSchemaTokens);

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

    // Seeds a fresh conversation with a streaming (non-terminal) assistant placeholder — the state both the purge and the
    // enveloped terminalize race from.
    private static async Task<NodeChatMessageCorrelation> SeedStreamingConversationAsync(NodeChatPersistenceService service, int iteration)
    {
        var conversation = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Race", "node", CreatedAtUtc: iteration)).ConfigureAwait(false);
        var correlation = await CreatePlaceholderAsync(service, conversation.ConversationId).ConfigureAwait(false);
        await service.MarkAssistantStreamingAsync(correlation, updatedAtUtc: 2).ConfigureAwait(false);
        return correlation;
    }

    // Runs the enveloped terminalize; returns true when it committed, false when the purge already removed the row so
    // terminalize found nothing (the documented InvalidOperationException).
    private static async Task<bool> TryTerminalizeAsync(NodeChatPersistenceService service, NodeChatMessageCorrelation correlation)
    {
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
    }

    private static Task PurgeAsync(NodeChatPersistenceService service, Guid conversationId)
    {
        return service.DeleteConversationAsync(new NodeChatDeleteConversationRequest(conversationId, DeletedAtUtc: 20, PurgeImmediately: true));
    }

    // Forces one lock-arbitration ordering deterministically: the intended winner is awaited to completion before the
    // loser starts, so the per-conversation lock arbitrates the intended branch without depending on the scheduler.
    // Asserts the winner's expected outcome plus the shared safety invariant, and returns 1 for the branch that won so
    // the caller can prove both branches ran.
    private static async Task<int> AssertForcedOrderingAsync(NodeChatPersistenceService service, ServiceProvider provider, bool terminalizeFirst, int iteration)
    {
        var correlation = await SeedStreamingConversationAsync(service, iteration).ConfigureAwait(false);

        if (terminalizeFirst)
        {
            var committed = await TryTerminalizeAsync(service, correlation).ConfigureAwait(false);
            AssertEx.True(committed, "Terminalize must commit when it runs to completion before the purge.");
            await PurgeAsync(service, correlation.ConversationId).ConfigureAwait(false);
        }
        else
        {
            await PurgeAsync(service, correlation.ConversationId).ConfigureAwait(false);
            var committed = await TryTerminalizeAsync(service, correlation).ConfigureAwait(false);
            AssertEx.False(committed, "Terminalize must find no row and throw when the purge runs to completion first.");
        }

        await AssertNoOrphanedFootprintAsync(service, provider, correlation.ConversationId).ConfigureAwait(false);
        return 1;
    }

    // The shared safety invariant: the conversation footprint is gone and NO envelope row survives to orphan the
    // plaintext conversation/message correlation.
    private static async Task AssertNoOrphanedFootprintAsync(NodeChatPersistenceService service, ServiceProvider provider, Guid conversationId)
    {
        AssertEx.Null(await service.GetConversationAsync(conversationId).ConfigureAwait(false));
        AssertEx.Equal(expected: 0, await CountMessagesAsync(provider, conversationId).ConfigureAwait(false));
        AssertEx.Equal(expected: 0, await CountEnvelopesAsync(provider).ConfigureAwait(false));
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

    private static async Task<string> ReadEnvelopeProviderAsync(ServiceProvider provider, NodeChatMessageCorrelation correlation)
    {
        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
        var connection = dbContext.Database.GetDbConnection();
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT provider FROM agent_execution_logs WHERE record_kind = $record_kind AND message_id = $message_id;";
        AddParameter(command, "$record_kind", EnvelopeKind);
        AddParameter(command, "$message_id", correlation.MessageId);
        var value = await command.ExecuteScalarAsync().ConfigureAwait(false);
        return value as string ?? throw new InvalidOperationException("The run-envelope row was not found.");
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
