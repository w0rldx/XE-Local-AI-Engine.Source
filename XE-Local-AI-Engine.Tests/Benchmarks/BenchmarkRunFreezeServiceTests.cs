namespace XE_Local_AI_Engine.Tests.Benchmarks;

using System.Text.Json;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class BenchmarkRunFreezeServiceTests
{
    [Test]
    public async Task Start_UsesExactTaskAcquiresDistinctModelsInOrderAndCarriesCommitGuard()
    {
        var projectId = Guid.NewGuid();
        var agentId = Guid.NewGuid();
        var project = Project(projectId, agentId, judgeEnabled: true, judgeModel: "z-judge.gguf");
        var store = Substitute.For<IBenchmarkStore>();
        store.GetProjectAsync(projectId, Arg.Any<CancellationToken>()).Returns(project);
        BenchmarkStartRunCommand? capturedCommand = null;
        store.StartRunAsync(Arg.Do<BenchmarkStartRunCommand>(command => capturedCommand = command), Arg.Any<CancellationToken>())
             .Returns(call => Run(call.Arg<BenchmarkStartRunCommand>()));
        var definitions = Substitute.For<IAgentDefinitionStore>();
        definitions.GetByIdAsync(agentId, Arg.Any<CancellationToken>()).Returns(Definition(agentId));
        var resolver = Substitute.For<IAgentDefinitionResolver>();
        resolver.ResolveAsync(agentId, "a-primary.gguf", "exact task", true, false, false, Arg.Any<CancellationToken>())
                .Returns(Runtime(agentId));
        var capabilities = Substitute.For<IGgufModelCapabilityResolver>();
        capabilities.TryResolveAsync("a-primary.gguf", Arg.Any<CancellationToken>()).Returns(new GgufModelCapabilities(false, true, false));
        var leaseProvider = new RecordingLeaseProvider(new Dictionary<string, InstalledModelSnapshot>(StringComparer.OrdinalIgnoreCase)
        {
            ["a-primary.gguf"] = CreateInstalledModel("a-primary.gguf"),
            ["z-judge.gguf"] = CreateInstalledModel("z-judge.gguf")
        });
        var dependencyService = Substitute.For<IBenchmarkFreezeDependencyService>();
        var dependencySet = Dependencies("initial");
        dependencyService.CaptureAsync(agentId, Arg.Any<ResolvedAgentRuntime>(), "a-primary.gguf", "z-judge.gguf", Arg.Any<CancellationToken>())
                         .Returns(dependencySet);
        var snapshotFactory = Substitute.For<IBenchmarkRuntimeSnapshotFactory>();
        BenchmarkRuntimeSnapshotInput? snapshotInput = null;
        snapshotFactory.Create(Arg.Do<BenchmarkRuntimeSnapshotInput>(input => snapshotInput = input))
                       .Returns(call => CreateRuntimeSnapshot(call.Arg<BenchmarkRuntimeSnapshotInput>()));
        snapshotFactory.Serialize(Arg.Any<BenchmarkRuntimeSnapshotV1>()).Returns([1, 2, 3]);
        var service = new BenchmarkRunFreezeService(store,
            definitions,
            resolver,
            capabilities,
            leaseProvider,
            new BenchmarkEligibilityPolicy(),
            dependencyService,
            snapshotFactory,
            Profiles(),
            Variants(),
            TimeProvider.System);

        _ = await service.StartAsync(projectId, "a-primary.gguf", project.Version);

        AssertEx.True(leaseProvider.Acquired.SequenceEqual(["a-primary.gguf", "z-judge.gguf"]),
            "Installed-model leases must be acquired in deterministic model-name order.");
        AssertEx.Equal("exact task", AssertEx.NotNull(snapshotInput).CoreTask);
        AssertEx.Equal(GpuVariant.Cpu, snapshotInput!.PrimaryRuntime.Variant);
        AssertEx.Equal(4096, snapshotInput.PrimaryRuntime.ContextTokens);
        AssertEx.Equal(BenchmarkFrozenPolicies.FixedSeedPolicy, snapshotInput.PrimarySampling.SeedPolicy);
        AssertEx.Equal("0", snapshotInput.PrimarySampling.SeedValue);
        AssertEx.Equal(BenchmarkFrozenPolicies.JudgeSystemPrompt, snapshotInput.Judge.SystemPrompt);
        AssertEx.Equal(BenchmarkFrozenPolicies.JudgeOutputSchemaJson, snapshotInput.Judge.OutputSchemaJson);
        AssertEx.Equal(2048, AssertEx.NotNull(snapshotInput.Judge.Runtime).ContextTokens);
        AssertEx.NotNull(capturedCommand);
        AssertEx.NotNull(capturedCommand!.FreezeCommitGuard);
        AssertEx.True(leaseProvider.Leases.All(static lease => lease.Disposed), "All installed-model read leases must be released after commit.");
        _ = resolver.Received(1).ResolveAsync(agentId, "a-primary.gguf", "exact task", true, false, false, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Start_ReusesOneLeaseWhenPrimaryAndJudgeAliasMatchIgnoringCase()
    {
        var projectId = Guid.NewGuid();
        var agentId = Guid.NewGuid();
        var project = Project(projectId, agentId, judgeEnabled: true, judgeModel: "MODEL.GGUF");
        var store = Substitute.For<IBenchmarkStore>();
        store.GetProjectAsync(projectId, Arg.Any<CancellationToken>()).Returns(project);
        store.StartRunAsync(Arg.Any<BenchmarkStartRunCommand>(), Arg.Any<CancellationToken>())
             .Returns(call => Run(call.Arg<BenchmarkStartRunCommand>()));
        var definitions = Substitute.For<IAgentDefinitionStore>();
        definitions.GetByIdAsync(agentId, Arg.Any<CancellationToken>()).Returns(Definition(agentId));
        var resolver = Substitute.For<IAgentDefinitionResolver>();
        resolver.ResolveAsync(agentId, "model.gguf", "exact task", true, false, false, Arg.Any<CancellationToken>()).Returns(Runtime(agentId));
        var capabilities = Substitute.For<IGgufModelCapabilityResolver>();
        capabilities.TryResolveAsync("model.gguf", Arg.Any<CancellationToken>()).Returns(new GgufModelCapabilities(false, true, false));
        var leaseProvider = new RecordingLeaseProvider(new Dictionary<string, InstalledModelSnapshot>(StringComparer.OrdinalIgnoreCase)
        {
            ["model.gguf"] = CreateInstalledModel("model.gguf")
        });
        var dependencies = Substitute.For<IBenchmarkFreezeDependencyService>();
        dependencies.CaptureAsync(Arg.Any<Guid>(), Arg.Any<ResolvedAgentRuntime>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                    .Returns(Dependencies("same"));
        var snapshots = Substitute.For<IBenchmarkRuntimeSnapshotFactory>();
        snapshots.Create(Arg.Any<BenchmarkRuntimeSnapshotInput>()).Returns(call => CreateRuntimeSnapshot(call.Arg<BenchmarkRuntimeSnapshotInput>()));
        snapshots.Serialize(Arg.Any<BenchmarkRuntimeSnapshotV1>()).Returns([1]);
        var service = new BenchmarkRunFreezeService(store, definitions, resolver, capabilities, leaseProvider,
            new BenchmarkEligibilityPolicy(), dependencies, snapshots, Profiles(), Variants(), TimeProvider.System);

        _ = await service.StartAsync(projectId, "model.gguf", project.Version);

        AssertEx.Equal(expected: 1, leaseProvider.Acquired.Count);
        AssertEx.Equal(expected: 1, leaseProvider.Leases.Count);
        AssertEx.True(leaseProvider.Leases[0].Disposed, "The reused lease must be disposed exactly once.");
    }

    private static BenchmarkProjectRecord Project(Guid id, Guid agentId, bool judgeEnabled, string? judgeModel) =>
        new(id, "Benchmark", JsonSerializer.SerializeToUtf8Bytes("exact task"), 4096, agentId, judgeEnabled, judgeModel,
            judgeEnabled ? 2048 : null, 1, 1, false, 7, 1, 1);

    private static AgentDefinitionRecord Definition(Guid id) =>
        new(id, "Agent", null, "instructions", null, null, AgentDefinitionKind.Single, [], new Dictionary<string, bool>(), null, 3, 1, 1);

    private static ResolvedAgentRuntime Runtime(Guid id) =>
        new("prompt", [], null, null, 3, id, "Agent", Kind: AgentDefinitionKind.Single);

    private static InstalledModelSnapshot CreateInstalledModel(string name)
    {
        var v1 = "v1:" + new string('a', 64);
        return new InstalledModelSnapshot(name,
            v1,
            [],
            v1,
            [new InstalledModelPhysicalMember(name, InstalledModelPhysicalMemberRole.Weight, 12, new string('b', 64),
                "sha256:" + new string('b', 64) + ":12", [name], true, null)],
            v1,
            LocalModelOrigin.Imported,
            "llamacpp",
            "map-revision",
            "repo/model",
            "revision",
            "Q4_K_M",
            GgufRole.Chat,
            v1);
    }

    private static BenchmarkFreezeDependencySetV1 Dependencies(string value) => new(value, value, value, value, value, value);

    private static BenchmarkRuntimeSnapshotV1 CreateRuntimeSnapshot(BenchmarkRuntimeSnapshotInput input) =>
        new(1, input.ProjectId, input.AgentDefinitionId, input.AgentVersion, input.CoreTask, input.RequestedContextTokens,
            input.ResolvedRuntime, input.PrimaryRuntime, input.PrimarySampling, input.PrimaryModel, input.Judge, input.Dependencies,
            input.ApplicationVersion, input.CreatedAtUtc, "hash");

    private static IInferenceProfileResolver Profiles()
    {
        var profiles = Substitute.For<IInferenceProfileResolver>();
        profiles.ResolveAsync(Arg.Any<string>(), Arg.Any<ModelRole>(), Arg.Any<GpuVariant>(), Arg.Any<CancellationToken>())
                .Returns(call => ResolvedLaunchArguments.Replay(call.ArgAt<string>(0).StartsWith("z-", StringComparison.Ordinal) ? 2048 : 4096));
        return profiles;
    }

    private static IGpuVariantSelector Variants()
    {
        var variants = Substitute.For<IGpuVariantSelector>();
        variants.SelectVariantAsync(Arg.Any<CancellationToken>()).Returns(GpuVariant.Cpu);
        return variants;
    }

    private static BenchmarkRunRecord Run(BenchmarkStartRunCommand command) =>
        new(command.RunId, command.ProjectId, command.RuntimeSnapshotJson, command.PrimaryModelName, command.PrimaryModelOrigin,
            command.ModelContentFingerprint, command.AgentName, command.AgentVersion, command.RequestedContextTokens,
            BenchmarkPrimaryStatus.Queued, null, null, null, null, null, 0, null,
            command.JudgeEnabled ? BenchmarkJudgeStatus.Pending : BenchmarkJudgeStatus.Disabled, null, null, null, 1, 1, null, null, null, null, 1);

    private sealed class RecordingLeaseProvider(IReadOnlyDictionary<string, InstalledModelSnapshot> snapshots) : IBenchmarkInstalledModelLeaseProvider
    {
        public List<string> Acquired { get; } = [];
        public List<RecordingLease> Leases { get; } = [];

        public Task<IBenchmarkInstalledModelLease> AcquireAsync(string modelName, CancellationToken cancellationToken)
        {
            Acquired.Add(modelName);
            var lease = new RecordingLease(snapshots[modelName]);
            Leases.Add(lease);
            return Task.FromResult<IBenchmarkInstalledModelLease>(lease);
        }
    }

    private sealed class RecordingLease(InstalledModelSnapshot snapshot) : IBenchmarkInstalledModelLease
    {
        public InstalledModelSnapshot Snapshot { get; } = snapshot;
        public bool Disposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
