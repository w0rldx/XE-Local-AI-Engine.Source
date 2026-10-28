namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     <c>DropApprovedUtilityImages</c> removes the last schema trace of the container-based model-fit probe. A drop is
///     easy to get wrong both ways, so BOTH ends are pinned: present before it, absent at head.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class DropApprovedUtilityImagesMigrationTests
{
    private const string PriorMigrationId = "20260714161306_AddRunEnvelopeDurabilityColumns";

    [Test]
    public async Task Migrate_ToPriorMigration_StillHasApprovedUtilityImages()
    {
        await using var probe = await MigrationSchemaProbe.FromChatTemplateAsync("approved-utility-images-before.sqlite", PriorMigrationId);

        AssertEx.True(await probe.TableExistsAsync("approved_utility_images"),
            "The table must still exist one migration before the drop, or this test is not measuring the drop.");
    }

    [Test]
    public async Task Migrate_ToLatest_DropsApprovedUtilityImages()
    {
        await using var probe = await MigrationSchemaProbe.FromChatTemplateAsync("approved-utility-images-after.sqlite");

        AssertEx.False(await probe.TableExistsAsync("approved_utility_images"),
            "approved_utility_images must be gone; Docker is off the inference path and stays there.");
    }
}
