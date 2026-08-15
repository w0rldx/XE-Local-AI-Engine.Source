namespace XE_Local_AI_Engine.Tests.Agents;

using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Integration coverage for <see cref="IPlaybookActionStore.PromoteSuggestedIfCurrentAsync" /> against the real
///     node SQLite store: the compare-and-swap that closes the promote-time TOCTOU. The write must land only on the exact
///     validated snapshot (Version + Suggested state), and the enabled-action cap is re-checked inside the same
///     transaction as the write so two promotes cannot both slip past a stale below-cap count.
/// </summary>
public sealed class PlaybookActionStorePromotionConcurrencyTests
{
    [ClassDataSource<TestServerWebAppFactory>(Shared = SharedType.PerClass)]
    public required TestServerWebAppFactory Factory { get; init; }

    [Test]
    public async Task PromoteSuggestedIfCurrentAsync_WhenConcurrentEditBumpsVersion_RejectsAndLeavesSuggested()
    {
        var factory = Factory;
        var agentId = await SeedAgentAsync(factory).ConfigureAwait(false);
        var actionId = await SeedSuggestionAsync(factory, agentId).ConfigureAwait(false);

        // The caller validated the snapshot at this Version.
        var snapshot = await GetAsync(factory, actionId).ConfigureAwait(false);
        var validatedVersion = snapshot.Version;

        // A concurrent edit lands between the caller's validation and its promote write: UpdateSuggestedAsync bumps
        // Version (and clears the eval), so the validated evidence no longer describes the current content.
        using (var editScope = factory.Services.CreateScope())
        {
            var service = editScope.ServiceProvider.GetRequiredService<IPlaybookActionService>();
            _ = await service.UpdateSuggestedAsync(new SuggestedActionEditInput(agentId, actionId, "An edited behavior after validation.", TriggerCondition: null, Scope: null, Priority: 100))
                             .ConfigureAwait(false);
        }

        PlaybookPromotionCommit commit;
        using (var promoteScope = factory.Services.CreateScope())
        {
            var store = promoteScope.ServiceProvider.GetRequiredService<IPlaybookActionStore>();
            commit = await store.PromoteSuggestedIfCurrentAsync(actionId, validatedVersion, maxEnabledActions: 10, evalResult: "{}").ConfigureAwait(false);
        }

        AssertEx.Equal(PlaybookPromotionCommitStatus.VersionConflict, commit.Status);
        AssertEx.Null(commit.Record, "A version-conflicted CAS writes nothing.");

        var after = await GetAsync(factory, actionId).ConfigureAwait(false);
        AssertEx.Equal(PlaybookActionState.Suggested, after.State);
    }

    [Test]
    public async Task PromoteSuggestedIfCurrentAsync_WhenCurrentAndUnderCap_Enables()
    {
        var factory = Factory;
        var agentId = await SeedAgentAsync(factory).ConfigureAwait(false);
        var actionId = await SeedSuggestionAsync(factory, agentId).ConfigureAwait(false);
        var snapshot = await GetAsync(factory, actionId).ConfigureAwait(false);

        PlaybookPromotionCommit commit;
        using (var scope = factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IPlaybookActionStore>();
            commit = await store.PromoteSuggestedIfCurrentAsync(actionId, snapshot.Version, maxEnabledActions: 10, evalResult: "{\"eval\":true}").ConfigureAwait(false);
        }

        AssertEx.Equal(PlaybookPromotionCommitStatus.Committed, commit.Status);
        var record = AssertEx.NotNull(commit.Record);
        AssertEx.Equal(PlaybookActionState.Enabled, record.State);
        AssertEx.Equal(snapshot.Version + 1, record.Version);
        AssertEx.Equal("{\"eval\":true}", record.EvalResult);
        AssertEx.True(record.EnabledAtUtc is not null, "an enabled action gets its cohort clock stamped");
    }

    [Test]
    public async Task PromoteSuggestedIfCurrentAsync_TwoPromotesAtCapOne_ExactlyOneEnabled()
    {
        var factory = Factory;
        var agentId = await SeedAgentAsync(factory).ConfigureAwait(false);
        var firstId = await SeedSuggestionAsync(factory, agentId).ConfigureAwait(false);
        var secondId = await SeedSuggestionAsync(factory, agentId).ConfigureAwait(false);
        var first = await GetAsync(factory, firstId).ConfigureAwait(false);
        var second = await GetAsync(factory, secondId).ConfigureAwait(false);

        PlaybookPromotionCommit firstCommit;
        using (var scope = factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IPlaybookActionStore>();
            firstCommit = await store.PromoteSuggestedIfCurrentAsync(firstId, first.Version, maxEnabledActions: 1, evalResult: "{}").ConfigureAwait(false);
        }

        // The second promote's cap re-check runs against the count the first promote committed, so it sees the agent
        // already at the cap and is refused — the cross-row cap invariant holds even though each row's own CAS passed.
        PlaybookPromotionCommit secondCommit;
        using (var scope = factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IPlaybookActionStore>();
            secondCommit = await store.PromoteSuggestedIfCurrentAsync(secondId, second.Version, maxEnabledActions: 1, evalResult: "{}").ConfigureAwait(false);
        }

        AssertEx.Equal(PlaybookPromotionCommitStatus.Committed, firstCommit.Status);
        AssertEx.Equal(PlaybookPromotionCommitStatus.CapReached, secondCommit.Status);

        using var verifyScope = factory.Services.CreateScope();
        var verifyStore = verifyScope.ServiceProvider.GetRequiredService<IPlaybookActionStore>();
        var enabled = await verifyStore.ListEnabledByAgentAsync(agentId).ConfigureAwait(false);
        AssertEx.Equal(expected: 1, enabled.Count);
    }

    private static async Task<Guid> SeedAgentAsync(TestServerWebAppFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IAgentDefinitionStore>();
        var agent = await store.AddAsync(new AgentDefinitionInput("Owner",
            Description: null,
            "You are a careful engineering agent.",
            ModelProfile: null,
            ReasoningEffort: null,
            AgentDefinitionKind.Single,
            [],
            new Dictionary<string, bool>(),
            OrchestrationTopologyJson: null)).ConfigureAwait(false);
        return agent.Id;
    }

    private static async Task<Guid> SeedSuggestionAsync(TestServerWebAppFactory factory, Guid agentDefinitionId)
    {
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IPlaybookActionService>();
        var created = await service.CreateAnalysisSuggestionAsync(new PlaybookAnalysisSuggestionInput(agentDefinitionId,
            "Cite sources before answering.",
            TriggerCondition: null,
            "search",
            Priority: 100,
            [Guid.NewGuid()],
            Confidence: 0.8d)).ConfigureAwait(false);
        return created.Id;
    }

    private static async Task<PlaybookActionRecord> GetAsync(TestServerWebAppFactory factory, Guid actionId)
    {
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IPlaybookActionStore>();
        return AssertEx.NotNull(await store.GetByIdAsync(actionId).ConfigureAwait(false));
    }
}
