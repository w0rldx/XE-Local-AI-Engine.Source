namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using System.Globalization;
using Microsoft.Data.Sqlite;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     <c>AddBenchmarkTaskItems</c> is the ONE migration the task-suite data layer ships: the item table, the
///     project's item-set hash, and the run's four identity stamps. Three of the stamps are NOT NULL, so the whole
///     suite is really about what happens to the rows that already exist — a legacy run must come out of this
///     migration ranking exactly as it went in.
/// </summary>
public sealed class AddBenchmarkTaskItemsMigrationTests
{
    private const string PreTaskItemsMigrationId = "20260825225103_AddBenchmarkP2Discrimination";
    private const string TaskItemsMigrationId = "20260826102207_AddBenchmarkTaskItems";
    private const string LegacyHash = "v1:legacy";

    private static readonly Guid ProjectId = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid RunId = new("22222222-2222-2222-2222-222222222222");
    private static readonly Guid SecondRunId = new("22222222-2222-2222-2222-222222222223");
    private static readonly Guid AgentId = new("33333333-3333-3333-3333-333333333333");

    [Test]
    public async Task Migrate_CreatesTheItemTableItsIndexesAndTheIdentityStamps()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("benchmark-task-items-up.sqlite").ConfigureAwait(false);

        AssertEx.True(await probe.TableExistsAsync("benchmark_task_items").ConfigureAwait(false), "M1 must create benchmark_task_items.");
        var itemColumns = await probe.ColumnsAsync("benchmark_task_items").ConfigureAwait(false);
        AssertEx.True(itemColumns.IsSupersetOf(new[]
            {
                "id",
                "project_id",
                "parent_item_id",
                "index",
                "kind",
                "revision",
                "input_hash",
                "counts_toward_score",
                "prompt_json",
                "reference_answer_json",
                "verifier_config_json",
                "generator_config_json",
                "version",
                "created_at_utc",
                "updated_at_utc"
            }),
            "A case is an ordinary item, so it needs a parent pointer, its own revision and its own input hash.");

        AssertEx.True(await probe.IndexExistsAsync("benchmark_task_items", "ux_benchmark_task_items_project_index", unique: true, "project_id", "index")
                                 .ConfigureAwait(false),
            "The unique (project, index) index is what makes the legacy item-0 backfill a constraint violation under a race rather than a duplicate.");
        AssertEx.True(await probe.IndexExistsAsync("benchmark_task_items", "ix_benchmark_task_items_parent", unique: false, "parent_item_id").ConfigureAwait(false),
            "Re-expanding a generator has to find its children.");

        var runColumns = await probe.ColumnsAsync("benchmark_runs").ConfigureAwait(false);
        AssertEx.True(runColumns.IsSupersetOf(new[]
            {
                "task_item_id",
                "task_item_index",
                "cell_key",
                "task_input_hash",
                "task_item_set_hash"
            }),
            "All four identity stamps land in this migration, so the freeze slice needs no second one.");

        var projectColumns = await probe.ColumnsAsync("benchmark_projects").ConfigureAwait(false);
        AssertEx.True(projectColumns.Contains("task_item_set_hash"), "The project carries the set hash every run copies at freeze.");

