namespace XE_Local_AI_Engine.Client.Persistence.Tests.Benchmarks;

using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     <c>AddBenchmarkLaunchIdentityScheme</c> stamps the launch-identity scheme a row was frozen under, so an intended
///     hash and an effective hash computed under different schemes are never compared. The columns are nullable on
///     purpose: a row frozen before the cutover carries NULL, and the guard reads NULL as scheme 1.
///     <para>
///         The rollback is the load-bearing half. It is step 4 of the downgrade runbook
///         (<c>docs/runbooks/benchmark-launch-identity-scheme-downgrade-runbook.md</c>), run on a node that has already
///         been quiesced and drained — a <c>Down</c> that fails there fails at the worst possible moment.
///     </para>
/// </summary>
public sealed class AddBenchmarkLaunchIdentitySchemeMigrationTests
{
    private const string PreviousMigrationId = "20260903104044_AddIntegrationFoundation";
    private const string ThisMigrationId = "20260904121650_AddAiTrendsWave";

    private static readonly (string Table, string Column)[] SchemeColumns =
    [
        ("benchmark_runs", "primary_launch_identity_scheme"),
        ("benchmark_judge_attempts", "launch_identity_scheme"),
        ("benchmark_comparisons", "launch_identity_scheme")
    ];

    [Test]
    public async Task Migrate_FromThePrecedingMigration_AddsTheThreeSchemeColumnsAsNullable()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("benchmark-launch-identity-scheme-up.sqlite", PreviousMigrationId).ConfigureAwait(false);

        foreach (var (table, column) in SchemeColumns)
        {
            AssertEx.False((await probe.ColumnsAsync(table).ConfigureAwait(false)).Contains(column), $"{table}.{column} must not exist before the migration.");
        }

        await probe.MigrateToAsync(ThisMigrationId).ConfigureAwait(false);

        foreach (var (table, column) in SchemeColumns)
        {
            AssertEx.True((await probe.ColumnsAsync(table).ConfigureAwait(false)).Contains(column), $"The migration must add {table}.{column}.");
            AssertEx.Equal(expected: 0L,
                (await probe.LongsAsync($"SELECT \"notnull\" FROM pragma_table_info('{table}') WHERE name = '{column}';").ConfigureAwait(false)).Single(),
                $"{table}.{column} must be nullable: a row frozen before the cutover has no scheme, and NULL is what the guard reads as scheme 1.");
            AssertEx.Null(await probe.ColumnDefaultAsync(table, column).ConfigureAwait(false), $"{table}.{column} must carry no default, so an un-stamped row stays un-stamped.");
        }
    }

    [Test]
    public async Task Migrate_ToLatest_CarriesTheSchemeColumnsAndRecordsTheMigration()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("benchmark-launch-identity-scheme-head.sqlite").ConfigureAwait(false);

        foreach (var (table, column) in SchemeColumns)
        {
            AssertEx.True((await probe.ColumnsAsync(table).ConfigureAwait(false)).Contains(column), $"A fresh box must end up with {table}.{column}.");
        }

        AssertEx.True((await probe.AppliedMigrationsAsync(identityContext: false).ConfigureAwait(false)).Contains(ThisMigrationId),
            "The scheme migration must be part of the chat chain a fresh box applies.");
    }

    [Test]
    public async Task Migrate_WhenRolledBack_DropsOnlyTheSchemeColumns()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("benchmark-launch-identity-scheme-down.sqlite").ConfigureAwait(false);

        await probe.MigrateToAsync(PreviousMigrationId).ConfigureAwait(false);

        foreach (var (table, column) in SchemeColumns)
        {
            AssertEx.False((await probe.ColumnsAsync(table).ConfigureAwait(false)).Contains(column), $"Rollback must drop {table}.{column}.");
        }

        // The three drops are three SQLite table rebuilds. The neighbouring launch-evidence columns are what a rolled-back
        // build still reads, so losing one of them here would be silent until an operator opened a run's evidence panel.
        AssertEx.True((await probe.ColumnsAsync("benchmark_runs").ConfigureAwait(false)).IsSupersetOf(new[]
        {
            "primary_intended_launch_identity",
            "primary_effective_launch_identity",
            "primary_launch_executable_sha256"
        }), "Rollback must leave the launch-evidence columns on benchmark_runs intact.");
        AssertEx.True((await probe.ColumnsAsync("benchmark_judge_attempts").ConfigureAwait(false)).Contains("launch_receipt_json"),
            "Rollback must leave the judge attempt's launch receipt intact.");
        AssertEx.True((await probe.ColumnsAsync("benchmark_comparisons").ConfigureAwait(false)).Contains("launch_receipt_json"),
            "Rollback must leave the comparison's launch receipt intact.");
    }
}
