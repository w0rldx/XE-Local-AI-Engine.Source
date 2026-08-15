namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     <c>AddSlashCommands</c> creates the slash-command table. <c>name</c> is NOCASE and uniquely indexed, so
///     <c>/Deploy</c> and <c>/deploy</c> cannot both exist and the picker cannot show an ambiguous pair.
/// </summary>
public sealed class AddSlashCommandsMigrationTests
{
    [Test]
    public async Task Migrate_ToLatest_CreatesSlashCommandsWithUniqueNameIndex()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("slash-commands.sqlite").ConfigureAwait(false);

        AssertEx.True(await probe.TableExistsAsync("slash_commands").ConfigureAwait(false), "slash_commands must exist.");

        var columns = await probe.ColumnsAsync("slash_commands").ConfigureAwait(false);
        AssertEx.True(columns.IsSupersetOf(new[]
        {
            "id",
            "name",
            "description",
            "action_type",
            "action_configuration",
            "created_at_utc",
            "updated_at_utc"
        }), "slash_commands must expose the mapped columns.");

        AssertEx.True(await probe.IndexExistsAsync("slash_commands", "IX_slash_commands_name", unique: true, "name").ConfigureAwait(false),
            "slash_commands.name must be uniquely indexed.");
    }
}
