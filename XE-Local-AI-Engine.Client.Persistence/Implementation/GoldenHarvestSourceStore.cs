namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using System.Data.Common;
using System.Text;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Sqlite;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Reconstructs golden-conversation harvest candidates from an agent's thumbs-up assistant turns. Reads node-local
///     data only; never logs turn/answer text (privacy).
/// </summary>
public sealed class GoldenHarvestSourceStore : IGoldenHarvestSourceStore
{
    private const string UserRole = "user";
    private const string AssistantRole = "assistant";

    private readonly NodeChatDbContext _dbContext;

    public GoldenHarvestSourceStore(NodeChatDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyList<HarvestCandidateSource>> ListThumbsUpSourcesAsync(Guid agentDefinitionId, int maxScan, CancellationToken cancellationToken = default)
    {
        var thumbsUp = await ScanThumbsUpAsync(agentDefinitionId, maxScan, cancellationToken);

        var sources = new List<HarvestCandidateSource>(thumbsUp.Count);
        foreach (var row in thumbsUp)
        {
            // Reconstruct turns via EF so the materialization interceptor decrypts message content (the raw-ADO scan
            // above only touched plaintext columns).
            var messages = await _dbContext.Set<NodeMessage>()
                                           .AsNoTracking()
                                           .Where(message => message.ConversationId == row.ConversationId)
                                           .OrderBy(message => message.Sequence)
                                           .ToListAsync(cancellationToken);

            var target = messages.FirstOrDefault(message => message.MessageId == row.MessageId);
            if (target is null)
            {
                continue;
            }

            // Variant/branch simplification (documented MVP limitation): take the linear lower-Sequence completed
            // user/assistant turns; variant siblings of the target are NOT path-filtered. Full selected-path
            // reconstruction is out of MVP scope.
            var priorTurns = messages
                             .Where(message => message.Sequence < target.Sequence
                                               && string.Equals(message.Status, NodeMessageStatus.Completed, StringComparison.Ordinal)
                                               && (string.Equals(message.Role, UserRole, StringComparison.OrdinalIgnoreCase)
                                                   || string.Equals(message.Role, AssistantRole, StringComparison.OrdinalIgnoreCase)))
                             .Select(message => new HarvestTurn { Role = message.Role, Text = Encoding.UTF8.GetString(message.Content) })
                             .ToArray();

            sources.Add(new HarvestCandidateSource
            {
                MessageId = row.MessageId,
                ConversationId = row.ConversationId,
                ConversationTitle = row.Title,
                PriorTurns = priorTurns,
                ApprovedAnswerText = Encoding.UTF8.GetString(target.Content)
            });
        }

        return sources;
    }

    private async Task<IReadOnlyList<ThumbsUpRow>> ScanThumbsUpAsync(Guid agentDefinitionId, int maxScan, CancellationToken cancellationToken)
    {
        await using var command = _dbContext.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
                              SELECT mf.message_id, mf.conversation_id, c.title, mf.created_at_utc
                              FROM message_feedback mf
                              JOIN conversations c ON c.conversation_id = mf.conversation_id
                              WHERE c.agent_definition_id = $agent AND mf.rating = $rating AND c.purged = 0
                              ORDER BY mf.created_at_utc DESC
                              LIMIT $scan;
                              """;
        AddParameter(command, "$agent", agentDefinitionId);
        AddParameter(command, "$rating", NodeMessageFeedbackRating.Up);
        AddParameter(command, "$scan", maxScan);

        await OpenIfNeededAsync(command.Connection, cancellationToken);

        var rows = new List<ThumbsUpRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var conversationId = Guid.Parse(reader.GetString(1));
            var titleBytes = await reader.IsDBNullAsync(ordinal: 2, cancellationToken)
                ? null
                : (byte[])reader.GetValue(2);
            var titleText = _dbContext.DecryptConversationTitle(titleBytes, conversationId);
            rows.Add(new ThumbsUpRow { MessageId = Guid.Parse(reader.GetString(0)), ConversationId = conversationId, Title = titleText });
        }

        return rows;
    }

    private static Task OpenIfNeededAsync(DbConnection? connection, CancellationToken cancellationToken)
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

    private sealed record ThumbsUpRow
    {
        public required Guid MessageId { get; init; }

        public required Guid ConversationId { get; init; }

        public required string? Title { get; init; }
    }
}
