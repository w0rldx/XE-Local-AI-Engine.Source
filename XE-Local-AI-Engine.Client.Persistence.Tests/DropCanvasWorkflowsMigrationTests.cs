namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     <c>DropCanvasWorkflows</c> removes the last schema trace of Open Canvas, whose saved rows the one-shot import
///     carries into Graph Workflows before this migration runs. A drop migration is the easy one to get wrong twice —
///     reintroducing the table, or leaving it in place while believing it gone — so this pins BOTH ends: present at the
///     migration before it, absent at the head of the chain.
/// </summary>
public sealed class DropCanvasWorkflowsMigrationTests
{
    private const string PriorMigrationId = "20260904234758_AddVramAtLoadTelemetry";

    [Test]
    public async Task Migrate_ToPriorMigration_StillHasCanvasWorkflows()
    {
        await using var probe = await MigrationSchemaProbe.FromChatTemplateAsync("canvas-workflows-before.sqlite", PriorMigrationId)
                                                          .ConfigureAwait(false);

        AssertEx.True(await probe.TableExistsAsync("canvas_workflows").ConfigureAwait(false),
            "The table must still exist one migration before the drop, or this test is not measuring the drop.");
    }

    [Test]
    public async Task Migrate_ToLatest_DropsCanvasWorkflows()
    {
        await using var probe = await MigrationSchemaProbe.FromChatTemplateAsync("canvas-workflows-after.sqlite").ConfigureAwait(false);

        AssertEx.False(await probe.TableExistsAsync("canvas_workflows").ConfigureAwait(false),
            "canvas_workflows must be gone; Open Canvas is removed and its rows live on as Graph Workflow definitions.");
    }
}