        AssertEx.Equal("'v1:legacy'", await probe.ColumnDefaultAsync("benchmark_runs", "task_input_hash").ConfigureAwait(false),
            "The legacy constant is the DEFAULT precisely so the column can be NOT NULL over existing rows.");
        AssertEx.Equal("'v1:legacy'", await probe.ColumnDefaultAsync("benchmark_runs", "task_item_set_hash").ConfigureAwait(false),
            "Both hash axes take the same constant, and both are compared against it.");
    }

    /// <summary>
    ///     The backfill that makes <c>cell_key</c> NOT NULL possible. A nullable cell key would put every ungrouped run
    ///     of a project into one anonymous bucket and average their scores together, so every pre-existing run becomes
    ///     its own singleton cell — derived, plaintext, and impossible to collide across freezes.
    /// </summary>
    [Test]
    public async Task Migrate_BackfillsEveryExistingRunToItsOwnSingletonCellAndTheLegacyHashes()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("benchmark-task-items-backfill.sqlite", PreTaskItemsMigrationId).ConfigureAwait(false);
        await SeedProjectAndRunsAsync(probe).ConfigureAwait(false);

        await probe.MigrateToAsync(targetMigration: null).ConfigureAwait(false);

        var first = await ScalarStringAsync(probe, "SELECT cell_key FROM benchmark_runs WHERE id = $id;", RunId).ConfigureAwait(false);
        var second = await ScalarStringAsync(probe, "SELECT cell_key FROM benchmark_runs WHERE id = $id;", SecondRunId).ConfigureAwait(false);
        AssertEx.True(first.StartsWith("run:", StringComparison.Ordinal), $"A legacy run's cell is derived from its own id; got '{first}'.");
        AssertEx.True(!string.Equals(first, second, StringComparison.Ordinal), "Two legacy runs of one project must never share a cell.");

        AssertEx.Equal(LegacyHash, await ScalarStringAsync(probe, "SELECT task_input_hash FROM benchmark_runs WHERE id = $id;", RunId).ConfigureAwait(false));
        AssertEx.Equal(LegacyHash, await ScalarStringAsync(probe, "SELECT task_item_set_hash FROM benchmark_runs WHERE id = $id;", RunId).ConfigureAwait(false));

        var empty = await probe.LongsAsync("SELECT COUNT(*) FROM benchmark_runs WHERE cell_key IS NULL OR cell_key = '';").ConfigureAwait(false);
        AssertEx.True(empty[0] == 0, "No run may leave this migration without a cell.");
    }

    /// <summary>
    ///     The acceptance test for the whole slice: an existing single-task project must behave IDENTICALLY after the
    ///     migration. Every column it had is byte-for-byte what it was, the encrypted task blob is untouched, the run
    ///     is alone in its cell, and — because a migration has no node encryption key — no item row was invented for it.
    /// </summary>
    [Test]
    public async Task Migrate_ExistingSingleTaskProject_IsUnchangedAndGetsNoItemRow()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("benchmark-task-items-identical.sqlite", PreTaskItemsMigrationId).ConfigureAwait(false);
        await SeedProjectAndRunsAsync(probe).ConfigureAwait(false);
        var beforeProject = await ScalarStringAsync(probe, "SELECT hex(core_task_json) || '|' || name || '|' || context_tokens || '|' || version FROM benchmark_projects WHERE id = $id;",
                ProjectId)
            .ConfigureAwait(false);
        var beforeRun = await ScalarStringAsync(probe,
                "SELECT hex(runtime_snapshot_json) || '|' || primary_model_name || '|' || primary_status || '|' || version FROM benchmark_runs WHERE id = $id;",
                RunId)
            .ConfigureAwait(false);

        await probe.MigrateToAsync(targetMigration: null).ConfigureAwait(false);

        AssertEx.Equal(beforeProject,
            await ScalarStringAsync(probe, "SELECT hex(core_task_json) || '|' || name || '|' || context_tokens || '|' || version FROM benchmark_projects WHERE id = $id;", ProjectId)
                .ConfigureAwait(false),
            "The migration must not touch what the project asks, nor its version.");
        AssertEx.Equal(beforeRun,
            await ScalarStringAsync(probe,
                    "SELECT hex(runtime_snapshot_json) || '|' || primary_model_name || '|' || primary_status || '|' || version FROM benchmark_runs WHERE id = $id;",
                    RunId)
                .ConfigureAwait(false),
            "A frozen run replays from bytes that this migration must leave alone.");

        var items = await probe.LongsAsync("SELECT COUNT(*) FROM benchmark_task_items;").ConfigureAwait(false);
        AssertEx.True(items[0] == 0,
            "No ENCRYPTED backfill is possible here: a migration has no node key, and prompt_json is AAD-bound to its own item id. Item 0 is materialized by the store.");

        var unstamped = await probe.LongsAsync("SELECT COUNT(*) FROM benchmark_runs WHERE task_item_id IS NOT NULL OR task_item_index IS NOT NULL;").ConfigureAwait(false);
        AssertEx.True(unstamped[0] == 0, "A pre-suite run names no item; it is read as item 0 rather than claiming one.");

        var alone = await probe.LongsAsync("SELECT COUNT(*) FROM benchmark_runs WHERE cell_key = (SELECT cell_key FROM benchmark_runs WHERE id = $id);",
                                   command => command.Parameters.AddWithValue("$id", RunId))
                               .ConfigureAwait(false);
        AssertEx.True(alone[0] == 1, "A legacy run's cell holds exactly itself, so its cell mean is its own score.");

        AssertEx.Null(await probe.ScalarAsync("SELECT task_item_set_hash FROM benchmark_projects WHERE id = $id;",
                                     command => command.Parameters.AddWithValue("$id", ProjectId))
                                 .ConfigureAwait(false) as string,
            "The project's set hash stays null until its first item write.");
    }

    [Test]
    public async Task Migrate_WhenRolledBack_DropsEverythingItAddedAndLeavesTheP2SchemaIntact()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("benchmark-task-items-down.sqlite").ConfigureAwait(false);
        await SeedProjectAndRunsAsync(probe).ConfigureAwait(false);

        await probe.MigrateToAsync(PreTaskItemsMigrationId).ConfigureAwait(false);

        AssertEx.False(await probe.TableExistsAsync("benchmark_task_items").ConfigureAwait(false), "Rollback must drop benchmark_task_items.");

        var runColumns = await probe.ColumnsAsync("benchmark_runs").ConfigureAwait(false);
        AssertEx.False(runColumns.Contains("cell_key"), "Rollback must drop the identity stamps.");
        AssertEx.False(runColumns.Contains("task_input_hash"));
        AssertEx.False(runColumns.Contains("task_item_set_hash"));
        AssertEx.False(runColumns.Contains("task_item_id"));
        AssertEx.False(runColumns.Contains("task_item_index"));

        // The trap this guards: a SQLite down migration rebuilds the table from its own target model, so a sibling
        // column added by a DIFFERENT branch's migration can vanish with it.
        AssertEx.True(runColumns.Contains("perplexity_mean"), "Rollback must leave P2's fidelity projection intact.");
        AssertEx.True(runColumns.Contains("repeat_mode"), "Rollback must leave the preceding migrations' columns intact.");
        AssertEx.False((await probe.ColumnsAsync("benchmark_projects").ConfigureAwait(false)).Contains("task_item_set_hash"));
        AssertEx.True((await probe.ColumnsAsync("benchmark_projects").ConfigureAwait(false)).Contains("fidelity_kld_base_fingerprint"),
            "Rollback must leave P2's project columns intact.");

        var runs = await probe.LongsAsync("SELECT COUNT(*) FROM benchmark_runs;").ConfigureAwait(false);
        AssertEx.True(runs[0] == 2, "Rollback must not lose rows.");
    }

    [Test]
    public async Task Migrate_KindCheck_AcceptsTheWholeVocabularyAndRejectsAnythingElse()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("benchmark-task-items-kind.sqlite").ConfigureAwait(false);
        await SeedProjectAndRunsAsync(probe).ConfigureAwait(false);

        await InsertItemAsync(probe, index: 0, kind: "prompt").ConfigureAwait(false);
        await InsertItemAsync(probe, index: 1, kind: "niah").ConfigureAwait(false);
        await InsertItemAsync(probe, index: 2, kind: "niahCase").ConfigureAwait(false);

        _ = await AssertEx.ThrowsAsync<SqliteException>(() => InsertItemAsync(probe, index: 3, kind: "whatever"),
                              "A kind outside the vocabulary must be refused by the schema, not only by the store.")
                          .ConfigureAwait(false);
        _ = await AssertEx.ThrowsAsync<SqliteException>(() => InsertItemAsync(probe, index: 0, kind: "prompt"),
                              "Two items of one project cannot share an index.")
                          .ConfigureAwait(false);
    }

    [Test]
    public async Task Migrate_RecordsThisMigrationInTheChatChain()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("benchmark-task-items-applied.sqlite").ConfigureAwait(false);

        var applied = await probe.AppliedMigrationsAsync(identityContext: false).ConfigureAwait(false);
        AssertEx.True(applied.Contains(TaskItemsMigrationId), "The task-item migration must be part of the chat chain a fresh box applies.");
    }

    private static Task InsertItemAsync(MigrationSchemaProbe probe, int index, string kind) =>
        probe.ExecuteAsync("""
                           INSERT INTO benchmark_task_items (id, project_id, parent_item_id, "index", kind, revision, input_hash, counts_toward_score,
                                                             prompt_json, version, created_at_utc, updated_at_utc)
                           VALUES ($id, $project, NULL, $index, $kind, 1, 'v1:0', 1, x'00', 1, 1, 1);
                           """, command =>
        {
            command.Parameters.AddWithValue("$id", Guid.NewGuid());
            command.Parameters.AddWithValue("$project", ProjectId);
            command.Parameters.AddWithValue("$index", index);
            command.Parameters.AddWithValue("$kind", kind);
        });

    private static async Task<string> ScalarStringAsync(MigrationSchemaProbe probe, string sql, Guid id)
    {
        var value = await probe.ScalarAsync(sql, command => command.Parameters.AddWithValue("$id", id)).ConfigureAwait(false);
        return AssertEx.NotNull(Convert.ToString(value, CultureInfo.InvariantCulture), "The probed row must exist.");
    }

    /// <summary>
    ///     Two runs of one project, written as SQL rather than through the entity model: the model describes the schema
    ///     at head, and these tests observe it before this migration applies.
    /// </summary>
    private static async Task SeedProjectAndRunsAsync(MigrationSchemaProbe probe)
    {
        await probe.ExecuteAsync("""
                                 INSERT INTO benchmark_projects (id, name, core_task_json, context_tokens, agent_definition_id, version, created_at_utc, updated_at_utc)
                                 VALUES ($project, 'task-item-probe', x'0badc0de', 4096, $agent, 1, 1, 1);
                                 INSERT INTO benchmark_runs (id, project_id, runtime_snapshot_json, primary_model_name, model_content_fingerprint,
                                                             agent_name, agent_version, requested_context_tokens, primary_status, last_stream_sequence,
                                                             is_warmup, version, created_at_utc, updated_at_utc)
                                 VALUES ($run, $project, x'0badbeef', 'probe-model', 'v1:0', 'probe-agent', 1, 4096, 'Succeeded', 0, 0, 3, 1, 1),
                                        ($second, $project, x'0badbeef', 'probe-model', 'v1:0', 'probe-agent', 1, 4096, 'Succeeded', 0, 0, 3, 1, 1);
                                 """, command =>
        {
            command.Parameters.AddWithValue("$project", ProjectId);
            command.Parameters.AddWithValue("$run", RunId);
            command.Parameters.AddWithValue("$second", SecondRunId);
            command.Parameters.AddWithValue("$agent", AgentId);
        });
    }
}
