namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     <c>AddTutorialState</c> adds the per-user onboarding tour state. It lives on the IDENTITY context, not the chat
///     context, which is the easy thing to get wrong when adding the next column near it.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class AddTutorialStateMigrationTests
{
    [Test]
    public async Task Migrate_ToLatest_AddsTutorialStateToAspNetUsers()
    {
        await using var probe = await MigrationSchemaProbe.FromIdentityTemplateAsync("tutorial-state.sqlite");

        var columns = await probe.ColumnsAsync("AspNetUsers");

        AssertEx.True(columns.Contains("tutorial_state"), "AspNetUsers must carry the tutorial state column.");
    }
}
