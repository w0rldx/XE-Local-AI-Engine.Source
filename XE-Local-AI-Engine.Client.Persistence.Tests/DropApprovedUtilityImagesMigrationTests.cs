namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     <c>DropApprovedUtilityImages</c> removes the last schema trace of the container-based model-fit probe that the
///     Docker teardown deleted. A drop migration is the easy one to get wrong twice — reintroducing the table, or
///     leaving it in place while believing it gone — so this pins BOTH ends: present at the migration before it, absent
///     at the head of the chain.
/// </summary>
public sealed class DropApprovedUtilityImagesMigrationTests
{
    private const string PriorMigrationId = "20260714161306_AddRunEnvelopeDurabilityColumns";

    [Test]
    public async Task Migrate_ToPriorMigration_StillHasApprovedUtilityImages()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("approved-utility-images-before.sqlite", PriorMigrationId)
                                                          .ConfigureAwait(false);

        AssertEx.True(await probe.TableExistsAsync("approved_utility_images").ConfigureAwait(false),
            "The table must still exist one migration before the drop, or this test is not measuring the drop.");
    }

    [Test]
    public async Task Migrate_ToLatest_DropsApprovedUtilityImages()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("approved-utility-images-after.sqlite").ConfigureAwait(false);

        AssertEx.False(await probe.TableExistsAsync("approved_utility_images").ConfigureAwait(false),
            "approved_utility_images must be gone; Docker is off the inference path and stays there.");
    }
}
