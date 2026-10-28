namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     <c>BindDevelopmentProjectsToSelectedFolders</c> ties a development project to its explicitly granted folder. The
///     RESTRICT foreign key is the point: a bound folder cannot be deleted, so the workspace root never silently
///     becomes unauthorized.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class BindDevelopmentProjectsToSelectedFoldersMigrationTests
{
    [Test]
    public async Task Migrate_ToLatest_BindsProjectsToTheGrantedFolder()
    {
        await using var probe = await MigrationSchemaProbe.FromChatTemplateAsync("development-selected-folder-binding.sqlite");

        var columns = await probe.ColumnsAsync("development_projects");

        AssertEx.True(columns.Contains("selected_folder_id"), "development_projects must carry the granted-folder binding.");

        AssertEx.True(await probe.ForeignKeyExistsAsync("development_projects", "selected_folder_id", "selected_folders"),
            "The binding must be a real foreign key into selected_folders, not a loose Guid.");

        AssertEx.True(await probe.IndexExistsAsync("development_projects",
                "ix_development_projects_selected_folder_id",
                unique: false,
                "selected_folder_id"),
            "The per-folder project lookup must be indexed.");
    }
}
