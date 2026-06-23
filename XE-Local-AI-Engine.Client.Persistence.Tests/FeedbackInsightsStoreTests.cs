namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

public sealed class FeedbackInsightsStoreTests : IDisposable
{
    private const string Up = "up";
    private const string Down = "down";
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    [Test]
    public async Task GetAgentFeedbackAggregateAsync_GroupsCountsToolsAndExemplars_ExcludingPurgedUnboundAndOtherAgents()
    {
        var databasePath = GetDatabasePath("aggregate.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();

        var agentA = await SeedAgentAsync(context, "Agent A");
        var agentB = await SeedAgentAsync(context, "Agent B");
        var connection = await OpenConnectionAsync(context);

        // Primary agent: one active conversation, one archived (still counted), one purged (excluded).
        var cA1 = Guid.NewGuid();
        var cA2 = Guid.NewGuid();
        var cAPurged = Guid.NewGuid();
        await InsertConversationAsync(connection, cA1, agentA, purged: false, archived: false);
        await InsertConversationAsync(connection, cA2, agentA, purged: false, archived: true);
        await InsertConversationAsync(connection, cAPurged, agentA, purged: true, archived: false);
        // Other-agent and unbound conversations: their feedback must not leak into the primary agent's aggregate.
        var cB = Guid.NewGuid();
        var cUnbound = Guid.NewGuid();
        await InsertConversationAsync(connection, cB, agentB, purged: false, archived: false);
        await InsertConversationAsync(connection, cUnbound, agentDefinitionId: null, purged: false, archived: false);

        var m1 = Guid.NewGuid();
        var m2 = Guid.NewGuid();
        var m3 = Guid.NewGuid();
        var m4 = Guid.NewGuid();
        await InsertFeedbackAsync(connection, m1, cA1, Up, comment: null, createdAtUtc: 50, agentA);
        await InsertFeedbackAsync(connection, m2, cA1, Down, "slow", createdAtUtc: 100, agentA);
        await InsertFeedbackAsync(connection, m3, cA1, Down, "wrong", createdAtUtc: 200, agentA);
        await InsertFeedbackAsync(connection, m4, cA2, Up, "great", createdAtUtc: 150, agentA);
        await InsertFeedbackAsync(connection, Guid.NewGuid(), cAPurged, Down, "excluded", createdAtUtc: 300, agentA);
        await InsertFeedbackAsync(connection, Guid.NewGuid(), cB, Down, "b-down", createdAtUtc: 120, agentB);
        // Unbound message: no per-message agent → must not leak into any named agent's aggregate.
        await InsertFeedbackAsync(connection, Guid.NewGuid(), cUnbound, Down, "unbound", createdAtUtc: 130, agentDefinitionId: null);

        // "search" fires three times in cA1: COUNT(DISTINCT message_id) must keep search at 2 up / 2 down (the rated
        // messages), NOT inflate by the conversation x tool cartesian. This makes the DISTINCT load-bearing.
        await InsertToolEventAsync(connection, cA1, "search");
        await InsertToolEventAsync(connection, cA1, "search");
        await InsertToolEventAsync(connection, cA1, "search");
        await InsertToolEventAsync(connection, cA2, "search");
        await InsertToolEventAsync(connection, cA2, "calc");
        await InsertToolEventAsync(connection, cAPurged, "search");
        await InsertToolEventAsync(connection, cB, "search");

        var store = new FeedbackInsightsStore(context);
        var aggregate = AssertEx.NotNull(await store.GetAgentFeedbackAggregateAsync(agentA, exemplarCap: 5), "Existing agent should aggregate.");

        AssertEx.Equal("Agent A", aggregate.AgentName);
        // Only the primary agent's non-purged feedback is counted: cA1 (1 up, 2 down) + cA2 (1 up). Purged/other-agent/unbound excluded.
        AssertEx.Equal(expected: 2, aggregate.UpCount);
        AssertEx.Equal(expected: 2, aggregate.DownCount);

        // Per-tool, conversation-level attribution, ordered by total desc then name. search spans cA1+cA2 (4 rated
        // messages: 2 up, 2 down); calc only cA2 (1 up). cAPurged's search is excluded.
        AssertEx.Equal(expected: 2, aggregate.ByTool.Count);
        AssertEx.Equal("search", aggregate.ByTool[0].ToolName);
        AssertEx.Equal(expected: 2, aggregate.ByTool[0].UpCount);
        AssertEx.Equal(expected: 2, aggregate.ByTool[0].DownCount);
        AssertEx.Equal("calc", aggregate.ByTool[1].ToolName);
        AssertEx.Equal(expected: 1, aggregate.ByTool[1].UpCount);
        AssertEx.Equal(expected: 0, aggregate.ByTool[1].DownCount);

        // Exemplars: comment-only, down-first then newest-first. m1 (no comment) and the purged comment are excluded.
        AssertEx.Equal(expected: 3, aggregate.Exemplars.Count);
        AssertEx.Equal("wrong", aggregate.Exemplars[0].Comment);
        AssertEx.Equal(Down, aggregate.Exemplars[0].Rating);
        AssertEx.Equal(m3, aggregate.Exemplars[0].MessageId);
        AssertEx.Equal(cA1, aggregate.Exemplars[0].ConversationId);
        AssertEx.Equal("slow", aggregate.Exemplars[1].Comment);
        AssertEx.Equal(Down, aggregate.Exemplars[1].Rating);
        AssertEx.Equal("great", aggregate.Exemplars[2].Comment);
        AssertEx.Equal(Up, aggregate.Exemplars[2].Rating);
    }

    [Test]
    public async Task GetAgentFeedbackAggregateAsync_AttributesByPerMessageAgent_NotConversationBinding()
    {
        // Regression for the analyze-always-empty bug: attribution must key on the per-message agent
        // (messages.agent_definition_id), NOT the conversation binding. The chat UI creates conversations with
        // no agent binding, so per-message attribution is the only signal that surfaces feedback.
        var databasePath = GetDatabasePath("per-message.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();

        var agentA = await SeedAgentAsync(context, "Agent A");
        var agentB = await SeedAgentAsync(context, "Agent B");
        var connection = await OpenConnectionAsync(context);

        // One unbound conversation (mirrors production) holding messages from BOTH agents, plus a conversation that
        // is BOUND to agent B but whose rated message was produced by agent A — proving the conversation binding is
        // ignored in favour of the per-message agent.
        var unbound = Guid.NewGuid();
        var boundToB = Guid.NewGuid();
        await InsertConversationAsync(connection, unbound, agentDefinitionId: null, purged: false, archived: false);
        await InsertConversationAsync(connection, boundToB, agentB, purged: false, archived: false);

        await InsertFeedbackAsync(connection, Guid.NewGuid(), unbound, Up, "a-up", createdAtUtc: 10, agentA);
        await InsertFeedbackAsync(connection, Guid.NewGuid(), unbound, Down, "b-down", createdAtUtc: 20, agentB);
        // Conversation bound to B, but this message was produced by A: it must count under A, not B.
        await InsertFeedbackAsync(connection, Guid.NewGuid(), boundToB, Up, "a-in-b-conv", createdAtUtc: 30, agentA);

        var store = new FeedbackInsightsStore(context);

        var aggregateA = AssertEx.NotNull(await store.GetAgentFeedbackAggregateAsync(agentA, exemplarCap: 5), "Agent A should aggregate.");
        AssertEx.Equal(expected: 2, aggregateA.UpCount);
        AssertEx.Equal(expected: 0, aggregateA.DownCount);

        var aggregateB = AssertEx.NotNull(await store.GetAgentFeedbackAggregateAsync(agentB, exemplarCap: 5), "Agent B should aggregate.");
        AssertEx.Equal(expected: 0, aggregateB.UpCount);
        AssertEx.Equal(expected: 1, aggregateB.DownCount);
    }

    [Test]
    public async Task GetAgentFeedbackAggregateAsync_WhenAgentMissing_ReturnsNull()
    {
        var databasePath = GetDatabasePath("missing.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();

        var store = new FeedbackInsightsStore(context);

        AssertEx.Null(await store.GetAgentFeedbackAggregateAsync(Guid.NewGuid(), exemplarCap: 5), "An unknown agent id should return null.");
    }

    [Test]
    public async Task GetAgentFeedbackAggregateAsync_CapsExemplarsNewestFirst()
    {
        var databasePath = GetDatabasePath("cap.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();

        var agent = await SeedAgentAsync(context, "Capped");
        var connection = await OpenConnectionAsync(context);

        var conversation = Guid.NewGuid();
        await InsertConversationAsync(connection, conversation, agent, purged: false, archived: false);
        await InsertFeedbackAsync(connection, Guid.NewGuid(), conversation, Down, "oldest", createdAtUtc: 10, agent);
        await InsertFeedbackAsync(connection, Guid.NewGuid(), conversation, Down, "middle", createdAtUtc: 20, agent);
        await InsertFeedbackAsync(connection, Guid.NewGuid(), conversation, Down, "newest", createdAtUtc: 30, agent);

        var store = new FeedbackInsightsStore(context);
        var aggregate = AssertEx.NotNull(await store.GetAgentFeedbackAggregateAsync(agent, exemplarCap: 2), "Existing agent should aggregate.");

        AssertEx.Equal(expected: 2, aggregate.Exemplars.Count);
        AssertEx.Equal("newest", aggregate.Exemplars[0].Comment);
        AssertEx.Equal("middle", aggregate.Exemplars[1].Comment);
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

    private static async Task InsertMessageAsync(DbConnection connection, Guid messageId, Guid conversationId, long createdAtUtc, Guid? agentDefinitionId)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              INSERT INTO messages (message_id, conversation_id, sequence, role, content, created_at_utc, updated_at_utc, agent_definition_id)
                              VALUES ($message_id, $conversation_id, 0, 'assistant', 'answer', $created_at_utc, $created_at_utc, $agent_definition_id);
                              """;
        AddParameter(command, "$message_id", messageId);
        AddParameter(command, "$conversation_id", conversationId);
        AddParameter(command, "$created_at_utc", createdAtUtc);
        AddParameter(command, "$agent_definition_id", agentDefinitionId);
        _ = await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertFeedbackAsync(DbConnection connection, Guid messageId, Guid conversationId, string rating, string? comment, long createdAtUtc, Guid? agentDefinitionId)
    {
        // A feedback row FKs to its message (FK enforcement is on for this context), so the rated message must exist
        // first — mirroring production, where the assistant placeholder precedes the feedback upsert. The per-message
        // agent id (not the conversation's) is what attribution keys on, so each rated message carries it explicitly.
        await InsertMessageAsync(connection, messageId, conversationId, createdAtUtc, agentDefinitionId).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
                              INSERT INTO message_feedback (message_id, conversation_id, rating, comment, created_at_utc, updated_at_utc)
                              VALUES ($message_id, $conversation_id, $rating, $comment, $created_at_utc, $created_at_utc);
                              """;
        AddParameter(command, "$message_id", messageId);
        AddParameter(command, "$conversation_id", conversationId);
        AddParameter(command, "$rating", rating);
        AddParameter(command, "$comment", comment);
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
