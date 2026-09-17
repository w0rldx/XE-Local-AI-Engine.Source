namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     Schema coverage for <c>AddExternalApps</c>: the two tables it creates, the column types the encrypted and the
///     plaintext columns must have, the two indexes, and that its <c>Down</c> takes exactly those two tables away
///     again. Also the fence for ruling R1-23 — the catalog cache is a file, so no third table exists.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class AddExternalAppsMigrationTests
{
    private const string PreviousMigrationId = "20260907085114_DropCanvasWorkflows";

    private const string MigrationId = "20260910230421_AddExternalApps";

    [Test]
    public async Task MigrateToHead_CreatesBothExternalAppTablesWithTheirDeclaredColumnsAndIndexes()
    {
        await using var probe = await MigrationSchemaProbe.FromChatTemplateAsync("external-apps-schema.sqlite").ConfigureAwait(false);

        AssertEx.True(await probe.TableExistsAsync("external_app_instances").ConfigureAwait(false));
        AssertEx.True(await probe.TableExistsAsync("external_app_instance_events").ConfigureAwait(false));

        // The catalog's remote copy is cached as a FILE under the node data directory (ruling R1-23). A table here
        // would mean someone re-added the entity the ruling removed.
        AssertEx.False(await probe.TableExistsAsync("external_app_catalog_cache").ConfigureAwait(false),
            "The catalog cache is a file, not a table; a table would be a second, diverging copy of the catalog.");

        AssertEx.Equal("BLOB", await ColumnTypeAsync(probe, "external_app_instances", "variables_json").ConfigureAwait(false),
            "variables_json holds AES-GCM output — nonce ‖ ciphertext ‖ tag — and a TEXT column would mangle it.");
        AssertEx.Equal("TEXT", await ColumnTypeAsync(probe, "external_app_instances", "published_ports_json").ConfigureAwait(false));
        AssertEx.Equal("TEXT", await ColumnTypeAsync(probe, "external_app_instances", "runtime_override").ConfigureAwait(false),
            "The runtime pin is text, not an enum: this project cannot see the containers layer's ContainerRuntimeSelection.");
        AssertEx.Equal("TEXT", await ColumnTypeAsync(probe, "external_app_instances", "manifest_snapshot_json").ConfigureAwait(false),
            "The installed manifest is plaintext on purpose: the update flow diffs it against the catalog's.");
        AssertEx.Equal("INTEGER", await ColumnTypeAsync(probe, "external_app_instances", "needs_recreate").ConfigureAwait(false));
        AssertEx.Equal("0", await probe.ColumnDefaultAsync("external_app_instances", "needs_recreate").ConfigureAwait(false),
            "A row that predates a configure owes no rebuild.");
        AssertEx.Equal("TEXT", await ColumnTypeAsync(probe, "external_app_instance_events", "detail_json").ConfigureAwait(false),
            "The event detail is content-free and plaintext by design, which is what lets the hub replay it as it stands.");

        AssertEx.True(await probe.IndexExistsAsync("external_app_instances", "ix_external_app_instances_application", unique: false, "application_id").ConfigureAwait(false));
        AssertEx.True(await probe.IndexExistsAsync("external_app_instances", "ix_external_app_instances_status", unique: false, "status", "installed_at_utc")
                                 .ConfigureAwait(false));
        AssertEx.True(await probe.IndexExistsAsync("external_app_instance_events",
                                     "ux_external_app_instance_events_instance_sequence",
                                     unique: true,
                                     "instance_id",
                                     "sequence")
                                 .ConfigureAwait(false));
        AssertEx.True(await probe.ForeignKeyExistsAsync("external_app_instance_events", "instance_id", "external_app_instances").ConfigureAwait(false),
            "Declared for parity with integration_execution_events; decorative at runtime because the node connection leaves PRAGMA foreign_keys off.");

        // No unique index on application_id: decision D13 keeps the schema N:1 and the one-per-application rule is the
        // install gate's. A unique index added here would turn a race into a 500 instead of the 409 the gate answers.
        AssertEx.False(await probe.IndexExistsAsync("external_app_instances", "ux_external_app_instances_application", unique: true, "application_id").ConfigureAwait(false));
    }

    [Test]
    public async Task Migration_ChainsOffDropCanvasWorkflowsAndItsDownTakesExactlyTheTwoTables()
    {
        // Up to the predecessor first: neither table may exist yet, which is what proves the ordering rather than
        // merely asserting the file name.
        await using var probe = await MigrationSchemaProbe.FromChatTemplateAsync("external-apps-chain.sqlite", PreviousMigrationId).ConfigureAwait(false);

        AssertEx.False(await probe.TableExistsAsync("external_app_instances").ConfigureAwait(false));
        AssertEx.False(await probe.TableExistsAsync("external_app_instance_events").ConfigureAwait(false));

        await probe.MigrateToAsync(targetMigration: null).ConfigureAwait(false);

        var applied = await probe.AppliedMigrationsAsync(identityContext: false).ConfigureAwait(false);
        AssertEx.True(applied.Contains(PreviousMigrationId), "The predecessor must still be in the chain — a rebased migration that skipped it would drift the snapshot.");
        AssertEx.True(applied.Contains(MigrationId));
        AssertEx.True(await probe.TableExistsAsync("external_app_instances").ConfigureAwait(false));
        AssertEx.True(await probe.TableExistsAsync("external_app_instance_events").ConfigureAwait(false));

        // Down. A SQLite down migration rebuilds tables from its own target model, so a mistake here does not report
        // itself — it silently drops a sibling's column. The sample below is the guard.
        var siblingTables = new[]
        {
            "integration_executions",
            "integration_execution_events",
            "graph_workflow_runs",
            "conversations",
            "messages"
        };

        await probe.MigrateToAsync(PreviousMigrationId).ConfigureAwait(false);

        AssertEx.False(await probe.TableExistsAsync("external_app_instances").ConfigureAwait(false), "Down must drop the instance table.");
        AssertEx.False(await probe.TableExistsAsync("external_app_instance_events").ConfigureAwait(false), "and its event table.");
        foreach (var table in siblingTables)
        {
            AssertEx.True(await probe.TableExistsAsync(table).ConfigureAwait(false), $"Down must leave {table} standing — it drops exactly the two tables Up created.");
        }
    }

    private static async Task<string> ColumnTypeAsync(MigrationSchemaProbe probe, string tableName, string columnName)
    {
        var type = await probe.ScalarAsync("SELECT type FROM pragma_table_info($table) WHERE name = $column;",
                                  command =>
                                  {
                                      _ = command.Parameters.AddWithValue("$table", tableName);
                                      _ = command.Parameters.AddWithValue("$column", columnName);
                                  })
                              .ConfigureAwait(false);
        return AssertEx.NotNull(type as string, $"{tableName}.{columnName} does not exist.");
    }
}
