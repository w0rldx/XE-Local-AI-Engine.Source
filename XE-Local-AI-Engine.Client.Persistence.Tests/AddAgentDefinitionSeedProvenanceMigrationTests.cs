namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using Microsoft.Data.Sqlite;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     <c>AddAgentDefinitionSeedProvenance</c> makes gallery seeding idempotent: the seed slug's unique index is
///     <b>filtered to non-null values</b>, so re-seeding cannot duplicate a template; slug-less rows stay free.
/// </summary>
/// <remarks>
///     Without the filter a second user-authored agent fails to save; without uniqueness every start re-seeds.
///     Columns are asserted by <c>AddAgentDefinitionsMigrationTests</c>; this suite owns the index semantics.
/// </remarks>
[Category(TestCategories.Integration)]
public sealed class AddAgentDefinitionSeedProvenanceMigrationTests
{
    private const string ThisMigrationId = "20260602195614_AddAgentDefinitionSeedProvenance";

    [Test]
    public async Task Migrate_ToThisMigration_AddsTheProvenanceColumnsDefaultingToUserAuthored()
    {
        await using var probe = await MigrationSchemaProbe.FromChatTemplateAsync("seed-provenance.sqlite", ThisMigrationId);

        var columns = await probe.ColumnsAsync("agent_definitions");
        AssertEx.True(columns.Contains("seed_slug"), "agent_definitions.seed_slug must be added.");
        AssertEx.True(columns.Contains("source"), "agent_definitions.source must be added.");

        // Source 0 is the user-authored origin: an existing definition predates seeding and must never be mistaken for
        // a template the seeder owns and may overwrite.
        AssertEx.Equal("0", await probe.ColumnDefaultAsync("agent_definitions", "source"));

        AssertEx.True(await probe.IndexExistsAsync("agent_definitions", "IX_agent_definitions_seed_slug", unique: true, "seed_slug"),
            "The seed slug must carry a unique index.");
    }

    [Test]
    public async Task Migrate_ToThisMigration_UniquelyIndexesSeededSlugsWithoutConstrainingUnseededDefinitions()
    {
        await using var probe = await MigrationSchemaProbe.FromChatTemplateAsync("seed-provenance-index.sqlite", ThisMigrationId);

        // Two operator-authored definitions, neither seeded: the filter has to let both through.
        await InsertDefinitionAsync(probe, "First", seedSlug: null);
        await InsertDefinitionAsync(probe, "Second", seedSlug: null);

        await InsertDefinitionAsync(probe, "Researcher", "researcher");

        await AssertEx.ThrowsAsync<SqliteException>(() => InsertDefinitionAsync(probe, "Researcher (again)", "researcher"),
            "A second definition claiming the same seed slug must be rejected — that is what makes re-seeding idempotent.");

        AssertEx.Equal(expected: 3L, (await probe.LongsAsync("SELECT COUNT(*) FROM agent_definitions;")).Single());
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
