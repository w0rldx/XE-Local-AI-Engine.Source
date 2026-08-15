namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using Microsoft.Data.Sqlite;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     <c>AddNodeSelectedFolders</c> creates the operator's folder grants — the only rows that say which host paths the
///     product may touch. Two invariants are load-bearing and asserted here: the alias is uniquely indexed (everything
///     above persistence addresses a grant by alias, so two rows sharing one would make the resolved folder ambiguous),
///     and the host path is a BLOB because it is encrypted at rest rather than stored as a readable path.
/// </summary>
public sealed class AddNodeSelectedFoldersMigrationTests
{
    private const string ThisMigrationId = "20260529173005_AddNodeSelectedFolders";

    [Test]
    public async Task Migrate_ToThisMigration_CreatesSelectedFoldersWithAnEncryptedHostPath()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("selected-folders.sqlite", ThisMigrationId).ConfigureAwait(false);

        AssertEx.True(await probe.TableExistsAsync("selected_folders").ConfigureAwait(false), "selected_folders must exist.");

        AssertEx.True((await probe.ColumnsAsync("selected_folders").ConfigureAwait(false)).SetEquals(new[]
        {
            "id",
            "alias",
            "host_path",
            "mode",
            "created_at_utc"
        }), "selected_folders must expose exactly the columns this migration created.");

        AssertEx.Equal("BLOB", await ColumnTypeAsync(probe, "host_path").ConfigureAwait(false),
            "The host path must be a BLOB — it is sealed by the node cipher, never a readable path.");

        // A grant with no explicit mode is the most restrictive one, not an unbounded one.
        AssertEx.Equal("0", await probe.ColumnDefaultAsync("selected_folders", "mode").ConfigureAwait(false));
    }

    [Test]
    public async Task Migrate_ToThisMigration_RejectsASecondGrantUnderTheSameAlias()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("selected-folders-alias.sqlite", ThisMigrationId).ConfigureAwait(false);

        AssertEx.True(await probe.IndexExistsAsync("selected_folders", "IX_selected_folders_alias", unique: true, "alias").ConfigureAwait(false),
            "The alias must be uniquely indexed.");

        await InsertGrantAsync(probe, "projects").ConfigureAwait(false);

        await AssertEx.ThrowsAsync<SqliteException>(() => InsertGrantAsync(probe, "projects"),
            "A second grant under the same alias must be rejected by the database, not merely by the service above it.").ConfigureAwait(false);
    }

    private static Task InsertGrantAsync(MigrationSchemaProbe probe, string alias)
    {
        return probe.ExecuteAsync("""
                                  INSERT INTO selected_folders (id, alias, host_path, mode, created_at_utc)
                                  VALUES ($id, $alias, X'00', 0, 1234);
                                  """,
            command =>
            {
                command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
                command.Parameters.AddWithValue("$alias", alias);
            });
    }

    private static async Task<string> ColumnTypeAsync(MigrationSchemaProbe probe, string columnName)
    {
        var value = await probe.ScalarAsync("SELECT type FROM pragma_table_info('selected_folders') WHERE name = $column;",
            command => command.Parameters.AddWithValue("$column", columnName)).ConfigureAwait(false);

        return AssertEx.NotNull(value as string, $"selected_folders.{columnName} must exist.");
    }
}
