namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     BE-08: guards against the exact drift <see cref="ConversationFootprintPurge" />'s remarks warn about — FK
///     cascades are off on the node-sqlite connection, so a table keyed by <c>conversation_id</c> (or
///     <c>message_id</c>) that is added to the EF model but never added to the purge helper would silently orphan
///     encrypted rows on a retention purge. This walks the live EF model rather than a migrated schema so the failure
///     fires the moment a new conversation/message-keyed entity configuration is added, before a migration even exists.
/// </summary>
public sealed class ConversationFootprintPurgeCoverageTests : IDisposable
{
    private readonly INodeSqliteKeyHolder _keyHolder = new NullNodeSqliteKeyHolder();
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }

        _keyHolder.Dispose();
    }

    [Test]
    public async Task CoveredChildTables_MatchesEveryConversationOrMessageKeyedTableInTheModel()
    {
        var databasePath = Path.Combine(_rootPath, "coverage.sqlite");
        await using var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder);

        var discovered = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entityType in context.Model.GetEntityTypes())
        {
            var tableName = entityType.GetTableName();
            if (tableName is null || tableName == "conversations")
            {
                // "conversations" is the root row DeleteAsync deletes last, not a child table — it is intentionally
                // excluded from CoveredChildTables (see that member's remarks).
                continue;
            }

            var storeObject = StoreObjectIdentifier.Table(tableName, entityType.GetSchema());
            var isConversationOrMessageKeyed = entityType.GetProperties()
                                                          .Any(property =>
                                                              property.GetColumnName(storeObject) is "conversation_id" or "message_id");

            if (isConversationOrMessageKeyed)
            {
                discovered.Add(tableName);
            }
        }

        AssertEx.True(discovered.Count > 0,
            "Sanity check failed: no conversation/message-keyed table was discovered in the model — the column-name " +
            "enumeration above is likely broken, which would make the coverage assertions below vacuously pass.");

        var covered = new HashSet<string>(ConversationFootprintPurge.CoveredChildTables, StringComparer.Ordinal);

        var uncovered = discovered.Except(covered, StringComparer.Ordinal).ToArray();
        AssertEx.Empty(uncovered,
            $"Table(s) keyed by conversation_id/message_id are not deleted by ConversationFootprintPurge, so a retention " +
            $"purge would orphan their rows: {string.Join(", ", uncovered)}. Add the missing delete(s) to " +
            $"ConversationFootprintPurge.DeleteAsync and list the table(s) in CoveredChildTables.");

        var stale = covered.Except(discovered, StringComparer.Ordinal).ToArray();
        AssertEx.Empty(stale,
            $"ConversationFootprintPurge.CoveredChildTables lists table(s) that are no longer keyed by " +
            $"conversation_id/message_id in the model: {string.Join(", ", stale)}. Update the list to match.");
    }
}
