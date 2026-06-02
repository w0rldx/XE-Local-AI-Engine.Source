namespace XE_Local_AI_Engine.Tests.Eval;

using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Eval;
using XE_Local_AI_Engine.Client.Services.Eval.Implementation;
using XE_Local_AI_Engine.HostAgent.Abstractions.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Golden-set create-validation unit tests. The service rejects over-long boundary fields before
///     persisting (mirroring the PlaybookAction free-text cap), so a client cannot push an unbounded encrypted payload.
/// </summary>
public sealed class GoldenConversationServiceTests
{
    private static readonly Guid AgentId = Guid.NewGuid();

    [Test]
    public async Task CreateAsync_WhenTitleExceedsCap_RejectsWithValidationError()
    {
        var service = CreateService(out _);
        var input = new GoldenConversationCreateInput(
            AgentId,
            Title: new string('t', 201),
            InputTurns: """[{"role":"user","text":"hi"}]""",
            Assertion: null,
            Rubric: "The answer must be helpful.");

        await AssertEx.ThrowsAsync<PlaybookActionValidationException>(
            async () => await service.CreateAsync(input).ConfigureAwait(false)).ConfigureAwait(false);
    }

    [Test]
    public async Task CreateAsync_WhenInputTurnsExceedsCap_RejectsWithValidationError()
    {
        var service = CreateService(out _);
        var input = new GoldenConversationCreateInput(
            AgentId,
            Title: "Long turns",
            InputTurns: new string('x', 50_001),
            Assertion: null,
            Rubric: "The answer must be helpful.");

        await AssertEx.ThrowsAsync<PlaybookActionValidationException>(
            async () => await service.CreateAsync(input).ConfigureAwait(false)).ConfigureAwait(false);
    }

    [Test]
    public async Task CreateAsync_WhenWithinCaps_Persists()
    {
        var service = CreateService(out var store);
        var input = new GoldenConversationCreateInput(
            AgentId,
            Title: "Valid case",
            InputTurns: """[{"role":"user","text":"hi"}]""",
            Assertion: null,
            Rubric: "The answer must be helpful.");

        _ = await service.CreateAsync(input).ConfigureAwait(false);

        await store.Received(1).AddAsync(Arg.Any<GoldenConversationInput>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
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
            UpdatedAtUtc: 10);
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
