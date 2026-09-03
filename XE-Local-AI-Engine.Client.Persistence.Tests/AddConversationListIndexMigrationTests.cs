namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     Verifies the <c>AddConversationListIndex</c> migration. <c>conversations</c> is the busiest table in the node and
///     had no secondary index at all, so both variants of the conversation-list query paid a full <c>SCAN</c> plus a
///     <c>USE TEMP B-TREE FOR ORDER BY</c> — and because the list join runs a correlated last-message subquery per row,
///     that sort cost one subquery per conversation instead of <c>limit</c> of them.
///     <para>
///         The column order is the load-bearing part, which is why these tests assert the planner's own verdict and not
///         merely that an index exists. <c>archived</c> sorts LAST: placed second it would serve the active-only query
///         and leave the show-all query (which does not constrain it) sorting every non-purged row.
///     </para>
/// </summary>
public sealed class AddConversationListIndexMigrationTests
{
    private const string IndexName = "ix_conversations_list";

    /// <summary>
    ///     The join shared by both list queries in <c>NodeChatReadModel</c>, whose two list methods are the only
    ///     readers of this index. The bound limit is spelled as a literal below because EXPLAIN takes no parameters,
    ///     and its value does not enter the plan. These copies grade the real queries, so if those change shape these
    ///     have to move with them.
    /// </summary>
    private const string ListJoin = """
                                    FROM conversations c
                                    LEFT JOIN messages m ON m.message_id = (
                                        SELECT mi.message_id FROM messages mi
                                        WHERE mi.conversation_id = c.conversation_id
                                        ORDER BY mi.sequence DESC LIMIT 1)
                                    """;

    private const string ActiveQuery = $"""
                                        SELECT c.conversation_id {ListJoin}
                                        WHERE c.purged = 0 AND c.archived = 0 AND c.kind = 'chat'
                                        ORDER BY c.is_pinned DESC, c.last_seen_utc DESC
                                        LIMIT 50;
                                        """;

    private const string AllQuery = $"""
                                     SELECT c.conversation_id {ListJoin}
                                     WHERE c.purged = 0 AND c.kind = 'chat'
                                     ORDER BY c.is_pinned DESC, c.last_seen_utc DESC
                                     LIMIT 50;
                                     """;

    [Test]
    public async Task MigrateToHead_CreatesTheListIndexInTheDeclaredColumnOrder()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("conversation-list-index.sqlite");

        AssertEx.True(await probe.IndexExistsAsync("conversations", IndexName, unique: false, "purged", "is_pinned", "last_seen_utc", "archived"),
            $"{IndexName} must exist on conversations over (purged, is_pinned, last_seen_utc, archived), in that order.");
    }

    [Test]
    public async Task ActiveListQuery_UsesTheIndexAndNeedsNoSort()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("conversation-list-active-plan.sqlite");

        AssertPlanUsesIndex(await probe.QueryPlanAsync(ActiveQuery), "the active-only list query");
    }

    [Test]
    public async Task ShowAllListQuery_UsesTheIndexAndNeedsNoSort()
    {
        // The reason `archived` is the trailing column. This assertion fails on the intuitive
        // (purged, archived, is_pinned, last_seen_utc) order, which leaves this query with a temp b-tree.
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("conversation-list-all-plan.sqlite");

        AssertPlanUsesIndex(await probe.QueryPlanAsync(AllQuery), "the show-all list query");
    }

    [Test]
    public async Task RollingBackOneStep_DropsTheListIndex()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("conversation-list-index-rollback.sqlite");
        await probe.MigrateToAsync("20260817201241_AddBenchmarkReadiness");

        AssertEx.False(await probe.IndexExistsAsync("conversations", IndexName, unique: false),
            $"Rolling back one migration must drop {IndexName}.");
    }

    private static void AssertPlanUsesIndex(IReadOnlyList<string> plan, string what)
    {
        var rendered = string.Join(" | ", plan);

        AssertEx.True(plan.Any(step => step.Contains(IndexName, StringComparison.Ordinal)),
            $"The planner must drive {what} off {IndexName}. Plan was: {rendered}");

        // The ordered index scan is the whole point: without it SQLite materializes and sorts every matching
        // conversation before the LIMIT can cut it, running the correlated last-message subquery for each one.
        AssertEx.False(plan.Any(static step => step.Contains("TEMP B-TREE", StringComparison.OrdinalIgnoreCase)),
            $"{what} must be answered in index order, with no temp b-tree sort. Plan was: {rendered}");

        AssertEx.False(plan.Any(static step => step.StartsWith("SCAN c", StringComparison.Ordinal)),
            $"{what} must not fall back to a full scan of conversations. Plan was: {rendered}");
    }
}
