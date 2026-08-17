namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     <c>AddDevelopmentModeFoundation</c> creates the five Development Mode tables. The two filtered UNIQUE indexes are
///     the load-bearing part and cannot be reconstructed from the entity model: one active attempt per task, and one
///     event per (project, operation, phase) — the idempotency key that makes a retried operation a no-op instead of a
///     duplicated ledger entry.
/// </summary>
public sealed class AddDevelopmentModeFoundationMigrationTests
{
    [Test]
    public async Task Migrate_ToLatest_CreatesTheProjectTaskAttemptArtifactEventChain()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("development-mode-foundation.sqlite").ConfigureAwait(false);

        foreach (var table in new[]
                 {
                     "development_projects",
                     "development_tasks",
                     "development_attempts",
                     "development_artifacts",
                     "development_events"
                 })
        {
            AssertEx.True(await probe.TableExistsAsync(table).ConfigureAwait(false), $"{table} must exist.");
        }

        AssertEx.True((await probe.ColumnsAsync("development_projects").ConfigureAwait(false)).IsSupersetOf(new[]
        {
            "id",
            "objective",
            "repository_identity_hash",
            "base_branch",
            "status",
            "egress_policy",
            "trusted_repository_acknowledged",
            "trusted_repository_policy_version",
            "version"
        }), "development_projects must expose the mapped columns, including the trusted-repository acknowledgement.");

        AssertEx.True((await probe.ColumnsAsync("development_attempts").ConfigureAwait(false)).IsSupersetOf(new[]
        {
            "id",
            "task_id",
            "predecessor_attempt_id",
            "role",
            "model_id",
            "status",
            "terminal_reason",
            "start_operation_id"
        }), "development_attempts must expose the mapped columns.");

        AssertEx.True(await probe.ForeignKeyExistsAsync("development_tasks", "project_id", "development_projects").ConfigureAwait(false),
            "Tasks must be foreign-keyed to their project.");
        AssertEx.True(await probe.ForeignKeyExistsAsync("development_attempts", "task_id", "development_tasks").ConfigureAwait(false),
            "Attempts must be foreign-keyed to their task.");
        AssertEx.True(await probe.ForeignKeyExistsAsync("development_artifacts", "attempt_id", "development_attempts").ConfigureAwait(false),
            "Artifacts must be foreign-keyed to their attempt.");

        AssertEx.True(await probe.IndexExistsAsync("development_attempts",
                "ux_development_attempts_one_active_per_task",
                unique: true,
                "task_id").ConfigureAwait(false),
            "At most one active attempt per task must be enforced by a unique index, not by application code alone.");

        AssertEx.True(await probe.IndexExistsAsync("development_attempts",
                "ux_development_attempts_start_operation_id",
                unique: true,
                "start_operation_id").ConfigureAwait(false),
            "The start-operation idempotency key must be unique.");

        AssertEx.True(await probe.IndexExistsAsync("development_events",
                "ux_development_events_operation_phase",
                unique: true,
                "project_id",
                "operation_id",
                "operation_phase").ConfigureAwait(false),
            "A retried operation phase must collide on a unique index rather than duplicate the ledger.");

        AssertEx.True(await probe.IndexExistsAsync("development_events",
                "ux_development_events_project_sequence",
                unique: true,
                "project_id",
                "sequence").ConfigureAwait(false),
            "The per-project event sequence must be unique.");

        AssertEx.True(await probe.IndexExistsAsync("development_tasks",
                "ux_development_tasks_project_id",
                unique: true,
                "project_id").ConfigureAwait(false),
            "A project carries at most one task.");
    }
}
