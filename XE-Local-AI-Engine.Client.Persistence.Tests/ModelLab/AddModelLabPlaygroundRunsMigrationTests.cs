namespace XE_Local_AI_Engine.Client.Persistence.Tests.ModelLab;

using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary><c>AddModelLabPlaygroundRuns</c> adds the training stepper's run table and its creation-order index, and drops both on a one-step rollback.</summary>
[Category(TestCategories.Integration)]
public sealed class AddModelLabPlaygroundRunsMigrationTests
{
    private const string PreviousMigrationId = "20261116222444_AddModelLabExperimentAttentionBlob";

    private const string MigrationId = "20261125213607_AddModelLabPlaygroundRuns";

    private const string Table = "model_lab_playground_runs";

    [Test]
    public async Task Upgrade_AddsTheTable_ItsColumns_AndTheCreationIndex()
    {
        await using var probe = await MigrationSchemaProbe.FromChatTemplateAsync("playground-up.sqlite", PreviousMigrationId);
        AssertEx.False(await probe.TableExistsAsync(Table), "The table must not exist before this migration.");

        await probe.MigrateToAsync(MigrationId);

        AssertEx.True(await probe.TableExistsAsync(Table));
        var columns = await probe.ColumnsAsync(Table);
        AssertEx.True(columns.SetEquals(["id", "template_id", "created_at_utc", "trace_blob_id", "trace_sha256", "trace_byte_count", "result_json"]),
            string.Join(", ", columns));
        AssertEx.True(await probe.IndexExistsAsync(Table, "ix_model_lab_playground_runs_created", unique: false, "created_at_utc"));
    }

    [Test]
    public async Task RollbackOneStep_DropsTheTable_AndKeepsTheExperiments()
    {
        await using var probe = await MigrationSchemaProbe.FromChatTemplateAsync("playground-down.sqlite", MigrationId);

        await probe.MigrateToAsync(PreviousMigrationId);

        AssertEx.False(await probe.TableExistsAsync(Table));
        AssertEx.True(await probe.TableExistsAsync("model_lab_experiments"));
    }

    [Test]
    public async Task Model_HasNoPendingChangesAgainstSnapshot()
    {
        await using var probe = await MigrationSchemaProbe.FromChatTemplateAsync("playground-drift.sqlite");
        using var keyHolder = new NullNodeSqliteKeyHolder();
        await using var context = AgentDefinitionTestContextFactory.CreateForMigration(probe.DatabasePath, keyHolder);

        AssertEx.False(context.Database.HasPendingModelChanges(), "The NodeChat model has drifted from the latest migration snapshot; regenerate the migration.");
    }
}
