namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     <c>AddCustomTools</c> creates the operator-authored tool table. The unique index on <c>name</c> is the control
///     that stops two tools resolving to the same <c>custom__</c> function name, which the model routes against.
/// </summary>
public sealed class AddCustomToolsMigrationTests
{
    [Test]
    public async Task Migrate_ToLatest_CreatesCustomToolsWithUniqueNameIndex()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("custom-tools.sqlite").ConfigureAwait(false);

        AssertEx.True(await probe.TableExistsAsync("custom_tools").ConfigureAwait(false), "custom_tools must exist.");

        var columns = await probe.ColumnsAsync("custom_tools").ConfigureAwait(false);
        AssertEx.True(columns.IsSupersetOf(new[]
        {
            "id",
            "name",
            "description",
            "kind",
            "mode",
            "parameters_json",
            "config_json",
            "enabled",
            "acknowledged",
            "version",
            "created_at_utc",
            "updated_at_utc"
        }), "custom_tools must expose the mapped columns.");

        AssertEx.True(await probe.IndexExistsAsync("custom_tools", "IX_custom_tools_name", unique: true, "name").ConfigureAwait(false),
            "custom_tools.name must be uniquely indexed.");
    }
}
