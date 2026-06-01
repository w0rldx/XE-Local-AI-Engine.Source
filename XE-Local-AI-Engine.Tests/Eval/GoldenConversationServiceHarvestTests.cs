namespace XE_Local_AI_Engine.Tests.Eval;

using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Eval;
using XE_Local_AI_Engine.Client.Services.Eval.Implementation;
using XE_Local_AI_Engine.HostAgent.Abstractions.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Harvest follow-up unit tests for <see cref="GoldenConversationService" />: the harvested create path pins
///     Source=Harvested + Enabled=false and requires provenance, while approve flips a harvested, owned, disabled case
///     enabled (and rejects every other shape). The manual create path's caps/ownership/signal rules are reused
///     verbatim (covered by <see cref="GoldenConversationServiceTests" />); these tests assert the harvest-specific
///     guards.
/// </summary>
public sealed class GoldenConversationServiceHarvestTests
{
    private static readonly Guid AgentId = Guid.NewGuid();

    [Test]
    public async Task CreateHarvestedAsync_PinsHarvestedSourceAndStagesInert_EvenWhenInputEnabled()
    {
        var service = CreateService(out var store);
        var input = new GoldenConversationCreateInput(
            AgentId,
            Title: "Harvested case",
            InputTurns: """[{"role":"user","text":"hi"}]""",
            Assertion: null,
            Rubric: "Be consistent with the approved answer.",
            Enabled: true,
            Source: GoldenConversationSource.Manual,
            SourceMessageId: Guid.NewGuid(),
            SourceConversationId: Guid.NewGuid());

        _ = await service.CreateHarvestedAsync(input).ConfigureAwait(false);

        await store.Received(1).AddAsync(
            Arg.Is<GoldenConversationInput>(stored =>
                stored.Source == GoldenConversationSource.Harvested
                && !stored.Enabled
                && stored.SourceMessageId == input.SourceMessageId
                && stored.SourceConversationId == input.SourceConversationId),
            Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task CreateHarvestedAsync_WhenProvenanceMissing_RejectsWithValidationError()
    {
        var service = CreateService(out var store);
        var input = new GoldenConversationCreateInput(
            AgentId,
            Title: "Harvested case",
            InputTurns: """[{"role":"user","text":"hi"}]""",
            Assertion: null,
            Rubric: "Be consistent.",
            Enabled: false,
            Source: GoldenConversationSource.Harvested,
            SourceMessageId: null,
            SourceConversationId: Guid.NewGuid());

        await AssertEx.ThrowsAsync<PlaybookActionValidationException>(
            async () => await service.CreateHarvestedAsync(input).ConfigureAwait(false)).ConfigureAwait(false);

        await store.DidNotReceive().AddAsync(Arg.Any<GoldenConversationInput>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task CreateHarvestedAsync_WhenTitleExceedsCap_RejectsWithValidationError()
    {
        var service = CreateService(out _);
        var input = new GoldenConversationCreateInput(
            AgentId,
            Title: new string('t', 201),
            InputTurns: """[{"role":"user","text":"hi"}]""",
            Assertion: null,
            Rubric: "Be consistent.",
            Enabled: false,
            Source: GoldenConversationSource.Harvested,
            SourceMessageId: Guid.NewGuid(),
            SourceConversationId: Guid.NewGuid());

        // The harvested path reuses the same boundary validation (caps + ≥1 signal + owning agent) as CreateAsync.
        await AssertEx.ThrowsAsync<PlaybookActionValidationException>(
            async () => await service.CreateHarvestedAsync(input).ConfigureAwait(false)).ConfigureAwait(false);
    }

    [Test]
    public async Task CreateHarvestedAsync_WhenAgentDoesNotExist_RejectsWithValidationError()
    {
        var store = Substitute.For<IGoldenConversationStore>();
        var agentStore = Substitute.For<IAgentDefinitionStore>();
        agentStore.GetByIdAsync(AgentId, Arg.Any<CancellationToken>())
                  .Returns(Task.FromResult<AgentDefinitionRecord?>(null));
        var service = new GoldenConversationService(store, agentStore);

        var input = new GoldenConversationCreateInput(
            AgentId,
            Title: "Harvested case",
            InputTurns: """[{"role":"user","text":"hi"}]""",
            Assertion: null,
            Rubric: "Be consistent.",
            Enabled: false,
            Source: GoldenConversationSource.Harvested,
            SourceMessageId: Guid.NewGuid(),
            SourceConversationId: Guid.NewGuid());

        await AssertEx.ThrowsAsync<PlaybookActionValidationException>(
            async () => await service.CreateHarvestedAsync(input).ConfigureAwait(false)).ConfigureAwait(false);
    }

    [Test]
    public async Task ApproveHarvestedAsync_WhenHarvestedDisabledAndOwned_EnablesAndReturnsRecord()
    {
        var goldenId = Guid.NewGuid();
        var service = CreateApproveService(out var store, Existing(goldenId, AgentId, GoldenConversationSource.Harvested, enabled: false));
        store.SetEnabledAsync(goldenId, true, Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<GoldenConversationRecord?>(Existing(goldenId, AgentId, GoldenConversationSource.Harvested, enabled: true)));

        var result = AssertEx.NotNull(await service.ApproveHarvestedAsync(AgentId, goldenId).ConfigureAwait(false), "Approve should return the updated record.");

        AssertEx.True(result.Enabled, "Approve should enable the case.");
        await store.Received(1).SetEnabledAsync(goldenId, true, Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task ApproveHarvestedAsync_WhenCrossAgent_ReturnsNullWithoutEnabling()
    {
        var goldenId = Guid.NewGuid();
        var otherAgentId = Guid.NewGuid();
        var service = CreateApproveService(out var store, Existing(goldenId, otherAgentId, GoldenConversationSource.Harvested, enabled: false));

        AssertEx.Null(await service.ApproveHarvestedAsync(AgentId, goldenId).ConfigureAwait(false), "A case owned by another agent must not be approved.");
        await store.DidNotReceive().SetEnabledAsync(Arg.Any<Guid>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task ApproveHarvestedAsync_WhenManualSource_ReturnsNullWithoutEnabling()
    {
        var goldenId = Guid.NewGuid();
        var service = CreateApproveService(out var store, Existing(goldenId, AgentId, GoldenConversationSource.Manual, enabled: false));

        AssertEx.Null(await service.ApproveHarvestedAsync(AgentId, goldenId).ConfigureAwait(false), "A manual case must not be approved via the harvest path.");
        await store.DidNotReceive().SetEnabledAsync(Arg.Any<Guid>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task ApproveHarvestedAsync_WhenAlreadyEnabled_ReturnsNullWithoutEnabling()
    {
        var goldenId = Guid.NewGuid();
        var service = CreateApproveService(out var store, Existing(goldenId, AgentId, GoldenConversationSource.Harvested, enabled: true));

        AssertEx.Null(await service.ApproveHarvestedAsync(AgentId, goldenId).ConfigureAwait(false), "An already-enabled case must not be re-approved.");
        await store.DidNotReceive().SetEnabledAsync(Arg.Any<Guid>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    private static GoldenConversationService CreateService(out IGoldenConversationStore store)
    {
        store = Substitute.For<IGoldenConversationStore>();
        store.AddAsync(Arg.Any<GoldenConversationInput>(), Arg.Any<CancellationToken>())
             .Returns(callInfo => Task.FromResult(StoredRecord(callInfo.Arg<GoldenConversationInput>())));

        var agentStore = Substitute.For<IAgentDefinitionStore>();
        agentStore.GetByIdAsync(AgentId, Arg.Any<CancellationToken>())
                  .Returns(Task.FromResult<AgentDefinitionRecord?>(CreateAgent()));

        return new GoldenConversationService(store, agentStore);
    }

    private static GoldenConversationService CreateApproveService(out IGoldenConversationStore store, GoldenConversationRecord existing)
    {
        store = Substitute.For<IGoldenConversationStore>();
        store.GetByIdAsync(existing.Id, Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<GoldenConversationRecord?>(existing));

        var agentStore = Substitute.For<IAgentDefinitionStore>();
        agentStore.GetByIdAsync(AgentId, Arg.Any<CancellationToken>())
                  .Returns(Task.FromResult<AgentDefinitionRecord?>(CreateAgent()));

        return new GoldenConversationService(store, agentStore);
    }

    private static GoldenConversationRecord Existing(Guid id, Guid agentId, GoldenConversationSource source, bool enabled)
    {
        return new GoldenConversationRecord(
            id,
            agentId,
            Title: "case",
            InputTurns: """[{"role":"user","text":"hi"}]""",
            Assertion: null,
            Rubric: "Be consistent.",
            enabled,
            CreatedAtUtc: 10,
            UpdatedAtUtc: 10,
            source,
            SourceMessageId: source == GoldenConversationSource.Harvested ? Guid.NewGuid() : null,
            SourceConversationId: source == GoldenConversationSource.Harvested ? Guid.NewGuid() : null);
    }

    private static GoldenConversationRecord StoredRecord(GoldenConversationInput input)
    {
        return new GoldenConversationRecord(
            Guid.NewGuid(),
            input.AgentDefinitionId,
            input.Title,
            input.InputTurns,
            input.Assertion,
            input.Rubric,
            input.Enabled,
            CreatedAtUtc: 10,
            UpdatedAtUtc: 10,
            input.Source,
            input.SourceMessageId,
            input.SourceConversationId);
    }

    private static AgentDefinitionRecord CreateAgent()
    {
        return new AgentDefinitionRecord(AgentId,
            "Builder",
            Description: null,
            "Base instructions.",
            ModelProfile: null,
            ReasoningEffort: null,
            AgentDefinitionKind.Single,
            AllowedToolNames: [],
            ToolApprovals: new Dictionary<string, bool>(),
            OrchestrationTopologyJson: null,
            Version: 1,
            CreatedAtUtc: 10,
            UpdatedAtUtc: 10);
    }
}
