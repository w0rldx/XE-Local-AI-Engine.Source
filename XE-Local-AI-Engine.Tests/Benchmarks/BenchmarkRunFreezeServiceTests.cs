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
        var harness = new FreezeHarness(judgeModel: "z-judge.gguf");

        _ = await harness.StartAsync();

        AssertEx.True(harness.LeaseProvider.Acquired.SequenceEqual(["a-primary.gguf", "z-judge.gguf"]),
            "Installed-model leases must be acquired in deterministic model-name order.");
        AssertEx.Equal("exact task", AssertEx.NotNull(harness.SnapshotInput).CoreTask);
        AssertEx.Equal(GpuVariant.Cpu, harness.SnapshotInput!.PrimaryRuntime.Variant);
        AssertEx.Equal(4096, harness.SnapshotInput.PrimaryRuntime.ContextTokens);
        AssertEx.Equal(BenchmarkFrozenPolicies.FixedSeedPolicy, harness.SnapshotInput.PrimarySampling.SeedPolicy);
        AssertEx.Equal("0", harness.SnapshotInput.PrimarySampling.SeedValue);
        AssertEx.Equal(BenchmarkFrozenPolicies.JudgeSystemPrompt, harness.SnapshotInput.Judge.SystemPrompt);
        AssertEx.Equal(BenchmarkFrozenPolicies.JudgeOutputSchemaJson, harness.SnapshotInput.Judge.OutputSchemaJson);
        AssertEx.Equal(2048, AssertEx.NotNull(harness.SnapshotInput.Judge.Runtime).ContextTokens);
        AssertEx.NotNull(harness.Command);
        AssertEx.NotNull(harness.Command!.FreezeCommitGuard);
        AssertEx.True(harness.LeaseProvider.Leases.All(static lease => lease.Disposed), "All installed-model read leases must be released after commit.");
        _ = harness.Resolver.Received(1).ResolveAsync(harness.AgentId, "a-primary.gguf", "exact task", true, false, false, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Start_ReusesOneLeaseWhenPrimaryAndJudgeAliasMatchIgnoringCase()
    {
        var harness = new FreezeHarness(primaryModel: "model.gguf", judgeModel: "MODEL.GGUF");

        _ = await harness.StartAsync();

        AssertEx.Equal(expected: 1, harness.LeaseProvider.Acquired.Count);
        AssertEx.Equal(expected: 1, harness.LeaseProvider.Leases.Count);
        AssertEx.True(harness.LeaseProvider.Leases[0].Disposed, "The reused lease must be disposed exactly once.");
    }

    [Test]
    public async Task Start_AutoOnAGpuBinaryThatAcceptsTheOptimizedVector_FreezesQuantizedKv()
    {
        var harness = new FreezeHarness(variant: GpuVariant.Cuda);

        _ = await harness.StartAsync();

        var intent = AssertEx.NotNull(harness.Command!.PrimaryLaunchIntent);
        AssertEx.Equal(BenchmarkKvCacheType.Q8_0, intent.KvCacheType);
        AssertEx.Equal(BenchmarkKvCacheType.SourceAuto, intent.KvCacheTypeSource);
        AssertEx.Null(intent.KvAutoReason);
        AssertEx.Equal(LlamaServerLaunchProjection.FlashAttentionOn, intent.FlashAttentionMode);
        AssertEx.Equal("cuda", intent.Variant);
        AssertEx.Equal("manifest-sha", intent.IntendedExecutableSha256);
        var runtime = AssertEx.NotNull(harness.SnapshotInput).PrimaryRuntime;
        AssertEx.Equal(BenchmarkKvCacheType.Q8_0, runtime.KvTypeK);
        AssertEx.Equal(BenchmarkKvCacheType.Q8_0, runtime.KvTypeV);
        AssertEx.True(runtime.FlashAttention, "A quantized KV cache must pin the fused flash-attention path.");
    }

    [Test]
    public async Task Start_AutoWhenTheManifestDoesNotAdvertiseTheVector_FreezesF16WithAReason()
    {
        var harness = new FreezeHarness(variant: GpuVariant.Cuda, supportsQuantizedKv: false);

        _ = await harness.StartAsync();

        var intent = AssertEx.NotNull(harness.Command!.PrimaryLaunchIntent);
        AssertEx.Equal(BenchmarkKvCacheType.F16, intent.KvCacheType);
        AssertEx.Equal(BenchmarkKvCacheType.SourceAuto, intent.KvCacheTypeSource);
        AssertEx.Equal(BenchmarkRunFreezeService.AutoReasonManifestUnsupported, intent.KvAutoReason);
        AssertEx.Equal(LlamaServerLaunchProjection.FlashAttentionAuto, intent.FlashAttentionMode);
        AssertEx.Null(AssertEx.NotNull(harness.SnapshotInput).PrimaryRuntime.KvTypeK);
    }

    [Test]
    public async Task Start_AutoWhenTheOptimizedConfigWasRecordedAsFailing_FreezesF16WithAReason()
    {
        var harness = new FreezeHarness(variant: GpuVariant.Cuda, optimizedConfigDisabled: true);

        _ = await harness.StartAsync();

        var intent = AssertEx.NotNull(harness.Command!.PrimaryLaunchIntent);
        AssertEx.Equal(BenchmarkKvCacheType.F16, intent.KvCacheType);
        AssertEx.Equal(BenchmarkRunFreezeService.AutoReasonFallbackDisabled, intent.KvAutoReason);
    }

    [Test]
    public async Task Start_AutoWhenTheBinaryCouldNotBeProbed_FreezesF16WithAReason()
    {
        var harness = new FreezeHarness(variant: GpuVariant.Cuda, probeSucceeded: false);

        _ = await harness.StartAsync();

        var intent = AssertEx.NotNull(harness.Command!.PrimaryLaunchIntent);
        AssertEx.Equal(BenchmarkKvCacheType.F16, intent.KvCacheType);
        AssertEx.Equal(BenchmarkRunFreezeService.AutoReasonProbeUnavailable, intent.KvAutoReason);
    }

    [Test]
    public async Task Start_AutoOnACpuBuild_FreezesF16WithAReason()
    {
        var harness = new FreezeHarness();

        _ = await harness.StartAsync();

        var intent = AssertEx.NotNull(harness.Command!.PrimaryLaunchIntent);
        AssertEx.Equal(BenchmarkKvCacheType.F16, intent.KvCacheType);
        AssertEx.Equal(BenchmarkRunFreezeService.AutoReasonCpuVariant, intent.KvAutoReason);
        AssertEx.Equal("cpu", intent.Variant);
    }

    [Test]
    public async Task Start_ExplicitF16OnACpuBuild_IsAccepted()
    {
        var harness = new FreezeHarness();

        _ = await harness.StartAsync(BenchmarkKvCacheType.F16);

        var intent = AssertEx.NotNull(harness.Command!.PrimaryLaunchIntent);
        AssertEx.Equal(BenchmarkKvCacheType.F16, intent.KvCacheType);
        AssertEx.Equal(BenchmarkKvCacheType.SourceExplicit, intent.KvCacheTypeSource);
        AssertEx.Null(intent.KvAutoReason);
    }

    [Test]
    public async Task Start_ExplicitQuantizedKvOnACpuBuild_IsRefused()
    {
        var harness = new FreezeHarness();

        var exception = await AssertEx.ThrowsAsync<BenchmarkUnsupportedKvCacheTypeException>(() => harness.StartAsync(BenchmarkKvCacheType.Q4_0));

        AssertEx.Contains(exception.Message, "CPU");
    }

    [Test]
    public async Task Start_ExplicitQuantizedKvTheManifestDoesNotAdvertise_IsRefused()
    {
        var harness = new FreezeHarness(variant: GpuVariant.Cuda, supportsQuantizedKv: false);

        var exception = await AssertEx.ThrowsAsync<BenchmarkUnsupportedKvCacheTypeException>(() => harness.StartAsync(BenchmarkKvCacheType.Q8_0));

        AssertEx.Contains(exception.Message, "does not accept");
    }

    [Test]
    public async Task Start_ExplicitQuantizedKvWhenTheBinaryCouldNotBeProbed_IsRefusedNamingTheProbe()
    {
        var harness = new FreezeHarness(variant: GpuVariant.Cuda, probeSucceeded: false);

        var exception = await AssertEx.ThrowsAsync<BenchmarkUnsupportedKvCacheTypeException>(() => harness.StartAsync(BenchmarkKvCacheType.Q8_0));

        AssertEx.Contains(exception.Message, "could not be inspected");
    }

    [Test]
    public async Task Start_ExplicitPickOverAFrozenProfile_KeepsThePlacementItWasFittedWith()
    {
        var harness = new FreezeHarness(variant: GpuVariant.Cuda,
            profile: static context => ResolvedLaunchArguments.Replay(context, 33, "0.7,0.3", "exps=CPU", BenchmarkKvCacheType.Q4_0, BenchmarkKvCacheType.Q4_0, flashAttn: true));

        _ = await harness.StartAsync(BenchmarkKvCacheType.F16);

        var runtime = AssertEx.NotNull(harness.SnapshotInput).PrimaryRuntime;
        AssertEx.Equal<int?>(33, runtime.GpuLayers);
        AssertEx.Equal("0.7,0.3", runtime.TensorSplit);
        AssertEx.Equal("exps=CPU", runtime.OverrideTensor);
        AssertEx.Null(runtime.KvTypeK);
        AssertEx.Null(runtime.KvTypeV);
        AssertEx.False(runtime.FlashAttention, "Dropping the quantized KV cache must drop the flag it requires with it.");
    }

    [Test]
    public async Task Start_JudgePhase_IgnoresTheRunsPickAndResolvesAuto()
    {
        var harness = new FreezeHarness(variant: GpuVariant.Cuda, judgeModel: "z-judge.gguf");

        _ = await harness.StartAsync(BenchmarkKvCacheType.Q4_0);

        AssertEx.Equal(BenchmarkKvCacheType.Q4_0, AssertEx.NotNull(harness.Command!.PrimaryLaunchIntent).KvCacheType);
        var judge = AssertEx.NotNull(harness.Command.JudgeLaunchIntent);
        AssertEx.Equal(BenchmarkKvCacheType.Q8_0, judge.KvCacheType);
        AssertEx.Equal(BenchmarkKvCacheType.SourceAuto, judge.KvCacheTypeSource);
    }

    [Test]
    public async Task Start_IntendedLaunchIdentity_IsTheProjectionOfTheFrozenVector()
    {
        var harness = new FreezeHarness(variant: GpuVariant.Cuda,
            profile: static context => ResolvedLaunchArguments.Replay(context, 33));

        _ = await harness.StartAsync(BenchmarkKvCacheType.Q8_0);

        var runtime = AssertEx.NotNull(harness.SnapshotInput).PrimaryRuntime;
        var policy = LlamaServerBenchmarkLaunchPolicy.DeterministicV1;
        var expected = LlamaServerLaunchProjection.From(GpuVariant.Cuda,
                                                      runtime.ToResolvedLaunchArguments(),
                                                      plan: null,
                                                      ModelRole.Chat,
                                                      policy.ChatCacheReuse,
                                                      policy.ChatCacheRamMiB)
                                                  .ComputeIdentity();
        AssertEx.Equal(expected, AssertEx.NotNull(harness.Command!.PrimaryLaunchIntent).IntendedLaunchIdentity);
    }

    [Test]
    public async Task Start_UnknownKvCacheType_IsRejectedBeforeAnythingIsFrozen()
    {
        var harness = new FreezeHarness();

        _ = await AssertEx.ThrowsAsync<BenchmarkValidationException>(() => harness.StartAsync("q3_k"));

        AssertEx.Null(harness.Command);
    }

    /// <summary>
    ///     One freeze wired end to end. Everything but the KV decision is held constant so a matrix test reads as the
    ///     single input it varies.
    /// </summary>
    private sealed class FreezeHarness
    {
        private readonly string _primaryModel;
        private readonly BenchmarkProjectRecord _project;
        private readonly BenchmarkRunFreezeService _service;

        public FreezeHarness(string primaryModel = "a-primary.gguf",
            string? judgeModel = null,
            GpuVariant variant = GpuVariant.Cpu,
            bool probeSucceeded = true,
            bool supportsQuantizedKv = true,
            bool optimizedConfigDisabled = false,
            Func<int, ResolvedLaunchArguments>? profile = null)
        {
            _primaryModel = primaryModel;
            AgentId = Guid.NewGuid();
            _project = Project(Guid.NewGuid(), AgentId, judgeModel is not null, judgeModel);
            var store = Substitute.For<IBenchmarkStore>();
            store.GetProjectAsync(_project.Id, Arg.Any<CancellationToken>()).Returns(_project);
            store.StartRunAsync(Arg.Do<BenchmarkStartRunCommand>(command => Command = command), Arg.Any<CancellationToken>())
                 .Returns(call => Run(call.Arg<BenchmarkStartRunCommand>()));

            var definitions = Substitute.For<IAgentDefinitionStore>();
            definitions.GetByIdAsync(AgentId, Arg.Any<CancellationToken>()).Returns(Definition(AgentId));
            Resolver = Substitute.For<IAgentDefinitionResolver>();
            Resolver.ResolveAsync(AgentId, Arg.Any<string>(), "exact task", true, false, false, Arg.Any<CancellationToken>()).Returns(Runtime(AgentId));
            var capabilities = Substitute.For<IGgufModelCapabilityResolver>();
            capabilities.TryResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new GgufModelCapabilities(false, true, false));

            var models = new Dictionary<string, InstalledModelSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                [primaryModel] = CreateInstalledModel(primaryModel)
            };
            if (judgeModel is not null)
            {
                models[judgeModel] = CreateInstalledModel(judgeModel);
            }

            LeaseProvider = new RecordingLeaseProvider(models);
            var dependencies = Substitute.For<IBenchmarkFreezeDependencyService>();
            dependencies.CaptureAsync(Arg.Any<Guid>(), Arg.Any<ResolvedAgentRuntime>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                        .Returns(Dependencies("initial"));
            var snapshots = Substitute.For<IBenchmarkRuntimeSnapshotFactory>();
            snapshots.Create(Arg.Do<BenchmarkRuntimeSnapshotInput>(input => SnapshotInput = input))
                     .Returns(call => CreateRuntimeSnapshot(call.Arg<BenchmarkRuntimeSnapshotInput>()));
            snapshots.Serialize(Arg.Any<BenchmarkRuntimeSnapshotV1>()).Returns([1, 2, 3]);

            _service = new BenchmarkRunFreezeService(store,
                definitions,
                Resolver,
                capabilities,
                LeaseProvider,
                new BenchmarkEligibilityPolicy(),
                dependencies,
                snapshots,
                Profiles(profile),
                Variants(variant),
                Inspector(variant, probeSucceeded, supportsQuantizedKv),
                FallbackStore(optimizedConfigDisabled),
                LaunchPolicy(),
                TimeProvider.System);
        }

        public Guid AgentId { get; }
        public IAgentDefinitionResolver Resolver { get; }
        public RecordingLeaseProvider LeaseProvider { get; }
        public BenchmarkStartRunCommand? Command { get; private set; }
        public BenchmarkRuntimeSnapshotInput? SnapshotInput { get; private set; }

        public Task<BenchmarkRunRecord> StartAsync(string? kvCacheType = null) =>
            _service.StartAsync(_project.Id, _primaryModel, _project.Version, kvCacheType);

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
                [
                    new InstalledModelPhysicalMember(name, InstalledModelPhysicalMemberRole.Weight, 12, new string('b', 64),
                        "sha256:" + new string('b', 64) + ":12", [name], true, null)
                ],
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

        private static BenchmarkFreezeDependencySetV1 Dependencies(string value) =>
            new(value, value, value, value, value, value);

        private static BenchmarkRuntimeSnapshotV1 CreateRuntimeSnapshot(BenchmarkRuntimeSnapshotInput input) =>
            new(1, input.ProjectId, input.AgentDefinitionId, input.AgentVersion, input.CoreTask, input.RequestedContextTokens,
                input.ResolvedRuntime, input.PrimaryRuntime, input.PrimarySampling, input.PrimaryModel, input.Judge, input.Dependencies,
                input.ApplicationVersion, input.CreatedAtUtc, "hash");

        private static IInferenceProfileResolver Profiles(Func<int, ResolvedLaunchArguments>? profile)
        {
            var profiles = Substitute.For<IInferenceProfileResolver>();
            profiles.ResolveAsync(Arg.Any<string>(), Arg.Any<ModelRole>(), Arg.Any<GpuVariant>(), Arg.Any<CancellationToken>())
                    .Returns(call =>
                    {
                        var context = call.ArgAt<string>(0).StartsWith("z-", StringComparison.Ordinal) ? 2048 : 4096;
                        return profile is null ? ResolvedLaunchArguments.Replay(context) : profile(context);
                    });
            return profiles;
        }

        private static IGpuVariantSelector Variants(GpuVariant variant)
        {
            var variants = Substitute.For<IGpuVariantSelector>();
            variants.SelectVariantAsync(Arg.Any<CancellationToken>()).Returns(variant);
            return variants;
        }

        private static ILlamaServerLaunchCapabilityInspector Inspector(GpuVariant variant, bool probeSucceeded, bool supportsQuantizedKv)
        {
            var cacheTypes = supportsQuantizedKv
                ? new HashSet<string>(StringComparer.Ordinal)
                {
                    BenchmarkKvCacheType.F16,
                    BenchmarkKvCacheType.Q8_0,
                    BenchmarkKvCacheType.Q4_0
                }
                : new HashSet<string>(StringComparer.Ordinal)
                {
                    BenchmarkKvCacheType.F16
                };
            var flashAttention = supportsQuantizedKv
                ? new HashSet<string>(StringComparer.Ordinal)
                {
                    LlamaServerLaunchProjection.FlashAttentionAuto,
                    LlamaServerLaunchProjection.FlashAttentionOn
                }
                : new HashSet<string>(StringComparer.Ordinal)
                {
                    LlamaServerLaunchProjection.FlashAttentionAuto
                };
            var inspector = Substitute.For<ILlamaServerLaunchCapabilityInspector>();
            inspector.InspectAsync(Arg.Any<CancellationToken>())
                     .Returns(new LlamaServerLaunchCapabilities(variant, probeSucceeded, "b10201", "manifest-sha", cacheTypes, cacheTypes, flashAttention));
            return inspector;
        }

        private static ILlamaServerLaunchFallbackStore FallbackStore(bool disabled)
        {
            var store = Substitute.For<ILlamaServerLaunchFallbackStore>();
            store.IsOptimizedConfigDisabledAsync(Arg.Any<GpuVariant>(), Arg.Any<CancellationToken>()).Returns(disabled);
            return store;
        }

        private static ILlamaServerLaunchPolicy LaunchPolicy()
        {
            var policy = Substitute.For<ILlamaServerLaunchPolicy>();
            policy.ResolveCpuReplayPlan(Arg.Any<ResolvedLaunchArguments>())
                  .Returns(call => new LlamaServerLaunchPlan(call.Arg<ResolvedLaunchArguments>().CtxSize, false, BenchmarkKvCacheType.Q8_0, 8, 8));
            return policy;
        }

        private static BenchmarkRunRecord Run(BenchmarkStartRunCommand command) =>
            new(command.RunId, command.ProjectId, command.RuntimeSnapshotJson, command.PrimaryModelName, command.PrimaryModelOrigin,
                command.ModelContentFingerprint, command.AgentName, command.AgentVersion, command.RequestedContextTokens,
                BenchmarkPrimaryStatus.Queued, null, null, null, null, null, 0, null,
                command.JudgeEnabled ? BenchmarkJudgeStatus.Pending : BenchmarkJudgeStatus.Disabled, null, null, null, 1, 1, null, null, null, null, 1);
    }

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
