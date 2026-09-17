namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     <c>AddMessageAgentDefinitionId</c> records which agent produced a message, plus the index the attribution lookup
///     and the per-agent purge both scan.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class AddMessageAgentDefinitionIdMigrationTests
{
    [Test]
    public async Task Migrate_ToLatest_AddsAgentDefinitionIdWithItsIndex()
    {
        await using var probe = await MigrationSchemaProbe.FromChatTemplateAsync("message-agent-definition-id.sqlite");

        var columns = await probe.ColumnsAsync("messages");

        AssertEx.True(columns.Contains("agent_definition_id"), "messages must carry the agent attribution column.");

        AssertEx.True(await probe.IndexExistsAsync("messages",
                "IX_messages_agent_definition_id",
                unique: false,
                "agent_definition_id"),
            "messages.agent_definition_id must be indexed.");
    }
}
