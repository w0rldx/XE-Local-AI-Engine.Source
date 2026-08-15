namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     <c>AddConversationCompactionSummary</c> adds the three columns that make compaction non-destructive: the
///     encrypted summary blob, the sequence it covers up to, and when it was written. All three are needed together —
///     a summary without its watermark cannot be resumed from, so the reader would re-send the full history.
/// </summary>
public sealed class AddConversationCompactionSummaryMigrationTests
{
    [Test]
    public async Task Migrate_ToLatest_AddsCompactionSummaryColumnsToConversations()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("compaction-summary.sqlite").ConfigureAwait(false);

        var columns = await probe.ColumnsAsync("conversations").ConfigureAwait(false);
        AssertEx.True(columns.IsSupersetOf(new[]
        {
            "compaction_summary",
            "compaction_summary_covers_to_sequence",
            "compaction_summary_updated_at_utc"
        }), "conversations must carry the full compaction-summary triple.");
    }
}
