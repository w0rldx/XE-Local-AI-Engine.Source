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
        await using var probe = await MigrationSchemaProbe.FromChatTemplateAsync("external-apps-schema.sqlite");

        AssertEx.True(await probe.TableExistsAsync("external_app_instances"));
        AssertEx.True(await probe.TableExistsAsync("external_app_instance_events"));

        // The catalog's remote copy is cached as a FILE under the node data directory (ruling R1-23). A table here
        // would mean someone re-added the entity the ruling removed.
        AssertEx.False(await probe.TableExistsAsync("external_app_catalog_cache"),
            "The catalog cache is a file, not a table; a table would be a second, diverging copy of the catalog.");

        AssertEx.Equal("BLOB", await ColumnTypeAsync(probe, "external_app_instances", "variables_json"),
            "variables_json holds AES-GCM output — nonce ‖ ciphertext ‖ tag — and a TEXT column would mangle it.");
        AssertEx.Equal("TEXT", await ColumnTypeAsync(probe, "external_app_instances", "published_ports_json"));
        AssertEx.Equal("TEXT", await ColumnTypeAsync(probe, "external_app_instances", "runtime_override"),
            "The runtime pin is text, not an enum: this project cannot see the containers layer's ContainerRuntimeSelection.");
        AssertEx.Equal("TEXT", await ColumnTypeAsync(probe, "external_app_instances", "manifest_snapshot_json"),
            "The installed manifest is plaintext on purpose: the update flow diffs it against the catalog's.");
        AssertEx.Equal("INTEGER", await ColumnTypeAsync(probe, "external_app_instances", "needs_recreate"));
        AssertEx.Equal("0", await probe.ColumnDefaultAsync("external_app_instances", "needs_recreate"),
            "A row that predates a configure owes no rebuild.");
        AssertEx.Equal("TEXT", await ColumnTypeAsync(probe, "external_app_instance_events", "detail_json"),
            "The event detail is content-free and plaintext by design, which is what lets the hub replay it as it stands.");

        AssertEx.True(await probe.IndexExistsAsync("external_app_instances", "ix_external_app_instances_application", unique: false, "application_id"));
        AssertEx.True(await probe.IndexExistsAsync("external_app_instances", "ix_external_app_instances_status", unique: false, "status", "installed_at_utc"));
        AssertEx.True(await probe.IndexExistsAsync("external_app_instance_events",
                                     "ux_external_app_instance_events_instance_sequence",
                                     unique: true,
                                     "instance_id",
                                     "sequence"));
        AssertEx.True(await probe.ForeignKeyExistsAsync("external_app_instance_events", "instance_id", "external_app_instances"),
            "Declared for parity with integration_execution_events, and enforced at runtime: the node connection sets Foreign Keys=True and PRAGMA foreign_keys=ON.");

        // No unique index on application_id: decision D13 keeps the schema N:1 and the one-per-application rule is the
        // install gate's. A unique index added here would turn a race into a 500 instead of the 409 the gate answers.
        AssertEx.False(await probe.IndexExistsAsync("external_app_instances", "ux_external_app_instances_application", unique: true, "application_id"));
    }

    [Test]
    public async Task Migration_ChainsOffDropCanvasWorkflowsAndItsDownTakesExactlyTheTwoTables()
    {
        // Up to the predecessor first: neither table may exist yet, which is what proves the ordering rather than
        // merely asserting the file name.
        await using var probe = await MigrationSchemaProbe.FromChatTemplateAsync("external-apps-chain.sqlite", PreviousMigrationId);

        AssertEx.False(await probe.TableExistsAsync("external_app_instances"));
        AssertEx.False(await probe.TableExistsAsync("external_app_instance_events"));

        await probe.MigrateToAsync(targetMigration: null);

        var applied = await probe.AppliedMigrationsAsync(identityContext: false);
        AssertEx.True(applied.Contains(PreviousMigrationId), "The predecessor must still be in the chain — a rebased migration that skipped it would drift the snapshot.");
        AssertEx.True(applied.Contains(MigrationId));
        AssertEx.True(await probe.TableExistsAsync("external_app_instances"));
        AssertEx.True(await probe.TableExistsAsync("external_app_instance_events"));

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

        await probe.MigrateToAsync(PreviousMigrationId);

        AssertEx.False(await probe.TableExistsAsync("external_app_instances"), "Down must drop the instance table.");
        AssertEx.False(await probe.TableExistsAsync("external_app_instance_events"), "and its event table.");
        foreach (var table in siblingTables)
        {
            AssertEx.True(await probe.TableExistsAsync(table), $"Down must leave {table} standing — it drops exactly the two tables Up created.");
        }
    }

    private static async Task<string> ColumnTypeAsync(MigrationSchemaProbe probe, string tableName, string columnName)
    {
        var type = await probe.ScalarAsync("SELECT type FROM pragma_table_info($table) WHERE name = $column;",
                                  command =>
                                  {
                                      _ = command.Parameters.AddWithValue("$table", tableName);
                                      _ = command.Parameters.AddWithValue("$column", columnName);
                                  });
        return AssertEx.NotNull(type as string, $"{tableName}.{columnName} does not exist.");
    }
}
