namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     <c>AddConversationCompactionSummary</c> adds what makes compaction non-destructive: the encrypted summary, the
///     sequence it covers, and when it was written. Without the watermark the reader re-sends the full history.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class AddConversationCompactionSummaryMigrationTests
{
    [Test]
    public async Task Migrate_ToLatest_AddsCompactionSummaryColumnsToConversations()
    {
        await using var probe = await MigrationSchemaProbe.FromChatTemplateAsync("compaction-summary.sqlite");

        var columns = await probe.ColumnsAsync("conversations");
        AssertEx.True(columns.IsSupersetOf(new[]
        {
            "compaction_summary",
            "compaction_summary_covers_to_sequence",
            "compaction_summary_updated_at_utc"
        }), "conversations must carry the full compaction-summary triple.");
    }
}
