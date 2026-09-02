namespace XE_Local_AI_Engine.Client.Persistence.Tests.DevWorkflows;

using System.Security.Cryptography;
using System.Text;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

public sealed class DevWorkflowRuleSetStoreTests
{
    private const string ProjectScope = """{"projectIds":["9c1a5c9e-0000-4000-8000-000000000001"],"nodeTypes":["Agent"]}""";

    [Test]
    public async Task CreateRuleSet_RoundTripsEveryFieldAndHashesTheBodyAlongsideIt()
    {
        using var fixture = new DevWorkflowTestFixture();
        Guid ruleSetId;

        await using (var context = await fixture.CreateSchemaAsync().ConfigureAwait(false))
        {
            var store = DevWorkflowTestFixture.StoreFor(context);
            var created = await store.CreateRuleSetAsync(new CreateDevWorkflowRuleSetCommand(Guid.NewGuid(),
                                         "House rules",
                                         "Always write the test first.",
                                         ProjectScope,
                                         "What every agent node on this project must follow."))
                                     .ConfigureAwait(false);
            ruleSetId = created.Id;

            AssertEx.Equal(expected: 1, created.Version);
            AssertEx.True(created.Enabled, "A rule set is created enabled unless the caller says otherwise.");
            AssertEx.Equal(Convert.ToHexStringLower(SHA256.HashData("Always write the test first."u8.ToArray())),
                created.ContentSha256,
                "The hash is lowercase hex of SHA-256 over the exact body bytes, computed store-side.");
        }

        // Read back through a FRESH context, so the answer comes off the file through the decrypt path rather than out
        // of the change tracker's plaintext.
        await using (var readContext = fixture.CreateContext())
        {
            var store = DevWorkflowTestFixture.StoreFor(readContext);
            var read = await store.GetRuleSetAsync(ruleSetId).ConfigureAwait(false);

            AssertEx.Equal("House rules", read.Name);
            AssertEx.Equal("What every agent node on this project must follow.", read.Description);
            AssertEx.Equal(ProjectScope, read.ScopeJson);
            AssertEx.Equal("Always write the test first.", read.Body);
            AssertEx.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(read.Body))),
                read.ContentSha256,
                "The stored hash must still describe the stored body after a round trip.");
        }
    }

    [Test]
    public async Task UpdateRuleSet_RewritesTheHashWithTheBodyAndBumpsTheVersion()
    {
        using var fixture = new DevWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = DevWorkflowTestFixture.StoreFor(context);
        var created = await DevWorkflowTestFixture.CreateRuleSetAsync(store).ConfigureAwait(false);

        var updated = await store.UpdateRuleSetAsync(new UpdateDevWorkflowRuleSetCommand(created.Id,
                                     created.Version,
                                     "House rules v2",
                                     "Never touch production.",
                                     ProjectScope,
                                     Description: null,
                                     Enabled: false))
                                 .ConfigureAwait(false);

        AssertEx.Equal(expected: 2, updated.Version);
        AssertEx.Equal("House rules v2", updated.Name);
        AssertEx.Null(updated.Description, "A PUT is a whole replacement, so an omitted description clears the one that was there.");
        AssertEx.False(updated.Enabled);
        AssertEx.Equal(Convert.ToHexStringLower(SHA256.HashData("Never touch production."u8.ToArray())),
            updated.ContentSha256,
            "The hash is rewritten in the same save as the body it describes.");
        AssertEx.False(string.Equals(created.ContentSha256, updated.ContentSha256, StringComparison.Ordinal), "A new body must not keep the old body's hash.");
    }

    [Test]
    public async Task UpdateRuleSet_WithAStaleVersion_IsRefusedAndLeavesTheStoredDocumentAlone()
    {
        using var fixture = new DevWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = DevWorkflowTestFixture.StoreFor(context);
        var created = await DevWorkflowTestFixture.CreateRuleSetAsync(store, body: "Original text.").ConfigureAwait(false);
        _ = await store.UpdateRuleSetAsync(new UpdateDevWorkflowRuleSetCommand(created.Id, created.Version, "Renamed", "Second text.", DevWorkflowTestFixture.MatchAllScope))
                       .ConfigureAwait(false);

        _ = await AssertEx.ThrowsAsync<DevWorkflowConcurrencyException>(() => store.UpdateRuleSetAsync(new UpdateDevWorkflowRuleSetCommand(created.Id,
                                  created.Version,
                                  "Loser",
                                  "Third text.",
                                  DevWorkflowTestFixture.MatchAllScope)),
                              "A second edit made against version 1 must be refused rather than silently overwrite the one that landed.")
                          .ConfigureAwait(false);

        var read = await store.GetRuleSetAsync(created.Id).ConfigureAwait(false);
        AssertEx.Equal("Second text.", read.Body, "The refused edit must not have reached the row.");
    }

    [Test]
    public async Task GetRuleSet_WhenItWasDeleted_ReportsItMissing()
    {
        using var fixture = new DevWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = DevWorkflowTestFixture.StoreFor(context);
        var created = await DevWorkflowTestFixture.CreateRuleSetAsync(store).ConfigureAwait(false);

        await store.DeleteRuleSetAsync(created.Id).ConfigureAwait(false);

        AssertEx.Equal(expected: 0L, await fixture.RawTableCountAsync("dev_workflow_rule_sets").ConfigureAwait(false), "DELETE is a hard delete, not an archive flag.");
        _ = await AssertEx.ThrowsAsync<DevWorkflowNotFoundException>(() => store.GetRuleSetAsync(created.Id)).ConfigureAwait(false);
        _ = await AssertEx.ThrowsAsync<DevWorkflowNotFoundException>(() => store.DeleteRuleSetAsync(created.Id),
                              "Deleting the same rule set twice must answer 404 rather than pretend it removed one.")
                          .ConfigureAwait(false);
    }

    [Test]
    public async Task ListRuleSets_IsOrderedByNameAndListEnabledDropsTheDisabledOnes()
    {
        using var fixture = new DevWorkflowTestFixture();
        await using var context = await fixture.CreateSchemaAsync().ConfigureAwait(false);
        var store = DevWorkflowTestFixture.StoreFor(context);
        _ = await DevWorkflowTestFixture.CreateRuleSetAsync(store, "Zulu").ConfigureAwait(false);
        _ = await DevWorkflowTestFixture.CreateRuleSetAsync(store, "Alpha").ConfigureAwait(false);
        _ = await DevWorkflowTestFixture.CreateRuleSetAsync(store, "Mike", enabled: false).ConfigureAwait(false);

        var all = await store.ListRuleSetsAsync().ConfigureAwait(false);
        var enabled = await store.ListEnabledRuleSetsAsync().ConfigureAwait(false);

        AssertEx.Equal("Alpha,Mike,Zulu", string.Join(',', all.Select(item => item.Name)), "The list is ordered by name — the order matches are injected in.");
        AssertEx.Equal("Alpha,Zulu", string.Join(',', enabled.Select(item => item.Name)), "A disabled rule set is not part of the resolver's working set.");
        AssertEx.True(all.All(item => item.ContentSha256.Length == 64), "The list still carries the content hash — it is the cheap half, and the body is the expensive one.");
    }
}
