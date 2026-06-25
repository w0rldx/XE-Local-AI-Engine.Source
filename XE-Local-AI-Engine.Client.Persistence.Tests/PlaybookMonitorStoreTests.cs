namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

public sealed class PlaybookMonitorStoreTests : IDisposable
{
    private const string Up = "up";
    private const string Down = "down";
    private const long EnabledAt = 100;
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    [Test]
    public async Task GetCohortComparisonAsync_SplitsFeedbackBeforeAndAfterEnabledAt_ExcludingPurgedAndOtherAgents()
    {
        var databasePath = GetDatabasePath("cohort-overall.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();

        var agentA = await SeedAgentAsync(context, "Agent A");
        var agentB = await SeedAgentAsync(context, "Agent B");
        var connection = await OpenConnectionAsync(context);

        var cA = Guid.NewGuid();
        var cAArchived = Guid.NewGuid();
        var cAPurged = Guid.NewGuid();
        await InsertConversationAsync(connection, cA, agentA, purged: false, archived: false);
        await InsertConversationAsync(connection, cAArchived, agentA, purged: false, archived: true);
        await InsertConversationAsync(connection, cAPurged, agentA, purged: true, archived: false);
        var cB = Guid.NewGuid();
        await InsertConversationAsync(connection, cB, agentB, purged: false, archived: false);

        // Before window (< 100): 1 up + 2 down = 3 total, 2 down.
        await InsertFeedbackAsync(connection, Guid.NewGuid(), cA, Up, createdAtUtc: 10);
        await InsertFeedbackAsync(connection, Guid.NewGuid(), cA, Down, createdAtUtc: 20);
        await InsertFeedbackAsync(connection, Guid.NewGuid(), cAArchived, Down, createdAtUtc: 30);
        // After window (>= 100): the boundary row at exactly 100 is "after"; 3 up + 1 down = 4 total, 1 down.
        await InsertFeedbackAsync(connection, Guid.NewGuid(), cA, Up, createdAtUtc: 100);
        await InsertFeedbackAsync(connection, Guid.NewGuid(), cA, Up, createdAtUtc: 150);
        await InsertFeedbackAsync(connection, Guid.NewGuid(), cAArchived, Up, createdAtUtc: 200);
        await InsertFeedbackAsync(connection, Guid.NewGuid(), cA, Down, createdAtUtc: 250);
        // Excluded: purged conversation + the other agent's feedback must not leak into the primary agent's windows.
        await InsertFeedbackAsync(connection, Guid.NewGuid(), cAPurged, Down, createdAtUtc: 40);
        await InsertFeedbackAsync(connection, Guid.NewGuid(), cAPurged, Down, createdAtUtc: 160);
        await InsertFeedbackAsync(connection, Guid.NewGuid(), cB, Down, createdAtUtc: 50);
        await InsertFeedbackAsync(connection, Guid.NewGuid(), cB, Down, createdAtUtc: 170);

        var store = new PlaybookMonitorStore(context);
        var comparison = await store.GetCohortComparisonAsync(agentA, EnabledAt, toolScope: null);

        AssertEx.Equal(expected: 3, comparison.BeforeTotal);
        AssertEx.Equal(expected: 2, comparison.BeforeDown);
        AssertEx.Equal(expected: 4, comparison.AfterTotal);
        AssertEx.Equal(expected: 1, comparison.AfterDown);
    }

    [Test]
    public async Task GetCohortComparisonAsync_WhenNoFeedback_ReturnsZeroedComparison()
    {
        var databasePath = GetDatabasePath("cohort-empty.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();

        var agent = await SeedAgentAsync(context, "Empty");
        var store = new PlaybookMonitorStore(context);

        var comparison = await store.GetCohortComparisonAsync(agent, EnabledAt, toolScope: null);

        AssertEx.Equal(expected: 0, comparison.BeforeTotal);
        AssertEx.Equal(expected: 0, comparison.BeforeDown);
        AssertEx.Equal(expected: 0, comparison.AfterTotal);
        AssertEx.Equal(expected: 0, comparison.AfterDown);
    }

    [Test]
    public async Task GetCohortComparisonAsync_WithToolScope_CountsDistinctMessagesForTheToolWindows()
    {
        var databasePath = GetDatabasePath("cohort-facet.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();

        var agent = await SeedAgentAsync(context, "Faceted");
        var connection = await OpenConnectionAsync(context);

        var cSearch = Guid.NewGuid();
        var cCalc = Guid.NewGuid();
        await InsertConversationAsync(connection, cSearch, agent, purged: false, archived: false);
        await InsertConversationAsync(connection, cCalc, agent, purged: false, archived: false);

        // The search conversation: before window 1 up + 1 down; after window 2 down (rated messages). The calc
        // conversation's feedback must NOT count toward the "search" facet.
        await InsertFeedbackAsync(connection, Guid.NewGuid(), cSearch, Up, createdAtUtc: 10);
        await InsertFeedbackAsync(connection, Guid.NewGuid(), cSearch, Down, createdAtUtc: 20);
        await InsertFeedbackAsync(connection, Guid.NewGuid(), cSearch, Down, createdAtUtc: 120);
        await InsertFeedbackAsync(connection, Guid.NewGuid(), cSearch, Down, createdAtUtc: 130);
        await InsertFeedbackAsync(connection, Guid.NewGuid(), cCalc, Down, createdAtUtc: 25);
        await InsertFeedbackAsync(connection, Guid.NewGuid(), cCalc, Down, createdAtUtc: 140);

        // "search" fires three times in cSearch: COUNT(DISTINCT message_id) must keep the rated messages from
        // inflating via the conversation x tool cartesian, so the facet counts stay at the per-message totals.
        await InsertToolEventAsync(connection, cSearch, "search");
        await InsertToolEventAsync(connection, cSearch, "search");
        await InsertToolEventAsync(connection, cSearch, "search");
        await InsertToolEventAsync(connection, cCalc, "calc");

        var store = new PlaybookMonitorStore(context);
        var comparison = await store.GetCohortComparisonAsync(agent, EnabledAt, "search");

        AssertEx.Equal(expected: 2, comparison.BeforeTotal);
        AssertEx.Equal(expected: 1, comparison.BeforeDown);
        AssertEx.Equal(expected: 2, comparison.AfterTotal);
        AssertEx.Equal(expected: 2, comparison.AfterDown);
    }

    private static async Task<Guid> SeedAgentAsync(NodeChatDbContext context, string name)
    {
        var store = new AgentDefinitionStore(context, TimeProvider.System);
        var agent = await store.AddAsync(new AgentDefinitionInput(name,
            Description: null,
            "You are a careful engineering agent.",
            ModelProfile: null,
            ReasoningEffort: null,
            AgentDefinitionKind.Single,
            [],
            new Dictionary<string, bool>(),
            OrchestrationTopologyJson: null));
        return agent.Id;
    }

    private static async Task<DbConnection> OpenConnectionAsync(NodeChatDbContext context)
    {
        var connection = context.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        return connection;
    }

    private static async Task InsertConversationAsync(DbConnection connection, Guid conversationId, Guid? agentDefinitionId, bool purged, bool archived)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              INSERT INTO conversations (conversation_id, title, created_at_utc, last_seen_utc, purged, origin, agent_definition_id, archived)
                              VALUES ($id, 'conv', 0, 0, $purged, 'Local', $agent, $archived);
                              """;
        AddParameter(command, "$id", conversationId);
        AddParameter(command, "$purged", purged ? 1 : 0);
        AddParameter(command, "$agent", agentDefinitionId);
        AddParameter(command, "$archived", archived ? 1 : 0);
        _ = await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertMessageAsync(DbConnection connection, Guid messageId, Guid conversationId, long createdAtUtc)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              INSERT INTO messages (message_id, conversation_id, sequence, role, content, created_at_utc, updated_at_utc)
                              VALUES ($message_id, $conversation_id, 0, 'assistant', 'answer', $created_at_utc, $created_at_utc);
                              """;
        AddParameter(command, "$message_id", messageId);
        AddParameter(command, "$conversation_id", conversationId);
        AddParameter(command, "$created_at_utc", createdAtUtc);
        _ = await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertFeedbackAsync(DbConnection connection, Guid messageId, Guid conversationId, string rating, long createdAtUtc)
    {
        // A feedback row FKs to its message (FK enforcement is on for this context), so the rated message must exist
        // first — mirroring production, where the assistant placeholder precedes the feedback upsert.
        await InsertMessageAsync(connection, messageId, conversationId, createdAtUtc).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
                              INSERT INTO message_feedback (message_id, conversation_id, rating, comment, created_at_utc, updated_at_utc)
                              VALUES ($message_id, $conversation_id, $rating, NULL, $created_at_utc, $created_at_utc);
                              """;
        AddParameter(command, "$message_id", messageId);
        AddParameter(command, "$conversation_id", conversationId);
        AddParameter(command, "$rating", rating);
        AddParameter(command, "$created_at_utc", createdAtUtc);
        _ = await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertToolEventAsync(DbConnection connection, Guid conversationId, string toolName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              INSERT INTO tool_events (tool_call_id, conversation_id, tool_name, plaintext_args, plaintext_result, status, created_at_utc)
                              VALUES ($tool_call_id, $conversation_id, $tool_name, '{}', '', 'Completed', 0);
                              """;
        AddParameter(command, "$tool_call_id", Guid.NewGuid());
        AddParameter(command, "$conversation_id", conversationId);
        AddParameter(command, "$tool_name", toolName);
        _ = await command.ExecuteNonQueryAsync();
    }

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static NodeChatDbContext CreateContext(string databasePath, INodeSqliteKeyHolder keyHolder)
    {
        return AgentDefinitionTestContextFactory.Create(databasePath, keyHolder);
    }

    private string GetDatabasePath(string fileName)
    {
        Directory.CreateDirectory(_rootPath);
        return Path.Combine(_rootPath, fileName);
    }

    private static byte[] CreateKeyMaterial()
    {
        return Enumerable.Range(start: 0, count: 32).Select(static value => (byte)(value + 1)).ToArray();
    }

    private sealed class FixedNodeSqliteKeyHolder(byte[] key) : INodeSqliteKeyHolder
    {
        private byte[]? _key = key;

        public ReadOnlyMemory<byte> Key
        {
            get
            {
                ObjectDisposedException.ThrowIf(_key is null, this);
                return _key;
            }
        }

        public void Dispose()
        {
            if (_key is null)
            {
                return;
            }

            CryptographicOperations.ZeroMemory(_key);
            _key = null;
        }
    }
}
