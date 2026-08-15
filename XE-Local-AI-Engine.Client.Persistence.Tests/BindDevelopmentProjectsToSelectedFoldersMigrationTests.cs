namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     <c>BindDevelopmentProjectsToSelectedFolders</c> ties a development project to the folder the operator explicitly
///     granted. The RESTRICT foreign key is the point: a granted folder cannot be deleted out from under a project that
///     is still bound to it, so the workspace root can never silently become unauthorized.
/// </summary>
public sealed class BindDevelopmentProjectsToSelectedFoldersMigrationTests
{
    [Test]
    public async Task Migrate_ToLatest_BindsProjectsToTheGrantedFolder()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("development-selected-folder-binding.sqlite").ConfigureAwait(false);

        var columns = await probe.ColumnsAsync("development_projects").ConfigureAwait(false);

        AssertEx.True(columns.Contains("selected_folder_id"), "development_projects must carry the granted-folder binding.");

        AssertEx.True(await probe.ForeignKeyExistsAsync("development_projects", "selected_folder_id", "selected_folders").ConfigureAwait(false),
            "The binding must be a real foreign key into selected_folders, not a loose Guid.");

        AssertEx.True(await probe.IndexExistsAsync("development_projects",
                "ix_development_projects_selected_folder_id",
                unique: false,
                "selected_folder_id").ConfigureAwait(false),
            "The per-folder project lookup must be indexed.");
    }
}
