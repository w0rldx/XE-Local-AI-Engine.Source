namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Harvest read boundary <see cref="GoldenHarvestSourceStore" />: reconstructs harvest candidates from an agent's
///     thumbs-up assistant turns. Messages are seeded through the encrypted context so the materialization interceptor
///     decrypts content on read (proving EF decryption); the thumbs-up scan reads the plaintext feedback columns via raw
///     ADO. FK enforcement is on (EnsureCreated) → seed agent → conversation → messages → feedback.
/// </summary>
public sealed class GoldenHarvestSourceStoreTests : IDisposable
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
    public async Task ListThumbsUpSourcesAsync_ReconstructsPriorTurnsAndApprovedAnswer()
    {
        var databasePath = GetDatabasePath("reconstruct.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();

        var agentId = await SeedAgentAsync(context, "Agent A");
        var conversationId = Guid.NewGuid();
        await SeedConversationAsync(context, conversationId, agentId, "Conv Title", purged: false);

        await SeedMessageAsync(context, conversationId, sequence: 1, "user", "hi");
        await SeedMessageAsync(context, conversationId, sequence: 2, "assistant", "answer one");
        await SeedMessageAsync(context, conversationId, sequence: 3, "user", "more");
        var targetMessageId = await SeedMessageAsync(context, conversationId, sequence: 4, "assistant", "GOOD ANSWER");

        await SeedFeedbackAsync(context, targetMessageId, conversationId, NodeMessageFeedbackRating.Up);

        var store = new GoldenHarvestSourceStore(context);
        var sources = await store.ListThumbsUpSourcesAsync(agentId, maxScan: 50);

        AssertEx.Equal(expected: 1, sources.Count);
        var source = sources[0];
        AssertEx.Equal(targetMessageId, source.MessageId);
        AssertEx.Equal(conversationId, source.ConversationId);
        AssertEx.Equal("Conv Title", source.ConversationTitle);
        AssertEx.Equal("GOOD ANSWER", source.ApprovedAnswerText);

        // PriorTurns = the three completed user/assistant turns with Sequence < 4 (NOT the target itself), decrypted.
        AssertEx.Equal(expected: 3, source.PriorTurns.Count);
        AssertEx.Equal("user", source.PriorTurns[0].Role);
        AssertEx.Equal("hi", source.PriorTurns[0].Text);
        AssertEx.Equal("assistant", source.PriorTurns[1].Role);
        AssertEx.Equal("answer one", source.PriorTurns[1].Text);
        AssertEx.Equal("user", source.PriorTurns[2].Role);
        AssertEx.Equal("more", source.PriorTurns[2].Text);
    }

    [Test]
    public async Task ListThumbsUpSourcesAsync_ExcludesOtherAgentsConversations()
    {
        var databasePath = GetDatabasePath("cross-agent.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();

        var agentA = await SeedAgentAsync(context, "Agent A");
        var agentB = await SeedAgentAsync(context, "Agent B");

        var convA = Guid.NewGuid();
        await SeedConversationAsync(context, convA, agentA, "A", purged: false);
        await SeedMessageAsync(context, convA, sequence: 1, "user", "a-question");
        var targetA = await SeedMessageAsync(context, convA, sequence: 2, "assistant", "a-answer");
        await SeedFeedbackAsync(context, targetA, convA, NodeMessageFeedbackRating.Up);

        var convB = Guid.NewGuid();
        await SeedConversationAsync(context, convB, agentB, "B", purged: false);
        await SeedMessageAsync(context, convB, sequence: 1, "user", "b-question");
        var targetB = await SeedMessageAsync(context, convB, sequence: 2, "assistant", "b-answer");
        await SeedFeedbackAsync(context, targetB, convB, NodeMessageFeedbackRating.Up);

        var store = new GoldenHarvestSourceStore(context);
        var sources = await store.ListThumbsUpSourcesAsync(agentA, maxScan: 50);

        AssertEx.Equal(expected: 1, sources.Count);
        AssertEx.Equal(targetA, sources[0].MessageId);
    }

    [Test]
    public async Task ListThumbsUpSourcesAsync_ExcludesPurgedConversationsAndDownRatings()
    {
        var databasePath = GetDatabasePath("purged-down.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();

        var agentId = await SeedAgentAsync(context, "Agent A");

        // A purged conversation with an up-rated answer → excluded.
        var purgedConv = Guid.NewGuid();
        await SeedConversationAsync(context, purgedConv, agentId, "Purged", purged: true);
        await SeedMessageAsync(context, purgedConv, sequence: 1, "user", "q");
        var purgedTarget = await SeedMessageAsync(context, purgedConv, sequence: 2, "assistant", "purged-answer");
        await SeedFeedbackAsync(context, purgedTarget, purgedConv, NodeMessageFeedbackRating.Up);

        // A live conversation with a DOWN rating → excluded.
        var downConv = Guid.NewGuid();
        await SeedConversationAsync(context, downConv, agentId, "Down", purged: false);
        await SeedMessageAsync(context, downConv, sequence: 1, "user", "q");
        var downTarget = await SeedMessageAsync(context, downConv, sequence: 2, "assistant", "down-answer");
        await SeedFeedbackAsync(context, downTarget, downConv, NodeMessageFeedbackRating.Down);

        var store = new GoldenHarvestSourceStore(context);
        var sources = await store.ListThumbsUpSourcesAsync(agentId, maxScan: 50);

        AssertEx.Empty(sources, "A purged conversation and a down rating are both excluded from the thumbs-up scan.");
    }

    [Test]
    public async Task ListThumbsUpSourcesAsync_ExcludesNonCompletedAndNonUserAssistantPriorTurns()
    {
        var databasePath = GetDatabasePath("filtered-priors.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();

        var agentId = await SeedAgentAsync(context, "Agent A");
        var conversationId = Guid.NewGuid();
        await SeedConversationAsync(context, conversationId, agentId, "Conv", purged: false);

        await SeedMessageAsync(context, conversationId, sequence: 1, "user", "kept-user");
        // A non-completed prior message must be excluded from PriorTurns.
        await SeedMessageAsync(context, conversationId, sequence: 2, "assistant", "streaming-assistant", NodeMessageStatus.Streaming);
        // A 'system' and a 'tool' role prior message must be excluded (only user/assistant kept).
        await SeedMessageAsync(context, conversationId, sequence: 3, "system", "system-prompt");
        await SeedMessageAsync(context, conversationId, sequence: 4, "tool", "tool-output");
        await SeedMessageAsync(context, conversationId, sequence: 5, "assistant", "kept-assistant");
        var targetMessageId = await SeedMessageAsync(context, conversationId, sequence: 6, "assistant", "FINAL");
        await SeedFeedbackAsync(context, targetMessageId, conversationId, NodeMessageFeedbackRating.Up);

        var store = new GoldenHarvestSourceStore(context);
        var sources = await store.ListThumbsUpSourcesAsync(agentId, maxScan: 50);

        AssertEx.Equal(expected: 1, sources.Count);
        var priorTurns = sources[0].PriorTurns;

        // Only the completed user + completed assistant turns survive: the streaming assistant, system, and tool rows
        // are filtered out.
        AssertEx.Equal(expected: 2, priorTurns.Count);
        AssertEx.Equal("kept-user", priorTurns[0].Text);
        AssertEx.Equal("kept-assistant", priorTurns[1].Text);
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

    private static async Task SeedConversationAsync(NodeChatDbContext context, Guid conversationId, Guid agentId, string title, bool purged)
    {
        // Conversation columns are plaintext (structural); seed through EF in the same encrypted context so the FK to
        // agent_definitions is satisfied and the messages can attach.
        context.Conversations.Add(new NodeConversation
        {
            ConversationId = conversationId,
            Title = Encoding.UTF8.GetBytes(title),
            UserId = "node",
            CreatedAtUtc = 10,
            LastSeenUtc = 10,
            Purged = purged,
            AgentDefinitionId = agentId
        });
        await context.SaveChangesAsync();
    }

    private static async Task<Guid> SeedMessageAsync(NodeChatDbContext context,
        Guid conversationId,
        int sequence,
        string role,
        string text,
        string status = NodeMessageStatus.Completed)
    {
        // Seed message content through the encrypted context: the save-changes interceptor encrypts Content at rest, so
        // the store reading it back via EF proves the materialization interceptor decrypts it.
        var messageId = Guid.NewGuid();
        context.Messages.Add(new NodeMessage
        {
            MessageId = messageId,
            ConversationId = conversationId,
            Sequence = sequence,
            Role = role,
            Content = Encoding.UTF8.GetBytes(text),
            CreatedAtUtc = 10 + sequence,
            UpdatedAtUtc = 10 + sequence,
            Status = status
        });
        await context.SaveChangesAsync();
        return messageId;
    }

    private static async Task SeedFeedbackAsync(NodeChatDbContext context, Guid messageId, Guid conversationId, string rating)
    {
        // The thumbs-up scan reads plaintext feedback columns via raw ADO; seed the row with raw ADO over the open
        // connection (the rated message already exists, satisfying the FK).
        var connection = context.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
                              INSERT INTO message_feedback (message_id, conversation_id, rating, comment, created_at_utc, updated_at_utc)
                              VALUES ($message_id, $conversation_id, $rating, NULL, $created_at_utc, $created_at_utc);
                              """;
        AddParameter(command, "$message_id", messageId);
        AddParameter(command, "$conversation_id", conversationId);
        AddParameter(command, "$rating", rating);
        AddParameter(command, "$created_at_utc", value: 100L);
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
