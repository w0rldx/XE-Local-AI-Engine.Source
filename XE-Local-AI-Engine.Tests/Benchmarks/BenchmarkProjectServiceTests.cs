namespace XE_Local_AI_Engine.Tests.Benchmarks;

using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class BenchmarkProjectServiceTests
{
    [Test]
    public async Task Create_PreservesExactTaskPayload()
    {
        var store = Substitute.For<IBenchmarkStore>();
        BenchmarkProjectInput? captured = null;
        store.CreateProjectAsync(Arg.Do<BenchmarkProjectInput>(input => captured = input), Arg.Any<CancellationToken>())
             .Returns(call => Project(call.Arg<BenchmarkProjectInput>()));
        var agents = Substitute.For<IAgentDefinitionStore>();
        var agentId = Guid.NewGuid();
        agents.GetByIdAsync(agentId, Arg.Any<CancellationToken>()).Returns(Definition(agentId, AgentDefinitionKind.Single));
        var service = new BenchmarkProjectService(store, agents);

        _ = await service.CreateAsync(new BenchmarkProjectDraft(Guid.NewGuid(), "Benchmark", "  exact task  ", 4096, agentId, false, null, null));

        AssertEx.Equal("  exact task  ", BenchmarkProjectService.DecodeCoreTask(AssertEx.NotNull(captured).CoreTaskJson.Span));
    }

    [Test]
    public async Task Create_RejectsUnsupportedContextAndNonSingleAgentBeforePersistence()
    {
        var store = Substitute.For<IBenchmarkStore>();
        var agents = Substitute.For<IAgentDefinitionStore>();
        var agentId = Guid.NewGuid();
        agents.GetByIdAsync(agentId, Arg.Any<CancellationToken>()).Returns(Definition(agentId, AgentDefinitionKind.Orchestrator));
        var service = new BenchmarkProjectService(store, agents);

        _ = await AssertEx.ThrowsAsync<BenchmarkValidationException>(() => service.CreateAsync(
            new BenchmarkProjectDraft(Guid.NewGuid(), "Benchmark", "task", 1234, agentId, false, null, null)));
        _ = await AssertEx.ThrowsAsync<BenchmarkValidationException>(() => service.CreateAsync(
            new BenchmarkProjectDraft(Guid.NewGuid(), "Benchmark", "task", 4096, agentId, false, null, null)));

        _ = store.DidNotReceive().CreateProjectAsync(Arg.Any<BenchmarkProjectInput>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Create_WithJudge_PerformsOnlyModelIndependentValidation()
    {
        var store = Substitute.For<IBenchmarkStore>();
        BenchmarkProjectInput? captured = null;
        store.CreateProjectAsync(Arg.Do<BenchmarkProjectInput>(input => captured = input), Arg.Any<CancellationToken>())
             .Returns(call => Project(call.Arg<BenchmarkProjectInput>()));
        var agents = Substitute.For<IAgentDefinitionStore>();
        var agentId = Guid.NewGuid();
        agents.GetByIdAsync(agentId, Arg.Any<CancellationToken>()).Returns(Definition(agentId, AgentDefinitionKind.Single));
        var service = new BenchmarkProjectService(store, agents);

        _ = await service.CreateAsync(new BenchmarkProjectDraft(
            Guid.NewGuid(),
            "Benchmark",
            "task",
            4096,
            agentId,
            true,
            "  judge-model  ",
            8192));

        var input = AssertEx.NotNull(captured);
        AssertEx.Equal("judge-model", input.JudgeModelName);
        AssertEx.Equal(8192, input.JudgeContextTokens);
    }

    private static AgentDefinitionRecord Definition(Guid id, AgentDefinitionKind kind) =>
        new(id, "Agent", null, "instructions", null, null, kind, [], new Dictionary<string, bool>(), null, 1, 1, 1);

    private static BenchmarkProjectRecord Project(BenchmarkProjectInput input) =>
        new(input.Id, input.Name, input.CoreTaskJson, input.ContextTokens, input.AgentDefinitionId, input.JudgeEnabled,
            input.JudgeModelName, input.JudgeContextTokens, input.JudgePromptVersion, input.JudgeOutputSchemaVersion,
            false, 1, 1, 1);
}
