namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     <c>AddTutorialState</c> adds the per-user onboarding tour state. It lives on the IDENTITY context, not the chat
///     context, which is the easy thing to get wrong when adding the next column near it.
/// </summary>
public sealed class AddTutorialStateMigrationTests
{
    [Test]
    public async Task Migrate_ToLatest_AddsTutorialStateToAspNetUsers()
    {
        await using var probe = await MigrationSchemaProbe.MigrateIdentityAsync("tutorial-state.sqlite").ConfigureAwait(false);

        var columns = await probe.ColumnsAsync("AspNetUsers").ConfigureAwait(false);

        AssertEx.True(columns.Contains("tutorial_state"), "AspNetUsers must carry the tutorial state column.");
    }
}
