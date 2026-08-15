namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     <c>AddModelProviderMapRevision</c> stamps every existing mapping row as <c>legacy</c> via a NOT NULL column with
///     a literal default. The default is the whole point: without it the migration cannot add a required column to a
///     populated table, and every pre-existing mapping would lose its revision identity.
/// </summary>
public sealed class AddModelProviderMapRevisionMigrationTests
{
    [Test]
    public async Task Migrate_ToLatest_AddsRevisionDefaultedToLegacy()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("model-provider-map-revision.sqlite").ConfigureAwait(false);

        var columns = await probe.ColumnsAsync("model_provider_map").ConfigureAwait(false);

        AssertEx.True(columns.Contains("revision"), "model_provider_map must carry the revision column.");

        var declaredDefault = await probe.ColumnDefaultAsync("model_provider_map", "revision").ConfigureAwait(false);
        AssertEx.True(declaredDefault?.Contains("legacy", StringComparison.Ordinal) == true,
            $"Existing rows must be stamped 'legacy'; the declared default is '{declaredDefault}'.");
    }
}
