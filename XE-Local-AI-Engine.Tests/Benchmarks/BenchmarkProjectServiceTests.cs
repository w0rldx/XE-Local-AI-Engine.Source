namespace XE_Local_AI_Engine.Tests.Benchmarks;

using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
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
        var service = new BenchmarkProjectService(store, agents, JudgeModels());

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
        var service = new BenchmarkProjectService(store, agents, JudgeModels());

        _ = await AssertEx.ThrowsAsync<BenchmarkValidationException>(() => service.CreateAsync(
            new BenchmarkProjectDraft(Guid.NewGuid(), "Benchmark", "task", 1234, agentId, false, null, null)));
        _ = await AssertEx.ThrowsAsync<BenchmarkValidationException>(() => service.CreateAsync(
            new BenchmarkProjectDraft(Guid.NewGuid(), "Benchmark", "task", 4096, agentId, false, null, null)));

        _ = store.DidNotReceive().CreateProjectAsync(Arg.Any<BenchmarkProjectInput>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Create_WithJudge_ValidatesAndNormalizesEligibleLocalModel()
    {
        var store = Substitute.For<IBenchmarkStore>();
        BenchmarkProjectInput? captured = null;
        store.CreateProjectAsync(Arg.Do<BenchmarkProjectInput>(input => captured = input), Arg.Any<CancellationToken>())
             .Returns(call => Project(call.Arg<BenchmarkProjectInput>()));
        var agents = Substitute.For<IAgentDefinitionStore>();
        var agentId = Guid.NewGuid();
        agents.GetByIdAsync(agentId, Arg.Any<CancellationToken>()).Returns(Definition(agentId, AgentDefinitionKind.Single));
        var service = new BenchmarkProjectService(store, agents, JudgeModels());

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

    [Test]
    public async Task Create_WithJudge_RejectsUnsupportedVersionsAndIneligibleLocalModel()
    {
        var store = Substitute.For<IBenchmarkStore>();
        var agents = Substitute.For<IAgentDefinitionStore>();
        var agentId = Guid.NewGuid();
        agents.GetByIdAsync(agentId, Arg.Any<CancellationToken>()).Returns(Definition(agentId, AgentDefinitionKind.Single));
        var missingModels = Substitute.For<IBenchmarkInstalledModelLeaseProvider>();
        missingModels.AcquireAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns<Task<IBenchmarkInstalledModelLease>>(_ => throw new KeyNotFoundException());
        var service = new BenchmarkProjectService(store, agents, missingModels);

        _ = await AssertEx.ThrowsAsync<BenchmarkValidationException>(() => service.CreateAsync(new BenchmarkProjectDraft(
            Guid.NewGuid(), "Benchmark", "task", 4096, agentId, true, "missing", 4096)));

        var installedService = new BenchmarkProjectService(store, agents, JudgeModels());
        _ = await AssertEx.ThrowsAsync<BenchmarkValidationException>(() => installedService.CreateAsync(new BenchmarkProjectDraft(
            Guid.NewGuid(), "Benchmark", "task", 4096, agentId, true, "judge", 4096, JudgePromptVersion: 2)));

        _ = store.DidNotReceive().CreateProjectAsync(Arg.Any<BenchmarkProjectInput>(), Arg.Any<CancellationToken>());
    }

    private static AgentDefinitionRecord Definition(Guid id, AgentDefinitionKind kind) =>
        new(id, "Agent", null, "instructions", null, null, kind, [], new Dictionary<string, bool>(), null, 1, 1, 1);

    private static BenchmarkProjectRecord Project(BenchmarkProjectInput input) =>
        new(input.Id, input.Name, input.CoreTaskJson, input.ContextTokens, input.AgentDefinitionId, input.JudgeEnabled,
            input.JudgeModelName, input.JudgeContextTokens, input.JudgePromptVersion, input.JudgeOutputSchemaVersion,
            false, 1, 1, 1);

    private static IBenchmarkInstalledModelLeaseProvider JudgeModels()
    {
        var provider = Substitute.For<IBenchmarkInstalledModelLeaseProvider>();
        provider.AcquireAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(call => Task.FromResult<IBenchmarkInstalledModelLease>(new JudgeLease(Installed(call.ArgAt<string>(0)))));
        return provider;
    }

    private static InstalledModelSnapshot Installed(string modelName)
    {
        var revision = $"v1:{new string('a', 64)}";
        return new InstalledModelSnapshot(modelName,
            revision,
            [],
            revision,
            [new InstalledModelPhysicalMember(modelName,
                InstalledModelPhysicalMemberRole.Weight,
                12,
                new string('b', 64),
                $"sha256:{new string('b', 64)}:12",
                [modelName],
                true,
                null)],
            revision,
            LocalModelOrigin.Imported,
            "llamacpp",
            "map-revision",
            "repo/judge",
            "revision",
            "Q4_K_M",
            GgufRole.Chat,
            revision);
    }

    private sealed class JudgeLease(InstalledModelSnapshot snapshot) : IBenchmarkInstalledModelLease
    {
        public InstalledModelSnapshot Snapshot { get; } = snapshot;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
