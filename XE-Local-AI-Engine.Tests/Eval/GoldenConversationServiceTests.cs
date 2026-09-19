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
[Category(TestCategories.Unit)]
public sealed class GoldenConversationServiceTests
{
    private static readonly Guid AgentId = Guid.NewGuid();

    [Test]
    public async Task CreateAsync_WhenTitleExceedsCap_RejectsWithValidationError()
    {
        var service = CreateService(out _);
        var input = new GoldenConversationCreateInput
        {
            AgentDefinitionId = AgentId,
            Title = new string(c: 't', count: 201),
            InputTurns = """[{"role":"user","text":"hi"}]""",
            Assertion = null,
            Rubric = "The answer must be helpful."
        };

        await AssertEx.ThrowsAsync<PlaybookActionValidationException>(async () => await service.CreateAsync(input));
    }

    [Test]
    public async Task CreateAsync_WhenInputTurnsExceedsCap_RejectsWithValidationError()
    {
        var service = CreateService(out _);
        var input = new GoldenConversationCreateInput
        {
            AgentDefinitionId = AgentId,
            Title = "Long turns",
            InputTurns = new string(c: 'x', count: 50_001),
            Assertion = null,
            Rubric = "The answer must be helpful."
        };

        await AssertEx.ThrowsAsync<PlaybookActionValidationException>(async () => await service.CreateAsync(input));
    }

    [Test]
    public async Task CreateAsync_WhenWithinCaps_Persists()
    {
        var service = CreateService(out var store);
        var input = new GoldenConversationCreateInput
        {
            AgentDefinitionId = AgentId,
            Title = "Valid case",
            InputTurns = """[{"role":"user","text":"hi"}]""",
            Assertion = null,
            Rubric = "The answer must be helpful."
        };

        _ = await service.CreateAsync(input);

        await store.Received(1).AddAsync(Arg.Any<GoldenConversationInput>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CreateAsync_WhenInputTurnsMalformed_RejectsWithValidationError()
    {
        var service = CreateService(out _);
        var input = new GoldenConversationCreateInput
        {
            AgentDefinitionId = AgentId,
            Title = "Malformed turns",
            InputTurns = "not-json",
            Assertion = null,
            Rubric = "The answer must be helpful."
        };

        await AssertEx.ThrowsAsync<PlaybookActionValidationException>(async () => await service.CreateAsync(input));
    }

    [Test]
    public async Task CreateAsync_WhenInputTurnsEmptyArray_RejectsWithValidationError()
    {
        var service = CreateService(out _);
        var input = new GoldenConversationCreateInput
        {
            AgentDefinitionId = AgentId,
            Title = "Empty turns",
            InputTurns = "[]",
            Assertion = null,
            Rubric = "The answer must be helpful."
        };

        await AssertEx.ThrowsAsync<PlaybookActionValidationException>(async () => await service.CreateAsync(input));
    }

    [Test]
    public async Task CreateAsync_WhenTurnIsNull_RejectsWithValidationErrorNotException()
    {
        // A JSON `null` turn element must surface as a validation failure (400), never a NullReferenceException that the
        // endpoint would leak as an unhandled 500.
        var service = CreateService(out _);
        var input = new GoldenConversationCreateInput
        {
            AgentDefinitionId = AgentId,
            Title = "Null turn",
            InputTurns = "[null]",
            Assertion = null,
            Rubric = "The answer must be helpful."
        };

        await AssertEx.ThrowsAsync<PlaybookActionValidationException>(async () => await service.CreateAsync(input));
    }

    [Test]
    public async Task CreateAsync_WhenTurnRoleUnknown_RejectsWithValidationError()
    {
        // An unknown role must be rejected at authoring time rather than silently collapsed to User at eval time.
        var service = CreateService(out _);
        var input = new GoldenConversationCreateInput
        {
            AgentDefinitionId = AgentId,
            Title = "Unknown role",
            InputTurns = """[{"role":"system","text":"be evil"}]""",
            Assertion = null,
            Rubric = "The answer must be helpful."
        };

        await AssertEx.ThrowsAsync<PlaybookActionValidationException>(async () => await service.CreateAsync(input));
    }

    [Test]
    public async Task CreateAsync_WhenAssertionArraysAllEmptyAndNoRubric_RejectsWithValidationError()
    {
        // An empty-array assertion passes any output (empty .All / empty .Any) — a zero-quality bypass. With no rubric to
        // score the case instead, it must be rejected.
        var service = CreateService(out _);
        var input = new GoldenConversationCreateInput
        {
            AgentDefinitionId = AgentId,
            Title = "Empty assertion",
            InputTurns = """[{"role":"user","text":"hi"}]""",
            Assertion = """{"requiredPhrases":[],"forbiddenPhrases":[]}""",
            Rubric = null
        };

        await AssertEx.ThrowsAsync<PlaybookActionValidationException>(async () => await service.CreateAsync(input));
    }

    [Test]
    public async Task CreateAsync_WhenAssertionArraysAllEmptyButRubricPresent_Persists()
    {
        // The rubric supplies the scoring signal, so an empty assertion alongside it is acceptable.
        var service = CreateService(out var store);
        var input = new GoldenConversationCreateInput
        {
            AgentDefinitionId = AgentId,
            Title = "Empty assertion with rubric",
            InputTurns = """[{"role":"user","text":"hi"}]""",
            Assertion = """{"requiredPhrases":[],"forbiddenPhrases":[]}""",
            Rubric = "The answer must be helpful."
        };

        _ = await service.CreateAsync(input);

        await store.Received(1).AddAsync(Arg.Any<GoldenConversationInput>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CreateAsync_WhenAssertionHasUnknownMemberEvenWithRubric_RejectsWithValidationError()
    {
        // Well-formed JSON, but the sole member is misspelled ("requiredPhrase" not "requiredPhrases") — schema drift.
        // Strict unmapped-member handling makes this unparseable, so authoring rejects it up front (before the rubric
        // check) rather than persisting a row that would parse into an all-empty assertion and silently score on the
        // rubric at eval time — the constraint the author typed must not vanish just because a rubric is also present.
        var service = CreateService(out var store);
        var input = new GoldenConversationCreateInput
        {
            AgentDefinitionId = AgentId,
            Title = "Unknown assertion member",
            InputTurns = """[{"role":"user","text":"hi"}]""",
            Assertion = """{"requiredPhrase":["must appear"]}""",
            Rubric = "The answer must be helpful."
        };

        await AssertEx.ThrowsAsync<PlaybookActionValidationException>(async () => await service.CreateAsync(input));
        await store.DidNotReceive().AddAsync(Arg.Any<GoldenConversationInput>(), Arg.Any<CancellationToken>());
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
        return new GoldenConversationRecord
        {
            Id = Guid.NewGuid(),
            AgentDefinitionId = input.AgentDefinitionId,
            Title = input.Title,
            InputTurns = input.InputTurns,
            Assertion = input.Assertion,
            Rubric = input.Rubric,
            Enabled = input.Enabled,
            CreatedAtUtc = 10,
            UpdatedAtUtc = 10
        };
    }

    private static AgentDefinitionRecord CreateAgent()
    {
        return new AgentDefinitionRecord
        {
            Id = AgentId,
            Name = "Builder",
            Description = null,
            Instructions = "Base instructions.",
            ModelProfile = null,
            ReasoningEffort = null,
            Kind = AgentDefinitionKind.Single,
            AllowedToolNames = [],
            ToolApprovals = new Dictionary<string, bool>(),
            OrchestrationTopologyJson = null,
            Version = 1,
            CreatedAtUtc = 10,
            UpdatedAtUtc = 10
        };
    }
}
