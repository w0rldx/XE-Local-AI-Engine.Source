namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     <c>AddNodeConversationSelectedPath</c> adds the per-conversation branch selection that the chat UI replays a
///     thread from. A conversation without the column falls back to the newest leaf, so losing it silently changes
///     which messages a reopened thread shows.
/// </summary>
public sealed class AddNodeConversationSelectedPathMigrationTests
{
    [Test]
    public async Task Migrate_ToLatest_AddsSelectedPathJsonToConversations()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("selected-path.sqlite").ConfigureAwait(false);

        var columns = await probe.ColumnsAsync("conversations").ConfigureAwait(false);

        AssertEx.True(columns.Contains("selected_path_json"), "conversations must carry the selected-path column.");
    }
}
