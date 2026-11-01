namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary><c>AddPlainConversations</c> adds the plain flag and the encrypted custom system prompt to conversations.</summary>
[Category(TestCategories.Integration)]
public sealed class AddPlainConversationsMigrationTests
{
    [Test]
    public async Task Migrate_ToLatest_AddsPlainColumnsToConversations()
    {
        await using var probe = await MigrationSchemaProbe.FromChatTemplateAsync("plain-conversations.sqlite");

        var columns = await probe.ColumnsAsync("conversations");
        AssertEx.True(columns.IsSupersetOf(new[]
        {
            "is_plain",
            "plain_system_prompt"
        }), "conversations must carry the plain flag and its custom system prompt.");
    }
}
