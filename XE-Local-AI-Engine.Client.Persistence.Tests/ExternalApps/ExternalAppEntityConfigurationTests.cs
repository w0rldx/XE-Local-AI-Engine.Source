namespace XE_Local_AI_Engine.Client.Persistence.Tests.ExternalApps;

using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     The Fluent mappings, through a real context and a real SQLite file: the snake_case column names the rest of the
///     schema uses, the enums stored as text, and the two mappings the store's correctness rests on — the unique
///     <c>(instance_id, sequence)</c> index and <c>version</c> as a concurrency token.
/// </summary>
public sealed class ExternalAppEntityConfigurationTests
{
    [Test]
    public async Task InstanceTable_MapsEveryColumnToItsSnakeCaseName()
    {
        using var fixture = new ExternalAppTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);

        var expected = new[]
        {
            "id",
            "application_id",
            "manifest_version",
            "manifest_snapshot_json",
            "display_name",
            "status",
            "desired_state",
            "runtime_override",
            "runtime_provider",
            "variables_json",
            "bridge_token",
            "published_ports_json",
            "storage_path",
            "failure_category",
            "failure_summary",
            "needs_recreate",
            "installed_at_utc",
            "started_at_utc",
            "stopped_at_utc",
            "updated_at_utc",
            "last_sequence",
            "version"
        };

        AssertEx.Empty(expected.Except(ColumnsOf<ExternalAppInstance>(context, "external_app_instances"), StringComparer.Ordinal),
            "A property left without HasColumnName lands as PascalCase and no test that reads through the model would notice.");
        AssertEx.Empty(ColumnsOf<ExternalAppInstance>(context, "external_app_instances").Except(expected, StringComparer.Ordinal),
            "A column the table has and this list does not means the entity gained a property nobody reviewed.");
    }

    [Test]
    public async Task EventTable_MapsEveryColumnToItsSnakeCaseName()
    {
        using var fixture = new ExternalAppTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);

        var expected = new[]
        {
            "id",
            "instance_id",
            "sequence",
            "kind",
            "detail_json",
            "occurred_at_utc"
        };

        AssertEx.Empty(expected.Except(ColumnsOf<ExternalAppInstanceEvent>(context, "external_app_instance_events"), StringComparer.Ordinal));
        AssertEx.Empty(ColumnsOf<ExternalAppInstanceEvent>(context, "external_app_instance_events").Except(expected, StringComparer.Ordinal));
    }

    [Test]
    public async Task StatusDesiredStateFailureCategoryAndKind_AreStoredAsText()
    {
        using var fixture = new ExternalAppTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = new ExternalAppInstanceStore(context);
        var command = ExternalAppTestFixture.Create();
        var created = await store.CreateAsync(command).ConfigureAwait(false);
        _ = await store.UpdateStatusAsync(new ExternalAppStatusUpdate(command.Id,
                             created.Version,
                             new HashSet<ExternalAppInstanceStatus>
                             {
                                 ExternalAppInstanceStatus.Installing
                             },
                             ExternalAppInstanceStatus.Failed,
                             ExternalAppInstanceEventKind.Failed,
                             EventDetailJson: null,
                             OccurredAtUtc: 2_000,
                             ExternalAppDesiredState.Running,
                             FailureCategory: ExternalAppFailureCategory.ImagePullFailed,
                             FailureSummary: "The image could not be pulled at its pinned digest."))
                       .ConfigureAwait(false);

        // Text rather than the ordinal: an enum stored as a number silently re-labels every existing row the day a
        // member is inserted in the middle, and these values are read by operators in the database file.
        AssertEx.Equal("Failed", await ScalarTextAsync(fixture, "SELECT status FROM external_app_instances;").ConfigureAwait(false));
        AssertEx.Equal("Running", await ScalarTextAsync(fixture, "SELECT desired_state FROM external_app_instances;").ConfigureAwait(false));
        AssertEx.Equal("ImagePullFailed", await ScalarTextAsync(fixture, "SELECT failure_category FROM external_app_instances;").ConfigureAwait(false));
        AssertEx.Equal("Failed", await ScalarTextAsync(fixture, "SELECT kind FROM external_app_instance_events WHERE sequence = 2;").ConfigureAwait(false));
    }

    [Test]
    public async Task NeedsRecreate_DefaultsToFalseAndRoundTrips()
    {
        using var fixture = new ExternalAppTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = new ExternalAppInstanceStore(context);
        var command = ExternalAppTestFixture.Create();
        var created = await store.CreateAsync(command).ConfigureAwait(false);

        AssertEx.Equal(expected: 0L, Convert.ToInt64(await fixture.RawScalarAsync("SELECT needs_recreate FROM external_app_instances;").ConfigureAwait(false),
            CultureInfo.InvariantCulture));

        _ = await store.UpdateVariablesAsync(command.Id, created.Version, """{"ODYSSEUS_ADMIN_PASSWORD":"rotated"}""", updatedAtUtc: 3_000).ConfigureAwait(false);

        AssertEx.Equal(expected: 1L, Convert.ToInt64(await fixture.RawScalarAsync("SELECT needs_recreate FROM external_app_instances;").ConfigureAwait(false),
            CultureInfo.InvariantCulture));
    }

    [Test]
    public async Task EventSequence_IsUniquePerInstance()
    {
        using var fixture = new ExternalAppTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var command = ExternalAppTestFixture.Create();
        _ = await new ExternalAppInstanceStore(context).CreateAsync(command).ConfigureAwait(false);

        _ = context.ExternalAppInstanceEvents.Add(new ExternalAppInstanceEvent
        {
            Id = Guid.NewGuid(),
            InstanceId = command.Id,
            Sequence = 1,
            Kind = ExternalAppInstanceEventKind.Installed,
            DetailJson = null,
            OccurredAtUtc = 2_000
        });

        var failure = await AssertEx.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync()).ConfigureAwait(false);
        _ = AssertEx.NotNull(failure.InnerException as SqliteException,
            "A duplicate (instance_id, sequence) means two writers minted the same sequence — a bug the database has to refuse, not a race to swallow.");
    }

    [Test]
    public async Task Version_IsAConcurrencyToken()
    {
        using var fixture = new ExternalAppTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);

        var property = AssertEx.NotNull(context.Model.FindEntityType(typeof(ExternalAppInstance))?.FindProperty(nameof(ExternalAppInstance.Version)));

        AssertEx.True(property.IsConcurrencyToken,
            "Without the token the store's query-first check is not atomic and two concurrent compare-and-swaps both win.");
    }

    private static IReadOnlySet<string> ColumnsOf<TEntity>(NodeChatDbContext context, string tableName)
        where TEntity : class
    {
        var entityType = AssertEx.NotNull(context.Model.FindEntityType(typeof(TEntity)));
        AssertEx.Equal(tableName, entityType.GetTableName());

        var storeObject = StoreObjectIdentifier.Table(tableName, entityType.GetSchema());
        return new HashSet<string>(entityType.GetProperties().Select(property => property.GetColumnName(storeObject)!), StringComparer.Ordinal);
    }

    private static async Task<string> ScalarTextAsync(ExternalAppTestFixture fixture, string sql) =>
        AssertEx.NotNull(await fixture.RawScalarAsync(sql).ConfigureAwait(false) as string, $"'{sql}' returned no text.");
}
