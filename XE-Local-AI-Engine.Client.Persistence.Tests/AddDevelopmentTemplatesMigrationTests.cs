namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     <c>AddDevelopmentTemplates</c> creates the template registry plus the per-folder materialization record. The
///     unique alias index is what lets an operator refer to a template by a short name without ambiguity.
/// </summary>
public sealed class AddDevelopmentTemplatesMigrationTests
{
    [Test]
    public async Task Migrate_ToLatest_CreatesTemplatesWithUniqueAliasAndMaterializations()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("development-templates.sqlite").ConfigureAwait(false);

        AssertEx.True(await probe.TableExistsAsync("development_templates").ConfigureAwait(false), "development_templates must exist.");
        AssertEx.True(await probe.TableExistsAsync("development_template_materializations").ConfigureAwait(false),
            "development_template_materializations must exist.");

        AssertEx.True((await probe.ColumnsAsync("development_templates").ConfigureAwait(false)).IsSupersetOf(new[]
        {
            "id",
            "alias",
            "host_path",
            "created_at_utc",
            "version"
        }), "development_templates must expose the mapped columns.");

        AssertEx.True((await probe.ColumnsAsync("development_template_materializations").ConfigureAwait(false)).IsSupersetOf(new[]
        {
            "selected_folder_id",
            "template_id",
            "template_alias",
            "template_path",
            "template_commit",
            "created_at_utc"
        }), "development_template_materializations must record which template landed in which granted folder.");

        AssertEx.True(await probe.IndexExistsAsync("development_templates",
                "ux_development_templates_alias",
                unique: true,
                "alias").ConfigureAwait(false),
            "A template alias must be unique.");

        AssertEx.True(await probe.ForeignKeyExistsAsync("development_template_materializations", "selected_folder_id", "selected_folders")
                                 .ConfigureAwait(false),
            "A materialization must be foreign-keyed to the granted folder it landed in.");
    }
}
