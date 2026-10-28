namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     <c>AddDevelopmentAttemptCommandProfile</c> adds the per-attempt command-profile snapshot as plaintext TEXT,
///     nullable and unbackfilled (null means "predates this column"; readers use the project's profile), pinned so
///     an encrypted-BLOB "fix" fails.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class AddDevelopmentAttemptCommandProfileMigrationTests
{
    [Test]
    public async Task Migrate_ToLatest_AddsNullablePlaintextCommandProfileJson()
    {
        await using var probe = await MigrationSchemaProbe.FromChatTemplateAsync("development-attempt-command-profile.sqlite");

        var columns = await probe.ColumnsAsync("development_attempts");

        AssertEx.True(columns.Contains("command_profile_json"),
            "development_attempts must carry the per-attempt command-profile snapshot.");

        AssertEx.Null(await probe.ColumnDefaultAsync("development_attempts", "command_profile_json"),
            "The column is deliberately not backfilled, so it must declare no default.");
    }
}
