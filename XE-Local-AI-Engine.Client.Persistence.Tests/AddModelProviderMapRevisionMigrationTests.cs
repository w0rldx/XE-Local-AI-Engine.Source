namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     <c>AddModelProviderMapRevision</c> stamps existing mapping rows <c>legacy</c> via a NOT NULL column with a
///     literal default, without which the required column cannot be added to a populated table.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class AddModelProviderMapRevisionMigrationTests
{
    [Test]
    public async Task Migrate_ToLatest_AddsRevisionDefaultedToLegacy()
    {
        await using var probe = await MigrationSchemaProbe.FromChatTemplateAsync("model-provider-map-revision.sqlite");

        var columns = await probe.ColumnsAsync("model_provider_map");

        AssertEx.True(columns.Contains("revision"), "model_provider_map must carry the revision column.");

        var declaredDefault = await probe.ColumnDefaultAsync("model_provider_map", "revision");
        AssertEx.True(declaredDefault?.Contains("legacy", StringComparison.Ordinal) == true,
            $"Existing rows must be stamped 'legacy'; the declared default is '{declaredDefault}'.");
    }
}
