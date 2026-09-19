namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using System.Data.Common;
using System.Runtime.InteropServices;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Sqlite;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Raw-SQL aggregate read over the node-local chat database. Mirrors the chat persistence read
///     idiom (parameterized ADO over the scoped <see cref="NodeChatDbContext" /> connection) rather than EF entity
///     materialization, because the aggregate touches only plaintext columns and spans every conversation — the
///     per-conversation write key on the persistence writer is irrelevant to a whole-database read.
/// </summary>
public sealed class FeedbackInsightsStore : IFeedbackInsightsStore
{
    private const string RatingDown = "down";

    private readonly NodeChatDbContext _dbContext;

    public FeedbackInsightsStore(NodeChatDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    public async Task<AgentFeedbackAggregate?> GetAgentFeedbackAggregateAsync(Guid agentDefinitionId, int exemplarCap, CancellationToken cancellationToken = default)
    {
        // Resolve the agent name through EF (scalar projection — no entity is materialized, so the encrypted
        // Instructions/Description are never decrypted) rather than raw SQL: this both avoids decryption and
        // guarantees the id comparison uses EF's own Guid↔TEXT binding for the EF-written agent_definitions.id.
        var agentName = await _dbContext.Set<AgentDefinition>()
                                        .AsNoTracking()
                                        .Where(agent => agent.Id == agentDefinitionId)
                                        .Select(agent => agent.Name)
                                        .FirstOrDefaultAsync(cancellationToken);
        if (agentName is null)
        {
            return null;
        }

        var connection = _dbContext.Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken);

        var (upCount, downCount) = await ReadOverallAsync(connection, agentDefinitionId, cancellationToken);
        var byTool = await ReadByToolAsync(connection, agentDefinitionId, cancellationToken);
        var exemplars = await ReadExemplarsAsync(connection, agentDefinitionId, exemplarCap, cancellationToken);

        return new AgentFeedbackAggregate { AgentDefinitionId = agentDefinitionId, AgentName = agentName, UpCount = upCount, DownCount = downCount, ByTool = byTool, Exemplars = exemplars };
    }

    private static async Task<VoteCounts> ReadOverallAsync(DbConnection connection, Guid agentDefinitionId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT f.rating, COUNT(*) AS cnt
                              FROM message_feedback f
                              JOIN messages m ON m.message_id = f.message_id
                              JOIN conversations c ON c.conversation_id = f.conversation_id
                              WHERE m.agent_definition_id = $agent_id AND c.purged = 0
                              GROUP BY f.rating;
                              """;
        AddParameter(command, "$agent_id", agentDefinitionId);

        var up = 0;
        var down = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var rating = reader.GetString(0);
            var count = reader.GetInt32(1);
            if (string.Equals(rating, RatingDown, StringComparison.Ordinal))
            {
                down += count;
            }
            else
            {
                up += count;
            }
        }

        return new VoteCounts(up, down);
    }

    private static async Task<IReadOnlyList<ToolFeedbackCount>> ReadByToolAsync(DbConnection connection, Guid agentDefinitionId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT te.tool_name, f.rating, COUNT(DISTINCT f.message_id) AS cnt
                              FROM message_feedback f
                              JOIN messages m ON m.message_id = f.message_id
                              JOIN conversations c ON c.conversation_id = f.conversation_id
                              JOIN tool_events te ON te.conversation_id = c.conversation_id
                              WHERE m.agent_definition_id = $agent_id AND c.purged = 0
                              GROUP BY te.tool_name, f.rating;
                              """;
        AddParameter(command, "$agent_id", agentDefinitionId);

        var byTool = new Dictionary<string, VoteCounts>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var toolName = reader.GetString(0);
            var rating = reader.GetString(1);
            var count = reader.GetInt32(2);
            var (up, down) = byTool.TryGetValue(toolName, out var existing) ? existing : new VoteCounts(UpCount: 0, DownCount: 0);
            byTool[toolName] = string.Equals(rating, RatingDown, StringComparison.Ordinal)
                ? new VoteCounts(up, down + count)
                : new VoteCounts(up + count, down);
        }

        return byTool
               .Select(static entry => new ToolFeedbackCount { ToolName = entry.Key, UpCount = entry.Value.UpCount, DownCount = entry.Value.DownCount })
               .OrderByDescending(static tool => tool.UpCount + tool.DownCount)
               .ThenBy(static tool => tool.ToolName, StringComparer.Ordinal)
               .ToArray();
    }

    private static async Task<IReadOnlyList<FeedbackExemplar>> ReadExemplarsAsync(DbConnection connection, Guid agentDefinitionId, int exemplarCap, CancellationToken cancellationToken)
    {
        if (exemplarCap <= 0)
        {
            return [];
        }

        await using var command = connection.CreateCommand();
        // Down-with-comment first (the actionable negative signal), then newest. Only rows that carry a comment are
        // exemplars; a rating without a comment adds no qualitative signal.
        command.CommandText = """
                              SELECT f.rating, f.comment, f.message_id, f.conversation_id, f.created_at_utc
                              FROM message_feedback f
                              JOIN messages m ON m.message_id = f.message_id
                              JOIN conversations c ON c.conversation_id = f.conversation_id
                              WHERE m.agent_definition_id = $agent_id AND c.purged = 0
                                AND f.comment IS NOT NULL AND TRIM(f.comment) <> ''
                              ORDER BY (f.rating = 'down') DESC, f.created_at_utc DESC, f.message_id DESC
                              LIMIT $cap;
                              """;
        AddParameter(command, "$agent_id", agentDefinitionId);
        AddParameter(command, "$cap", exemplarCap);

        var exemplars = new List<FeedbackExemplar>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            exemplars.Add(new FeedbackExemplar
            {
                Rating = reader.GetString(0),
                Comment = reader.GetString(1),
                MessageId = Guid.Parse(reader.GetString(2)),
                ConversationId = Guid.Parse(reader.GetString(3)),
                CreatedAtUtc = reader.GetInt64(4)
            });
        }

        return exemplars;
    }

    private static Task OpenIfNeededAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        // Open-if-needed AND apply the shared WAL/busy_timeout/synchronous pragmas on the open.
        return NodeSqlitePragmas.OpenAndConfigureAsync(connection, cancellationToken);
    }

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    /// <summary>Thumbs-up / thumbs-down tallies for one scope (the whole agent, or a single tool).</summary>
    [StructLayout(LayoutKind.Auto)]
    private readonly record struct VoteCounts(int UpCount, int DownCount);
}
