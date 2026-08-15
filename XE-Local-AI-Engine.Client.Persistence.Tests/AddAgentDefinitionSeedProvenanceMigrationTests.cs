namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using Microsoft.Data.Sqlite;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     <c>AddAgentDefinitionSeedProvenance</c> is what makes seeding the built-in agent gallery idempotent: the seed
///     slug carries a unique index <b>filtered to non-null values</b>, so re-running the seeder cannot duplicate a
///     template, while the many operator-authored definitions — which have no slug at all — stay unconstrained. Drop
///     the filter and the second user-authored agent fails to save; drop the uniqueness and every startup re-seeds.
///     The columns themselves are asserted at head by <c>AddAgentDefinitionsMigrationTests</c>; this suite owns the
///     index semantics, which nothing else pins.
/// </summary>
public sealed class AddAgentDefinitionSeedProvenanceMigrationTests
{
    private const string ThisMigrationId = "20260602195614_AddAgentDefinitionSeedProvenance";

    [Test]
    public async Task Migrate_ToThisMigration_AddsTheProvenanceColumnsDefaultingToUserAuthored()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("seed-provenance.sqlite", ThisMigrationId).ConfigureAwait(false);

        var columns = await probe.ColumnsAsync("agent_definitions").ConfigureAwait(false);
        AssertEx.True(columns.Contains("seed_slug"), "agent_definitions.seed_slug must be added.");
        AssertEx.True(columns.Contains("source"), "agent_definitions.source must be added.");

        // Source 0 is the user-authored origin: an existing definition predates seeding and must never be mistaken for
        // a template the seeder owns and may overwrite.
        AssertEx.Equal("0", await probe.ColumnDefaultAsync("agent_definitions", "source").ConfigureAwait(false));

        AssertEx.True(await probe.IndexExistsAsync("agent_definitions", "IX_agent_definitions_seed_slug", unique: true, "seed_slug").ConfigureAwait(false),
            "The seed slug must carry a unique index.");
    }

    [Test]
    public async Task Migrate_ToThisMigration_UniquelyIndexesSeededSlugsWithoutConstrainingUnseededDefinitions()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("seed-provenance-index.sqlite", ThisMigrationId).ConfigureAwait(false);

        // Two operator-authored definitions, neither seeded: the filter has to let both through.
        await InsertDefinitionAsync(probe, "First", seedSlug: null).ConfigureAwait(false);
        await InsertDefinitionAsync(probe, "Second", seedSlug: null).ConfigureAwait(false);

        await InsertDefinitionAsync(probe, "Researcher", "researcher").ConfigureAwait(false);

        await AssertEx.ThrowsAsync<SqliteException>(() => InsertDefinitionAsync(probe, "Researcher (again)", "researcher"),
            "A second definition claiming the same seed slug must be rejected — that is what makes re-seeding idempotent.").ConfigureAwait(false);

        AssertEx.Equal(expected: 3L, (await probe.LongsAsync("SELECT COUNT(*) FROM agent_definitions;").ConfigureAwait(false)).Single());
    }

    private static Task InsertDefinitionAsync(MigrationSchemaProbe probe, string name, string? seedSlug)
    {
        return probe.ExecuteAsync("""
                                  INSERT INTO agent_definitions
                                      (id, name, instructions, allowed_tool_names_json, tool_approvals_json, version,
                                       created_at_utc, updated_at_utc, seed_slug)
                                  VALUES ($id, $name, X'00', '[]', '{}', 1, 1234, 1234, $seed_slug);
                                  """,
            command =>
            {
                command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
                command.Parameters.AddWithValue("$name", name);
                command.Parameters.AddWithValue("$seed_slug", seedSlug is null ? DBNull.Value : seedSlug);
            });
    }
}
