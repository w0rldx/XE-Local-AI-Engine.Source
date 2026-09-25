namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     <c>AddConversationState</c> adds the distilled-state triple: the encrypted state JSON, the anchor sequence it
///     covers up to, and when it was written. The watermark is what lets the next distillation resume, so the three
///     are only useful together.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class AddConversationStateMigrationTests
{
    [Test]
    public async Task Migrate_ToLatest_AddsConversationStateColumnsToConversations()
    {
        await using var probe = await MigrationSchemaProbe.FromChatTemplateAsync("conversation-state.sqlite");

        var columns = await probe.ColumnsAsync("conversations");
        AssertEx.True(columns.IsSupersetOf(new[]
        {
            "conversation_state",
            "conversation_state_covers_to_sequence",
            "conversation_state_updated_at_utc"
        }), "conversations must carry the full conversation-state triple.");
    }
}
