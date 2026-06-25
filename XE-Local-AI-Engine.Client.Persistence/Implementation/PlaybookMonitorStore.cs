namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Raw-SQL cohort monitor over the node-local chat database. Mirrors
///     <see cref="FeedbackInsightsStore" />: parameterized ADO over the scoped <see cref="NodeChatDbContext" />
///     connection rather than EF entity materialization, because the aggregate touches only plaintext columns and
///     spans every conversation for the agent — the per-conversation write key on the persistence writer is irrelevant
///     to a whole-database read. Computed on read; there is no snapshot table.
/// </summary>
public sealed class PlaybookMonitorStore(NodeChatDbContext dbContext) : IPlaybookMonitorStore
{
    private const string RatingDown = "down";

    private readonly NodeChatDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));

    public async Task<CohortComparison> GetCohortComparisonAsync(Guid agentDefinitionId, long enabledAtUtc, string? toolScope, CancellationToken cancellationToken = default)
    {
        var connection = _dbContext.Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken).ConfigureAwait(false);

        return toolScope is null
            ? await ReadOverallAsync(connection, agentDefinitionId, enabledAtUtc, cancellationToken).ConfigureAwait(false)
            : await ReadByToolAsync(connection, agentDefinitionId, enabledAtUtc, toolScope, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<CohortComparison> ReadOverallAsync(DbConnection connection, Guid agentDefinitionId, long enabledAtUtc, CancellationToken cancellationToken)
    {
        // Conditional aggregation splits the two windows in a single pass: <enabledAt is "before", the rest is "after".
        // The down-rate is derived in the service; the store returns the raw totals and down counts only.
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT
                                  SUM(CASE WHEN f.created_at_utc < $enabled_at THEN 1 ELSE 0 END) AS before_total,
                                  SUM(CASE WHEN f.created_at_utc < $enabled_at AND f.rating = $down THEN 1 ELSE 0 END) AS before_down,
                                  SUM(CASE WHEN f.created_at_utc >= $enabled_at THEN 1 ELSE 0 END) AS after_total,
                                  SUM(CASE WHEN f.created_at_utc >= $enabled_at AND f.rating = $down THEN 1 ELSE 0 END) AS after_down
                              FROM message_feedback f
                              JOIN conversations c ON c.conversation_id = f.conversation_id
                              WHERE c.agent_definition_id = $agent_id AND c.purged = 0;
                              """;
        AddParameter(command, "$agent_id", agentDefinitionId);
        AddParameter(command, "$enabled_at", enabledAtUtc);
        AddParameter(command, "$down", RatingDown);

        return await ReadComparisonAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<CohortComparison> ReadByToolAsync(DbConnection connection, Guid agentDefinitionId, long enabledAtUtc, string toolScope, CancellationToken cancellationToken)
    {
        // Facet path: restrict to conversations that recorded a tool_events row for the scoped tool. tool_events has no
        // message link, so attribution is conversation-level — COUNT(DISTINCT message_id) keeps a tool used many times
        // in a conversation from inflating each rated message beyond one (the conversation-level attribution limit).
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT
                                  COUNT(DISTINCT CASE WHEN f.created_at_utc < $enabled_at THEN f.message_id END) AS before_total,
                                  COUNT(DISTINCT CASE WHEN f.created_at_utc < $enabled_at AND f.rating = $down THEN f.message_id END) AS before_down,
                                  COUNT(DISTINCT CASE WHEN f.created_at_utc >= $enabled_at THEN f.message_id END) AS after_total,
                                  COUNT(DISTINCT CASE WHEN f.created_at_utc >= $enabled_at AND f.rating = $down THEN f.message_id END) AS after_down
                              FROM message_feedback f
                              JOIN conversations c ON c.conversation_id = f.conversation_id
                              JOIN tool_events te ON te.conversation_id = c.conversation_id
                              WHERE c.agent_definition_id = $agent_id AND c.purged = 0 AND te.tool_name = $tool_name;
                              """;
        AddParameter(command, "$agent_id", agentDefinitionId);
        AddParameter(command, "$enabled_at", enabledAtUtc);
        AddParameter(command, "$down", RatingDown);
        AddParameter(command, "$tool_name", toolScope);

        return await ReadComparisonAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<CohortComparison> ReadComparisonAsync(DbCommand command, CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return new CohortComparison(BeforeTotal: 0, BeforeDown: 0, AfterTotal: 0, AfterDown: 0);
        }

        // SUM over no rows yields SQL NULL; the aggregate-row read coalesces each column to 0.
        return new CohortComparison(ReadCount(reader, ordinal: 0),
            ReadCount(reader, ordinal: 1),
            ReadCount(reader, ordinal: 2),
            ReadCount(reader, ordinal: 3));
    }

    private static int ReadCount(DbDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? 0 : Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    }

    private static async Task OpenIfNeededAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
