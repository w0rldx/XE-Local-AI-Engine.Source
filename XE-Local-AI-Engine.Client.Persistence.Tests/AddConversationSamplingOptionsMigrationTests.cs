namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary><c>AddConversationSamplingOptions</c> adds the nullable plain-JSON sampling settings to conversations.</summary>
[Category(TestCategories.Integration)]
public sealed class AddConversationSamplingOptionsMigrationTests
{
    [Test]
    public async Task Migrate_ToLatest_AddsTheSamplingOptionsColumnToConversations()
    {
        await using var probe = await MigrationSchemaProbe.FromChatTemplateAsync("conversation-sampling-options.sqlite");

        var columns = await probe.ColumnsAsync("conversations");
        AssertEx.True(columns.Contains("sampling_options_json"), "conversations must carry the per-conversation sampling settings.");
    }
}
