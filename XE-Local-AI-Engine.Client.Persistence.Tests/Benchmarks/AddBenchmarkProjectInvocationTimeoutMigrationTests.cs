namespace XE_Local_AI_Engine.Client.Persistence.Tests.Benchmarks;

using Microsoft.Data.Sqlite;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     <c>AddBenchmarkProjectInvocationTimeout</c> adds the operator-tunable generation budget and the copy frozen onto
///     each run. Both nullable: null means the node's frozen default, so every existing project and run keeps reading
///     without a backfill that would claim a budget nobody chose.
/// </summary>
public sealed class AddBenchmarkProjectInvocationTimeoutMigrationTests
{
    [Test]
    public async Task Migrate_ToLatest_AddsTheTimeoutToBothTheProjectAndTheRun()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("benchmark-invocation-timeout.sqlite").ConfigureAwait(false);

        AssertEx.True((await probe.ColumnsAsync("benchmark_projects").ConfigureAwait(false)).Contains("invocation_timeout_seconds"),
            "The project carries the operator's setting.");
        AssertEx.True((await probe.ColumnsAsync("benchmark_runs").ConfigureAwait(false)).Contains("invocation_timeout_seconds"),
            "The run carries the frozen copy it replays under.");
        AssertEx.Null(await probe.ColumnDefaultAsync("benchmark_projects", "invocation_timeout_seconds").ConfigureAwait(false),
            "An absent budget is the node default, which is NULL — never a defaulted number.");
    }

    [Test]
    public async Task GenerationTimeout_MustStayInsideItsBounds()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("benchmark-invocation-timeout-bounds.sqlite").ConfigureAwait(false);

        await AssertEx.ThrowsAsync<SqliteException>(() => InsertProjectAsync(probe, timeoutSeconds: 59)).ConfigureAwait(false);
        await AssertEx.ThrowsAsync<SqliteException>(() => InsertProjectAsync(probe, timeoutSeconds: 7201)).ConfigureAwait(false);

        await InsertProjectAsync(probe, timeoutSeconds: 60).ConfigureAwait(false);
        await InsertProjectAsync(probe, timeoutSeconds: 7200).ConfigureAwait(false);
        await InsertProjectAsync(probe, timeoutSeconds: null).ConfigureAwait(false);
    }

    private static Task InsertProjectAsync(MigrationSchemaProbe probe, int? timeoutSeconds) =>
        probe.ExecuteAsync("""
                           INSERT INTO benchmark_projects
                               (id, name, core_task_json, context_tokens, invocation_timeout_seconds, agent_definition_id, version,
                                created_at_utc, updated_at_utc)
                           VALUES ($id, 'p', X'7B7D', 4096, $timeout, $agent, 1, 1, 1);
                           """,
            command =>
            {
                command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
                command.Parameters.AddWithValue("$agent", Guid.NewGuid().ToString());
                command.Parameters.AddWithValue("$timeout", timeoutSeconds is null ? DBNull.Value : timeoutSeconds.Value);
            });
}
