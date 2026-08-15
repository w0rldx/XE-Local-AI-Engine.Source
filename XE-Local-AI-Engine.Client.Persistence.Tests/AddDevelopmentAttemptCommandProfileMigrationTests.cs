namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     <c>AddDevelopmentAttemptCommandProfile</c> adds the immutable per-attempt snapshot of the command profile the
///     attempt ran under. Deliberately plaintext TEXT and deliberately nullable-and-unbackfilled (a null means "predates
///     this column", which readers resolve against the project's profile) — the migration's own comment says so, and
///     this test pins the shape so a later "fix" to an encrypted BLOB fails loudly.
/// </summary>
public sealed class AddDevelopmentAttemptCommandProfileMigrationTests
{
    [Test]
    public async Task Migrate_ToLatest_AddsNullablePlaintextCommandProfileJson()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("development-attempt-command-profile.sqlite").ConfigureAwait(false);

        var columns = await probe.ColumnsAsync("development_attempts").ConfigureAwait(false);

        AssertEx.True(columns.Contains("command_profile_json"),
            "development_attempts must carry the per-attempt command-profile snapshot.");

        AssertEx.Null(await probe.ColumnDefaultAsync("development_attempts", "command_profile_json").ConfigureAwait(false),
            "The column is deliberately not backfilled, so it must declare no default.");
    }
}
