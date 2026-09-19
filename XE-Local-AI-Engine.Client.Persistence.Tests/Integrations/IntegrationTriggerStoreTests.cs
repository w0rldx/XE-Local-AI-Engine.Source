namespace XE_Local_AI_Engine.Client.Persistence.Tests.Integrations;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>Every method on the trigger store, against a real SQLite file.</summary>
[Category(TestCategories.Integration)]
public sealed class IntegrationTriggerStoreTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task CreateAsync_RoundTripsEveryFieldAndIsReadableByIdAndByName()
    {
        using var fixture = new IntegrationTestFixture();
        await using var context = await fixture.CreateSchemaAsync();
        var store = new IntegrationTriggerStore(context, new FixedTimeProvider(FixedNow));

        var agentId = Guid.NewGuid();
        var created = await store.CreateAsync(new IntegrationTriggerCreateCommand
        {
            TriggerId = Guid.NewGuid(),
            Name = "sensor-ingest",
            DisplayName = "Sensor ingest",
            Description = "Accepts a reading.",
            Enabled = true,
            TargetKind = IntegrationTargetKind.Agent,
            TargetAgentDefinitionId = agentId,
            SessionPolicy = IntegrationSessionPolicy.CallerManaged,
            AcceptedInputKinds = IntegrationInputKinds.Text | IntegrationInputKinds.Json
        });

        AssertEx.Equal(expected: 1L, created.Version);
        AssertEx.Equal(FixedNow.ToUnixTimeMilliseconds(), created.CreatedAtUtc);

        await using var readContext = fixture.CreateContext();
        var readStore = new IntegrationTriggerStore(readContext, new FixedTimeProvider(FixedNow));

        var byId = AssertEx.NotNull(await readStore.GetByIdAsync(created.Id));
        var byName = AssertEx.NotNull(await readStore.GetByNameAsync("sensor-ingest"));
        AssertEx.Equal(byId, byName, "The name is the external contract, so both lookups must resolve the same row.");
        AssertEx.Equal(IntegrationSessionPolicy.CallerManaged, byId.SessionPolicy);
        AssertEx.Equal(IntegrationInputKinds.Text | IntegrationInputKinds.Json, byId.AcceptedInputKinds);
        AssertEx.Equal(agentId, byId.TargetAgentDefinitionId);

        AssertEx.Null(await readStore.GetByNameAsync("no-such-trigger"));
    }

    [Test]
    public async Task UpdateAsync_AppliesOnAMatchingVersionAndAnswersFalseOtherwise()
    {
        using var fixture = new IntegrationTestFixture();
        await using var context = await fixture.CreateSchemaAsync();
        var store = new IntegrationTriggerStore(context, new FixedTimeProvider(FixedNow));
        var created = await CreateAsync(store, "sensor-ingest");

        var update = new IntegrationTriggerUpdateCommand
        {
            TriggerId = created.Id,
            ExpectedVersion = created.Version,
            DisplayName = "Renamed label",
            Description = null,
            Enabled = false,
            TargetAgentDefinitionId = created.TargetAgentDefinitionId,
            SessionPolicy = IntegrationSessionPolicy.CallerManaged,
            AcceptedInputKinds = IntegrationInputKinds.Text
        };

        AssertEx.True(await store.UpdateAsync(update));

        // False rather than an exception: the caller maps it to 409, and a store that threw would make every admin PUT
        // a try/catch.
        AssertEx.False(await store.UpdateAsync(update), "Replaying a spent version must lose.");
        AssertEx.False(await store.UpdateAsync(update with
        {
            TriggerId = Guid.NewGuid()
        }));

        var read = AssertEx.NotNull(await store.GetByIdAsync(created.Id));
        AssertEx.Equal("Renamed label", read.DisplayName);
        AssertEx.False(read.Enabled);
        AssertEx.Equal(IntegrationInputKinds.Text, read.AcceptedInputKinds);
        AssertEx.Equal(created.Version + 1, read.Version);
        AssertEx.Equal("sensor-ingest", read.Name, "The external name is not an editable field.");
    }

    [Test]
    public async Task ListAsync_OrdersByNameAndDeleteAsyncAnswersFalseForAMissingRow()
    {
        using var fixture = new IntegrationTestFixture();
        await using var context = await fixture.CreateSchemaAsync();
        var store = new IntegrationTriggerStore(context, new FixedTimeProvider(FixedNow));

        _ = await CreateAsync(store, "zulu-ingest");
        var alpha = await CreateAsync(store, "alpha-ingest");

        var listed = await store.ListAsync();
        AssertEx.Equal(expected: 2, listed.Count);
        AssertEx.Equal("alpha-ingest", listed[0].Name);

        AssertEx.True(await store.DeleteAsync(alpha.Id));
        AssertEx.False(await store.DeleteAsync(alpha.Id));
        AssertEx.Equal(expected: 1, (await store.ListAsync()).Count);
    }

    private static Task<IntegrationTriggerSnapshot> CreateAsync(IIntegrationTriggerStore store, string name) =>
        store.CreateAsync(new IntegrationTriggerCreateCommand
        {
            TriggerId = Guid.NewGuid(),
            Name = name,
            DisplayName = name,
            Description = null,
            Enabled = true,
            TargetKind = IntegrationTargetKind.Agent,
            TargetAgentDefinitionId = Guid.NewGuid(),
            SessionPolicy = IntegrationSessionPolicy.PerInvocation,
            AcceptedInputKinds = IntegrationInputKinds.Text
        });

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedTimeProvider(DateTimeOffset now)
        {
            _now = now;
        }

        public override DateTimeOffset GetUtcNow() =>
            _now;
    }
}
