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

        // Only the primary: the judge is defined by the project's policy revision and frozen per attempt, so a freeze
        // neither leases nor resolves it.
        AssertEx.True(harness.LeaseProvider.Acquired.SequenceEqual(["a-primary.gguf"]),
            "A freeze must lease exactly the primary model.");
        AssertEx.Equal("exact task", AssertEx.NotNull(harness.SnapshotInput).CoreTask);
        AssertEx.Equal(GpuVariant.Cpu, harness.SnapshotInput!.PrimaryRuntime.Variant);
        AssertEx.Equal(4096, harness.SnapshotInput.PrimaryRuntime.ContextTokens);
        AssertEx.Equal(BenchmarkFrozenPolicies.FixedSeedPolicy, harness.SnapshotInput.PrimarySampling.SeedPolicy);
        AssertEx.Equal("0", harness.SnapshotInput.PrimarySampling.SeedValue);
        AssertEx.NotNull(harness.Command);
        AssertEx.NotNull(harness.Command!.FreezeCommitGuard);
        AssertEx.True(harness.LeaseProvider.Leases.All(static lease => lease.Disposed), "All installed-model read leases must be released after commit.");
        _ = harness.Resolver.Received(1).ResolveAsync(harness.AgentId, "a-primary.gguf", "exact task", true, false, false, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Start_WithRepeatsAndAWarmup_EnqueuesOneGroupInQueueOrderAgainstASingleFreeze()
    {
        var harness = new FreezeHarness();

        var runs = await harness.StartAsync(repeatCount: 3, warmup: true);

        // ONE freeze, four inserts. Re-freezing per repeat could straddle a runtime swap and give two runs of the same
        // "group" different snapshots — which is exactly the variable a repeat is supposed to hold still.
        AssertEx.Equal(1, harness.SnapshotsCreated, "A repeat group must be frozen once and shared.");
        AssertEx.Equal(4, runs.Count);
        AssertEx.Equal(4, harness.Commands.Count);
        var groupId = harness.Commands[0].RepeatGroupId;
        AssertEx.True(groupId is not null && groupId != Guid.Empty, "A repeat group must carry a real id.");
        AssertEx.True(harness.Commands.TrueForAll(command => command.RepeatGroupId == groupId), "Every run of a group shares its id.");
        AssertEx.True(harness.Commands.Select(static command => command.RepeatIndex).SequenceEqual<int?>([0, 1, 2, 3]),
            "The warm-up is index 0 and the measured repeats are 1..N, in queue order.");
        AssertEx.True(harness.Commands.Select(static command => command.IsWarmup).SequenceEqual([true, false, false, false]),
            "Only the index-0 run is a warm-up.");

        // Each insert bumps the project version by exactly one, so insert i must present expectedVersion + i. Getting
        // this wrong would make every repeat after the first fail its CAS.
        AssertEx.True(harness.Commands.Select(static command => command.ExpectedProjectVersion).SequenceEqual([7L, 8L, 9L, 10L]),
            "The version each insert presents chains off its predecessor.");
    }

    [Test]
    public async Task Start_ForAPlainSingleRun_LeavesTheRepeatColumnsNull()
    {
        var harness = new FreezeHarness();

        _ = await harness.StartAsync();

        // A group only exists when there is something to group; a single run must look exactly as it always did.
        AssertEx.Null(AssertEx.NotNull(harness.Command).RepeatGroupId);
        AssertEx.Null(harness.Command!.RepeatIndex);
        AssertEx.False(harness.Command.IsWarmup, "A single run is never a warm-up.");
    }

    [Test]
    public async Task Start_WithRepeatsButNoWarmup_NumbersTheRepeatsFromOne()
    {
        var harness = new FreezeHarness();

        _ = await harness.StartAsync(repeatCount: 2, warmup: false);

        AssertEx.True(harness.Commands.Select(static command => command.RepeatIndex).SequenceEqual<int?>([1, 2]),
            "Without a warm-up there is no index 0 — the measured repeats still start at 1.");
        AssertEx.True(harness.Commands.TrueForAll(static command => !command.IsWarmup), "Nothing is a warm-up unless one was asked for.");
    }

    [Test]
    [Arguments(0)]
    [Arguments(11)]
    public async Task Start_WithARepeatCountOutOfRange_IsRejectedBeforeAnythingIsFrozen(int repeatCount)
    {
        var harness = new FreezeHarness();

        _ = await AssertEx.ThrowsAsync<BenchmarkValidationException>(async () => await harness.StartAsync(repeatCount, warmup: false));

        AssertEx.Equal(0, harness.Commands.Count, "A rejected repeat count must not leave a partial group behind.");
    }

    [Test]
    public async Task Start_CopiesTheProjectGenerationTimeoutOntoTheRun()
    {
        var pinned = new FreezeHarness(invocationTimeoutSeconds: 1800);
        var defaulted = new FreezeHarness();

        _ = await pinned.StartAsync();
        _ = await defaulted.StartAsync();

        // Copied onto the run, not read from the project at execution: a run replays with the budget it was started
        // under, exactly like its context.
        AssertEx.Equal<int?>(1800, AssertEx.NotNull(pinned.Command).InvocationTimeoutSeconds);
        AssertEx.Null(AssertEx.NotNull(defaulted.Command).InvocationTimeoutSeconds, "No project setting means the node default.");
    }

    [Test]
    public async Task Start_FreezesTheProjectOutputBudgetIntoTheRunSampling()
    {
        var budgeted = new FreezeHarness(maxOutputTokens: 2048);
        var unbudgeted = new FreezeHarness();

        _ = await budgeted.StartAsync();
        _ = await unbudgeted.StartAsync();

        AssertEx.Equal<int?>(2048, AssertEx.NotNull(budgeted.SnapshotInput).PrimarySampling.MaxOutputTokens,
            "The budget is frozen per run, so a later project edit cannot change what an existing run replays.");
        AssertEx.Null(AssertEx.NotNull(unbudgeted.SnapshotInput).PrimarySampling.MaxOutputTokens,
            "No budget means context-limited, which is the sampling every existing snapshot already hashes.");
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
    public async Task Start_FreezesTheVariantTheInspectedBinaryReports_SoTheIntendedDigestDescribesIt()
    {
        // A second selection could answer differently from the one the inspection used; the digest recorded as
        // INTENDED must belong to the binary that was actually inspected, or every run reads as intended != effective.
        var harness = new FreezeHarness(variant: GpuVariant.Cpu, inspectedVariant: GpuVariant.Cuda, judgeModel: "z-judge.gguf");

        _ = await harness.StartAsync();

        var intent = AssertEx.NotNull(harness.Command!.PrimaryLaunchIntent);
        AssertEx.Equal("cuda", intent.Variant);
        AssertEx.Equal("manifest-sha", intent.IntendedExecutableSha256);
        AssertEx.Equal(BenchmarkKvCacheType.Q8_0, intent.KvCacheType, "The inspected GPU binary's own capabilities decide Auto.");
        AssertEx.Equal(GpuVariant.Cuda, AssertEx.NotNull(harness.SnapshotInput).PrimaryRuntime.Variant);
    }

    [Test]
    public async Task Start_WhenTheBinaryCannotBeInspected_FallsBackToTheSelectedVariantAndRecordsNoDigest()
    {
        var harness = new FreezeHarness(variant: GpuVariant.Cuda, inspectionFails: true);

        _ = await harness.StartAsync();

        var intent = AssertEx.NotNull(harness.Command!.PrimaryLaunchIntent);
        AssertEx.Equal("cuda", intent.Variant);
        AssertEx.Null(intent.IntendedExecutableSha256, "No inspection means no digest to claim as intended.");
        AssertEx.Equal(BenchmarkKvCacheType.F16, intent.KvCacheType);
        AssertEx.Equal(BenchmarkRunFreezeService.AutoReasonProbeUnavailable, intent.KvAutoReason);
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
            GpuVariant? inspectedVariant = null,
            bool inspectionFails = false,
            bool probeSucceeded = true,
            bool supportsQuantizedKv = true,
            bool optimizedConfigDisabled = false,
            Func<int, ResolvedLaunchArguments>? profile = null,
            int? maxOutputTokens = null,
            int? invocationTimeoutSeconds = null)
        {
            _primaryModel = primaryModel;
            AgentId = Guid.NewGuid();
            _project = Project(Guid.NewGuid(), AgentId, judgeModel is not null, judgeModel, maxOutputTokens, invocationTimeoutSeconds);
            var store = Substitute.For<IBenchmarkStore>();
            store.GetProjectAsync(_project.Id, Arg.Any<CancellationToken>()).Returns(_project);
            store.StartRunAsync(Arg.Do<BenchmarkStartRunCommand>(command =>
                     {
                         Command = command;
                         Commands.Add(command);
                     }),
                     Arg.Any<CancellationToken>())
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
            snapshots.Create(Arg.Do<BenchmarkRuntimeSnapshotInput>(input =>
                     {
                         SnapshotInput = input;
                         SnapshotsCreated++;
                     }))
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
                new BenchmarkPhaseLaunchResolver(Profiles(profile),
                    Variants(variant),
                    Inspector(inspectedVariant ?? variant, probeSucceeded, supportsQuantizedKv, inspectionFails),
                    FallbackStore(optimizedConfigDisabled),
                    LaunchPolicy()),
                TimeProvider.System);
        }

        public Guid AgentId { get; }
        public IAgentDefinitionResolver Resolver { get; }
        public RecordingLeaseProvider LeaseProvider { get; }
        public BenchmarkStartRunCommand? Command { get; private set; }

        /// <summary>Every insert in order — a repeat group is several, and their ORDER is the contract.</summary>
        public List<BenchmarkStartRunCommand> Commands { get; } = [];

        public int SnapshotsCreated { get; private set; }
        public BenchmarkRuntimeSnapshotInput? SnapshotInput { get; private set; }

        public async Task<BenchmarkRunRecord> StartAsync(string? kvCacheType = null) =>
            (await _service.StartAsync(_project.Id, _primaryModel, _project.Version, kvCacheType).ConfigureAwait(false))[0];

        public Task<IReadOnlyList<BenchmarkRunRecord>> StartAsync(int repeatCount, bool warmup) =>
            _service.StartAsync(_project.Id, _primaryModel, _project.Version, kvCacheType: null, repeatCount, warmup);

        private static BenchmarkProjectRecord Project(Guid id,
            Guid agentId,
            bool judgeEnabled,
            string? judgeModel,
            int? maxOutputTokens,
            int? invocationTimeoutSeconds)
        {
            _ = judgeModel;
            return new BenchmarkProjectRecord(id, "Benchmark", JsonSerializer.SerializeToUtf8Bytes("exact task"), 4096, agentId,
                judgeEnabled, judgeEnabled ? Guid.NewGuid() : null, IsFrozen: false, 7, 1, 1, maxOutputTokens, invocationTimeoutSeconds);
        }

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
                input.ResolvedRuntime, input.PrimaryRuntime, input.PrimarySampling, input.PrimaryModel, input.Dependencies,
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

        private static ILlamaServerLaunchCapabilityInspector Inspector(GpuVariant variant,
            bool probeSucceeded,
            bool supportsQuantizedKv,
            bool inspectionFails)
        {
            if (inspectionFails)
            {
                var unavailable = Substitute.For<ILlamaServerLaunchCapabilityInspector>();
                unavailable.InspectAsync(Arg.Any<CancellationToken>())
                           .Returns<LlamaServerLaunchCapabilities>(_ => throw new LlamaRuntimeException("The llama.cpp runtime is not installed."));
                return unavailable;
            }

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
                BenchmarkPrimaryStatus.Queued, null, null, null, null, null, 0, null, null, 1, 1, null, null, 1);
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
