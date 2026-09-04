namespace XE_Local_AI_Engine.Client.Persistence.Tests.Integrations;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>Every method on the trigger store, against a real SQLite file.</summary>
public sealed class IntegrationTriggerStoreTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task CreateAsync_RoundTripsEveryFieldAndIsReadableByIdAndByName()
    {
        using var fixture = new IntegrationTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = new IntegrationTriggerStore(context, new FixedTimeProvider(FixedNow));

        var agentId = Guid.NewGuid();
        var created = await store.CreateAsync(new IntegrationTriggerCreateCommand(Guid.NewGuid(),
                                     "sensor-ingest",
                                     "Sensor ingest",
                                     "Accepts a reading.",
                                     Enabled: true,
                                     IntegrationTargetKind.Agent,
                                     agentId,
                                     IntegrationSessionPolicy.CallerManaged,
                                     IntegrationInputKinds.Text | IntegrationInputKinds.Json))
                                 .ConfigureAwait(false);

        AssertEx.Equal(expected: 1L, created.Version);
        AssertEx.Equal(FixedNow.ToUnixTimeMilliseconds(), created.CreatedAtUtc);

        await using var readContext = fixture.CreateContext();
        var readStore = new IntegrationTriggerStore(readContext, new FixedTimeProvider(FixedNow));

        var byId = AssertEx.NotNull(await readStore.GetByIdAsync(created.Id).ConfigureAwait(false));
        var byName = AssertEx.NotNull(await readStore.GetByNameAsync("sensor-ingest").ConfigureAwait(false));
        AssertEx.Equal(byId, byName, "The name is the external contract, so both lookups must resolve the same row.");
        AssertEx.Equal(IntegrationSessionPolicy.CallerManaged, byId.SessionPolicy);
        AssertEx.Equal(IntegrationInputKinds.Text | IntegrationInputKinds.Json, byId.AcceptedInputKinds);
        AssertEx.Equal(agentId, byId.TargetAgentDefinitionId);

        AssertEx.Null(await readStore.GetByNameAsync("no-such-trigger").ConfigureAwait(false));
    }

    [Test]
    public async Task UpdateAsync_AppliesOnAMatchingVersionAndAnswersFalseOtherwise()
    {
        using var fixture = new IntegrationTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = new IntegrationTriggerStore(context, new FixedTimeProvider(FixedNow));
        var created = await CreateAsync(store, "sensor-ingest").ConfigureAwait(false);

        var update = new IntegrationTriggerUpdateCommand(created.Id,
            created.Version,
            "Renamed label",
            Description: null,
            Enabled: false,
            created.TargetAgentDefinitionId,
            IntegrationSessionPolicy.CallerManaged,
            IntegrationInputKinds.Text);

        AssertEx.True(await store.UpdateAsync(update).ConfigureAwait(false));

        // False rather than an exception: the caller maps it to 409, and a store that threw would make every admin PUT
        // a try/catch.
        AssertEx.False(await store.UpdateAsync(update).ConfigureAwait(false), "Replaying a spent version must lose.");
        AssertEx.False(await store.UpdateAsync(update with { TriggerId = Guid.NewGuid() }).ConfigureAwait(false));

        var read = AssertEx.NotNull(await store.GetByIdAsync(created.Id).ConfigureAwait(false));
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
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = new IntegrationTriggerStore(context, new FixedTimeProvider(FixedNow));

        _ = await CreateAsync(store, "zulu-ingest").ConfigureAwait(false);
        var alpha = await CreateAsync(store, "alpha-ingest").ConfigureAwait(false);

        var listed = await store.ListAsync().ConfigureAwait(false);
        AssertEx.Equal(expected: 2, listed.Count);
        AssertEx.Equal("alpha-ingest", listed[0].Name);

        AssertEx.True(await store.DeleteAsync(alpha.Id).ConfigureAwait(false));
        AssertEx.False(await store.DeleteAsync(alpha.Id).ConfigureAwait(false));
        AssertEx.Equal(expected: 1, (await store.ListAsync().ConfigureAwait(false)).Count);
    }

    private static Task<IntegrationTriggerSnapshot> CreateAsync(IIntegrationTriggerStore store, string name) =>
        store.CreateAsync(new IntegrationTriggerCreateCommand(Guid.NewGuid(),
            name,
            name,
            Description: null,
            Enabled: true,
            IntegrationTargetKind.Agent,
            Guid.NewGuid(),
            IntegrationSessionPolicy.PerInvocation,
            IntegrationInputKinds.Text));

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
