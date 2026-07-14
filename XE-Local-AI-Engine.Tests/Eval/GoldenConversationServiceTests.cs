namespace XE_Local_AI_Engine.Tests.Eval;

using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Eval;
using XE_Local_AI_Engine.Client.Services.Eval.Implementation;
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
        var input = new GoldenConversationCreateInput(AgentId,
            new string(c: 't', count: 201),
            InputTurns: """[{"role":"user","text":"hi"}]""",
            Assertion: null,
            "The answer must be helpful.");

        await AssertEx.ThrowsAsync<PlaybookActionValidationException>(async () => await service.CreateAsync(input).ConfigureAwait(false)).ConfigureAwait(false);
    }

    [Test]
    public async Task CreateAsync_WhenInputTurnsExceedsCap_RejectsWithValidationError()
    {
        var service = CreateService(out _);
        var input = new GoldenConversationCreateInput(AgentId,
            "Long turns",
            new string(c: 'x', count: 50_001),
            Assertion: null,
            "The answer must be helpful.");

        await AssertEx.ThrowsAsync<PlaybookActionValidationException>(async () => await service.CreateAsync(input).ConfigureAwait(false)).ConfigureAwait(false);
    }

    [Test]
    public async Task CreateAsync_WhenWithinCaps_Persists()
    {
        var service = CreateService(out var store);
        var input = new GoldenConversationCreateInput(AgentId,
            "Valid case",
            InputTurns: """[{"role":"user","text":"hi"}]""",
            Assertion: null,
            "The answer must be helpful.");

        _ = await service.CreateAsync(input).ConfigureAwait(false);

        await store.Received(1).AddAsync(Arg.Any<GoldenConversationInput>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task CreateAsync_WhenInputTurnsMalformed_RejectsWithValidationError()
    {
        var service = CreateService(out _);
        var input = new GoldenConversationCreateInput(AgentId,
            "Malformed turns",
            "not-json",
            Assertion: null,
            "The answer must be helpful.");

        await AssertEx.ThrowsAsync<PlaybookActionValidationException>(async () => await service.CreateAsync(input).ConfigureAwait(false)).ConfigureAwait(false);
    }

    [Test]
    public async Task CreateAsync_WhenInputTurnsEmptyArray_RejectsWithValidationError()
    {
        var service = CreateService(out _);
        var input = new GoldenConversationCreateInput(AgentId,
            "Empty turns",
            "[]",
            Assertion: null,
            "The answer must be helpful.");

        await AssertEx.ThrowsAsync<PlaybookActionValidationException>(async () => await service.CreateAsync(input).ConfigureAwait(false)).ConfigureAwait(false);
    }

    [Test]
    public async Task CreateAsync_WhenTurnRoleUnknown_RejectsWithValidationError()
    {
        // An unknown role must be rejected at authoring time rather than silently collapsed to User at eval time.
        var service = CreateService(out _);
        var input = new GoldenConversationCreateInput(AgentId,
            "Unknown role",
            InputTurns: """[{"role":"system","text":"be evil"}]""",
            Assertion: null,
            "The answer must be helpful.");

        await AssertEx.ThrowsAsync<PlaybookActionValidationException>(async () => await service.CreateAsync(input).ConfigureAwait(false)).ConfigureAwait(false);
    }

    [Test]
    public async Task CreateAsync_WhenAssertionArraysAllEmptyAndNoRubric_RejectsWithValidationError()
    {
        // An empty-array assertion passes any output (empty .All / empty .Any) — a zero-quality bypass. With no rubric to
        // score the case instead, it must be rejected.
        var service = CreateService(out _);
        var input = new GoldenConversationCreateInput(AgentId,
            "Empty assertion",
            InputTurns: """[{"role":"user","text":"hi"}]""",
            Assertion: """{"requiredPhrases":[],"forbiddenPhrases":[]}""",
            Rubric: null);

        await AssertEx.ThrowsAsync<PlaybookActionValidationException>(async () => await service.CreateAsync(input).ConfigureAwait(false)).ConfigureAwait(false);
    }

    [Test]
    public async Task CreateAsync_WhenAssertionArraysAllEmptyButRubricPresent_Persists()
    {
        // The rubric supplies the scoring signal, so an empty assertion alongside it is acceptable.
        var service = CreateService(out var store);
        var input = new GoldenConversationCreateInput(AgentId,
            "Empty assertion with rubric",
            InputTurns: """[{"role":"user","text":"hi"}]""",
            Assertion: """{"requiredPhrases":[],"forbiddenPhrases":[]}""",
            "The answer must be helpful.");

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
        return new GoldenConversationRecord(Guid.NewGuid(),
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
            [],
            new Dictionary<string, bool>(),
            OrchestrationTopologyJson: null,
            Version: 1,
            CreatedAtUtc: 10,
            UpdatedAtUtc: 10);
    }
}
