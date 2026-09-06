namespace XE_Local_AI_Engine.Tests.Inference;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Inference;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <see cref="InferenceProfileService" /> orchestration tests over substituted seams: explore persists only a concrete
///     parsed GPU draft (with a CPU-only context fallback) and rejects a non-local model without spawning; benchmark runs
///     the harness and persists the snapshot + repro-keyed row, marking the snapshot Succeeded/Failed; and the freeze gate
///     only freezes a benchmark-justified Explored profile, never throwing. The supervisor fake actually invokes the
///     passed profiling body so the explore/benchmark logic runs end to end. No DB, no process.
/// </summary>
public sealed class InferenceProfileServiceTests
{
    private const string Model = "bartowski/Model-GGUF:Q4_K_M";
    private const string MachineKey = "machine-1";
    private const string Build = "b9692";
    private const string ModelFilePath = "/models/model.gguf";

    // What llama.cpp turns --cpu-moe into: the ffn-experts pattern pinned to the CPU buffer type. llama-fit-params
    // fits against it and echoes it back as -ot, which is how the expert placement reaches a frozen profile.
    private const string ExpertOverride = @"\.ffn_(up|down|gate|gate_up)_(ch|)exps=CPU";

    [Test]
    public async Task Explore_PersistsDraftFromParsedFitOutput()
    {
        var fixture = new ServiceFixture();
        fixture.WithLocalModel();
        fixture.WithMetadata(new GgufModelMetadata(ParamCount: 7_000_000_000, QuantType: "15", ContextLength: 8192, ExpertCount: null, IsMoe: false));
        fixture.WithExploreFitParamsOutput("-c 8192 -ngl 33");
        fixture.EchoExploredUpsert();

        var result = await fixture.CreateService().ExploreAsync(Model, ModelRole.Chat, CancellationToken.None);

        AssertEx.True(result.Success);
        var profile = AssertEx.NotNull(result.Profile);
        AssertEx.Equal(8192, profile.CtxSize);
        AssertEx.Equal<int?>(33, profile.NGpuLayers);
        AssertEx.Equal("Explored", profile.Status);
        await fixture.ProfileStore.Received(1).CreateOrUpdateExploredAsync(
            Arg.Is<InferenceProfileInput>(input => input.CtxSize == 8192 && input.NGpuLayers == 33 && input.Backend == "cuda" && input.LlamacppBuild == Build),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Explore_WhenFitReturnsUnresolvedDefaults_ReturnsFailureWithoutPersistingPartialPlacement()
    {
        var fixture = new ServiceFixture();
        fixture.WithLocalModel();
        fixture.WithMetadata(new GgufModelMetadata(ParamCount: 7_000_000_000, QuantType: "15", ContextLength: 8192, ExpertCount: null, IsMoe: false));
        // Automatic GPU placement without authoritative full-offload startup evidence is not replayable.
        fixture.WithExploreFitParamsOutput(["load_tensors: offloaded 24/25 layers to GPU"], "-c 8192 -ngl -1");

        var result = await fixture.CreateService().ExploreAsync(Model, ModelRole.Chat, CancellationToken.None);

        AssertEx.False(result.Success);
        AssertEx.True(result.FailureReason!.Contains("concrete", StringComparison.OrdinalIgnoreCase));
        AssertEx.Null(result.Profile);
        await fixture.ProfileStore.DidNotReceive()
                     .CreateOrUpdateExploredAsync(Arg.Any<InferenceProfileInput>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Explore_WhenAutomaticLayersHaveFullOffloadEvidence_PersistsExplicitAllLayersReplay()
    {
        var fixture = new ServiceFixture();
        fixture.WithLocalModel();
        fixture.WithMetadata(new GgufModelMetadata(ParamCount: 7_000_000_000, QuantType: "15", ContextLength: 8192, ExpertCount: null, IsMoe: false));
        fixture.WithExploreFitParamsOutput(["load_tensors: offloaded 25/25 layers to GPU"], "-c 8192 -ngl -1");
        fixture.EchoExploredUpsert();

        var result = await fixture.CreateService().ExploreAsync(Model, ModelRole.Chat, CancellationToken.None);

        AssertEx.True(result.Success);
        var profile = AssertEx.NotNull(result.Profile);
        AssertEx.Equal<int?>(-2, profile.NGpuLayers);
        await fixture.ProfileStore.Received(1)
                     .CreateOrUpdateExploredAsync(Arg.Is<InferenceProfileInput>(input => input.CtxSize == 8192 && input.NGpuLayers == -2),
                         Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Explore_WhenOptimizedCandidateSucceeds_PersistsItsKvAndFlashAttentionPolicy()
    {
        var fixture = new ServiceFixture();
        fixture.WithLocalModel();
        fixture.WithMetadata(new GgufModelMetadata(ParamCount: 7_000_000_000, QuantType: "15", ContextLength: 8192, ExpertCount: null, IsMoe: false));
        fixture.WithExploreFitParamsOutput(startupOutput: [],
            successfulLaunchArguments: ["-fa", "on", "-ctk", "q8_0", "-ctv", "q8_0"],
            "-c 8192 -ngl 33");
        fixture.EchoExploredUpsert();

        var result = await fixture.CreateService().ExploreAsync(Model, ModelRole.Chat, CancellationToken.None);

        AssertEx.True(result.Success);
        var profile = AssertEx.NotNull(result.Profile);
        AssertEx.Equal("q8_0", profile.KvTypeK);
        AssertEx.Equal("q8_0", profile.KvTypeV);
        AssertEx.True(profile.FlashAttn);
    }

    [Test]
    public async Task Explore_WhenSafeCandidateSucceeds_PersistsKvAndFlashAttentionAsAbsent()
    {
        var fixture = new ServiceFixture();
        fixture.WithLocalModel();
        fixture.WithMetadata(new GgufModelMetadata(ParamCount: 7_000_000_000, QuantType: "15", ContextLength: 8192, ExpertCount: null, IsMoe: false));
        fixture.WithExploreFitParamsOutput(startupOutput: [],
            successfulLaunchArguments: ["--fit", "on", "-c", "8192"],
            "-c 8192 -ngl 33");
        fixture.EchoExploredUpsert();

        var result = await fixture.CreateService().ExploreAsync(Model, ModelRole.Chat, CancellationToken.None);

        AssertEx.True(result.Success);
        var profile = AssertEx.NotNull(result.Profile);
        AssertEx.Null(profile.KvTypeK);
        AssertEx.Null(profile.KvTypeV);
        AssertEx.False(profile.FlashAttn);
    }

    [Test]
    public async Task Explore_WhenExpertsStayInSystemRam_PersistsTheEquivalentOverrideTensor()
    {
        // D14, end to end: the spawn that decided expert offload carried --cpu-moe, the helper fitted against it and
        // echoed the equivalent tensor override, and THAT is what freezes. The frozen intent therefore replays the
        // placement the admission ledger booked, without a --cpu-moe the replay branch never sets.
        var fixture = new ServiceFixture();
        fixture.WithLocalModel();
        fixture.WithMetadata(new GgufModelMetadata(ParamCount: 30_000_000_000, QuantType: "15", ContextLength: 8192, ExpertCount: 128, IsMoe: true));
        fixture.WithExploreFitParamsOutput(startupOutput: [],
            successfulLaunchArguments: ["--fit", "on", "--cpu-moe"],
            $@"-c 8192 -ngl 33 -ot ""{ExpertOverride}""");
        fixture.EchoExploredUpsert();

        var result = await fixture.CreateService().ExploreAsync(Model, ModelRole.Chat, CancellationToken.None);

        AssertEx.True(result.Success);
        AssertEx.Equal(ExpertOverride, AssertEx.NotNull(result.Profile).OverrideTensor);
        await fixture.ProfileStore.Received(1).CreateOrUpdateExploredAsync(Arg.Is<InferenceProfileInput>(input => input.OverrideTensor == ExpertOverride && input.IsMoe),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Explore_WhenExpertOffloadYieldsNoOverrideTensor_PersistsNothing()
    {
        // The fit line cannot express the placement the spawn ran under. Freezing it would replay the experts back
        // onto the GPU, outside the reserved footprint, so the explore fails instead of saving a partial profile.
        var fixture = new ServiceFixture();
        fixture.WithLocalModel();
        fixture.WithMetadata(new GgufModelMetadata(ParamCount: 30_000_000_000, QuantType: "15", ContextLength: 8192, ExpertCount: 128, IsMoe: true));
        fixture.WithExploreFitParamsOutput(startupOutput: [],
            successfulLaunchArguments: ["--fit", "on", "--cpu-moe"],
            "-c 8192 -ngl 33");

        var result = await fixture.CreateService().ExploreAsync(Model, ModelRole.Chat, CancellationToken.None);

        AssertEx.False(result.Success);
        await fixture.ProfileStore.DidNotReceive()
                     .CreateOrUpdateExploredAsync(Arg.Any<InferenceProfileInput>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Benchmark_ForAnExpertOffloadProfile_ReplaysTheFrozenExpertPlacement()
    {
        var fixture = new ServiceFixture();
        var profile = ExploredRecord() with
        {
            OverrideTensor = ExpertOverride,
            IsMoe = true,
            ExpertCount = 128
        };
        fixture.WithProfiles(profile);
        fixture.WithLocalModel();
        fixture.WithRunningSnapshot(Guid.NewGuid());
        fixture.WithBenchmarkResult(SuccessMetrics());

        var result = await fixture.CreateService().BenchmarkAsync(profile.Id, CancellationToken.None);

        AssertEx.True(result.Success);
        await fixture.Supervisor.Received(1).RunExclusiveProfilingAsync(profile.ModelName,
            ModelRole.Chat,
            Arg.Is<ResolvedLaunchArguments>(args => !args.ExploreMode && args.OverrideTensor == ExpertOverride),
            enableMetrics: true,
            Arg.Any<Func<LlamaServerProfilingContext, CancellationToken, Task<InferenceBenchmarkMetrics>>>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<Func<CancellationToken, Task<LlamaServerProfilingVramSnapshot>>>());
    }

    [Test]
    public async Task Benchmark_ForALegacyExpertOffloadProfileWithNoOverrideTensor_IsStaleAndNeverSpawns()
    {
        // The legacy row an intermediate C5 build could freeze: fingerprint version 5, expert offload decided, but no
        // -ot recorded. Its replay would launch an MoE model fully resident, so the placement axis marks it Stale.
        var fixture = new ServiceFixture();
        var profile = ExploredRecord() with
        {
            OverrideTensor = null,
            IsMoe = true,
            ExpertCount = 128
        };
        fixture.WithProfiles(profile);
        fixture.WithLocalModel();
        fixture.InvalidationEvaluator.ContradictsCurrentPlacementAsync(profile, Arg.Any<CancellationToken>())
               .Returns(Task.FromResult(true));

        var result = await fixture.CreateService().BenchmarkAsync(profile.Id, CancellationToken.None);

        AssertEx.False(result.Success);
        AssertEx.NotNullOrEmpty(result.FailureReason);
        await fixture.ProfileStore.Received(1).MarkStaleAsync(profile.Id, Arg.Any<CancellationToken>());
        AssertEx.Empty(fixture.Supervisor.ReceivedCalls());
    }

    [Test]
    public async Task Benchmark_ForAResidentProfile_ReplaysWithNoOverrideTensor()
    {
        // The byte-identical pin: a dense/fully-resident profile's replay is untouched by the expert-offload rule.
        var fixture = new ServiceFixture();
        var profile = ExploredRecord();
        fixture.WithProfiles(profile);
        fixture.WithLocalModel();
        fixture.WithRunningSnapshot(Guid.NewGuid());
        fixture.WithBenchmarkResult(SuccessMetrics());

        var result = await fixture.CreateService().BenchmarkAsync(profile.Id, CancellationToken.None);

        AssertEx.True(result.Success);
        await fixture.Supervisor.Received(1).RunExclusiveProfilingAsync(profile.ModelName,
            ModelRole.Chat,
            Arg.Is<ResolvedLaunchArguments>(args => !args.ExploreMode
                                                    && args.OverrideTensor == null
                                                    && args.CtxSize == 8192
                                                    && args.NGpuLayers == 33),
            enableMetrics: true,
            Arg.Any<Func<LlamaServerProfilingContext, CancellationToken, Task<InferenceBenchmarkMetrics>>>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<Func<CancellationToken, Task<LlamaServerProfilingVramSnapshot>>>());
    }

    [Test]
    public async Task Explore_CloudOrMissingModel_Rejected_NoSpawn()
    {
        var fixture = new ServiceFixture();
        fixture.GgufModelStore.ResolveModelFilePathAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
               .Returns(Task.FromResult<string?>(null));

        var result = await fixture.CreateService().ExploreAsync(Model, ModelRole.Chat, CancellationToken.None);

        AssertEx.False(result.Success);
        AssertEx.NotNullOrEmpty(result.FailureReason);
        // Never spawned a profiling process.
        AssertEx.Empty(fixture.Supervisor.ReceivedCalls());
        await fixture.ProfileStore.DidNotReceive().CreateOrUpdateExploredAsync(Arg.Any<InferenceProfileInput>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Benchmark_RunsHarness_PersistsMetricsAndSnapshot()
    {
        var fixture = new ServiceFixture();
        var profile = ExploredRecord();
        fixture.WithProfiles(profile);
        fixture.WithLocalModel();
        var snapshotId = Guid.NewGuid();
        fixture.WithRunningSnapshot(snapshotId);
        var metrics = SuccessMetrics();
        fixture.HardwareProfiler.GetProfileAsync(forceRefresh: true, Arg.Any<CancellationToken>())
               .Returns(Task.FromResult(NvidiaProfile(availableVramBytes: 6000)));
        fixture.ProcessVramBudgetProbe.TryGetProcessBudgetBytesAsync(profile.Backend, Arg.Any<CancellationToken>())
               .Returns(Task.FromResult<long?>(8000));
        fixture.WithBenchmarkResult(metrics);

        var result = await fixture.CreateService().BenchmarkAsync(profile.Id, CancellationToken.None);

        AssertEx.True(result.Success);
        AssertEx.Equal<Guid?>(snapshotId, result.SnapshotId);
        AssertEx.Equal<double?>(42d, AssertEx.NotNull(result.Metrics).TokensPerSecond);
        AssertEx.Equal(new LlamaServerProfilingVramSnapshot(6000, 8000),
            AssertEx.NotNull(fixture.CapturedBenchmarkContext).PreSpawnVram);
        await fixture.Supervisor.Received(1).RunExclusiveProfilingAsync(profile.ModelName,
            ModelRole.Chat,
            Arg.Is<ResolvedLaunchArguments>(args => !args.ExploreMode
                                                    && args.CtxSize == profile.CtxSize
                                                    && args.NGpuLayers == profile.NGpuLayers),
            enableMetrics: true,
            Arg.Any<Func<LlamaServerProfilingContext, CancellationToken, Task<InferenceBenchmarkMetrics>>>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<Func<CancellationToken, Task<LlamaServerProfilingVramSnapshot>>>());
        await fixture.BenchmarkStore.Received(1).ReplaceForSnapshotAsync(snapshotId,
            Arg.Is<IReadOnlyList<ModelFitBenchmarkInput>>(rows => rows.Count == 1
                                                                  && Math.Abs((rows[0].TokensPerSecond ?? 0d) - 42d) < 0.0001
                                                                  && rows[0].Backend == "cuda"
                                                                  && rows[0].MachineKey == MachineKey
                                                                  && rows[0].CtxSize == 8192
                                                                  && rows[0].LlamacppBuild == Build
                                                                  && rows[0].DiagnosticsJson == """{"vram":{"globalFreeBytes":1000,"processBudgetBytes":1200}}"""),
            Arg.Any<CancellationToken>());
        await fixture.SnapshotStore.Received(1).MarkTerminalAsync(snapshotId,
            ModelFitRunStatus.Succeeded,
            Arg.Any<int?>(),
            Arg.Any<long?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<long>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Explore_WhenProfilingRefusedByLiveInference_ReturnsSkipped()
    {
        // The supervisor refuses the pre-spawn eviction while a role is serving. ExploreAsync has no try/catch of its
        // own, so a refusal implemented as a plain runtime throw would escape the service entirely.
        var fixture = new ServiceFixture();
        fixture.WithLocalModel();
        fixture.WithMetadata(new GgufModelMetadata(ParamCount: 7_000_000_000, QuantType: "15", ContextLength: 8192, ExpertCount: null, IsMoe: false));
        fixture.WithExploreProfilingRefusal(ModelRole.Chat, activeLeases: 2);

        var result = await fixture.CreateService().ExploreAsync(Model, ModelRole.Chat, CancellationToken.None);

        AssertEx.True(result.Skipped, "A refused explore is skipped, not failed.");
        AssertEx.False(result.Success);

        // The operator-facing sentence, pinned verbatim — the endpoint renders exactly this.
        AssertEx.Equal($"Skipped: {Model} (Chat) is serving 2 in-flight request(s); profiling did not run and nothing was evicted. Retry when the model is idle.",
            result.FailureReason);
        await fixture.ProfileStore.DidNotReceive().CreateOrUpdateExploredAsync(Arg.Any<InferenceProfileInput>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Benchmark_WhenProfilingRefusedByLiveInference_ReturnsSkipped_AndClosesTheSnapshot()
    {
        var fixture = new ServiceFixture();
        var profile = ExploredRecord();
        fixture.WithProfiles(profile);
        fixture.WithLocalModel();
        var snapshotId = Guid.NewGuid();
        fixture.WithRunningSnapshot(snapshotId);
        fixture.WithBenchmarkProfilingRefusal(ModelRole.Embedding, activeLeases: 1);

        var result = await fixture.CreateService().BenchmarkAsync(profile.Id, CancellationToken.None);

        AssertEx.True(result.Skipped, "A refused benchmark is skipped, not failed.");
        AssertEx.False(result.Success);
        AssertEx.Contains(result.FailureReason, "Embedding", StringComparison.Ordinal);
        // The snapshot was opened before the refusal, so it must not be left Running forever.
        await fixture.SnapshotStore.Received(1).MarkTerminalAsync(snapshotId,
            ModelFitRunStatus.Cancelled,
            Arg.Any<int?>(),
            Arg.Any<long?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<long>(),
            Arg.Any<CancellationToken>());
        await fixture.BenchmarkStore.DidNotReceive().ReplaceForSnapshotAsync(Arg.Any<Guid>(),
            Arg.Any<IReadOnlyList<ModelFitBenchmarkInput>>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Benchmark_WhenHarnessFails_MarksSnapshotFailed_NoFreeze()
    {
        var fixture = new ServiceFixture();
        var profile = ExploredRecord();
        fixture.WithProfiles(profile);
        fixture.WithLocalModel();
        var snapshotId = Guid.NewGuid();
        fixture.WithRunningSnapshot(snapshotId);
        fixture.WithBenchmarkResult(InferenceBenchmarkMetrics.Failed("harness boom"));

        var result = await fixture.CreateService().BenchmarkAsync(profile.Id, CancellationToken.None);

        AssertEx.False(result.Success);
        await fixture.SnapshotStore.Received(1).MarkTerminalAsync(snapshotId,
            ModelFitRunStatus.Failed,
            Arg.Any<int?>(),
            Arg.Any<long?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<long>(),
            Arg.Any<CancellationToken>());
        // A failed benchmark never freezes the profile.
        await fixture.ProfileStore.DidNotReceive().MarkFrozenAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<long?>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Benchmark_ExplicitPreSpawnPressureOverride_DisablesOnlyPreSpawnRejection()
    {
        var fixture = new ServiceFixture();
        var profile = ExploredRecord();
        fixture.WithProfiles(profile);
        fixture.WithLocalModel();
        fixture.WithRunningSnapshot(Guid.NewGuid());
        fixture.WithBenchmarkResult(SuccessMetrics());

        var result = await fixture.CreateService()
                                  .BenchmarkAsync(profile.Id,
                                      allowPreSpawnVramPressure: true,
                                      CancellationToken.None);

        AssertEx.True(result.Success);
        var spec = AssertEx.NotNull(fixture.CapturedBenchmarkSpec);
        AssertEx.False(spec.RejectPreSpawnVramPressure);
        AssertEx.Equal(512L * 1024 * 1024, spec.IncrementalVramDivergenceAbsoluteThresholdBytes);
        AssertEx.Equal(0.05d, spec.IncrementalVramDivergenceRatioThreshold);
    }

    [Test]
    public async Task Freeze_AfterSuccessfulBenchmark_TransitionsToFrozen()
    {
        var fixture = new ServiceFixture();
        var profile = ExploredRecord();
        fixture.WithProfiles(profile);
        fixture.WithLocalModel();
        var snapshotId = Guid.NewGuid();
        fixture.BenchmarkStore.GetLatestSuccessfulForProfileAsync(profile.Id, Arg.Any<CancellationToken>())
               .Returns(Task.FromResult<ModelFitBenchmarkRecord?>(BenchmarkRowFor(profile, snapshotId)));
        fixture.HardwareProfiler.GetProfileAsync(forceRefresh: true, Arg.Any<CancellationToken>())
               .Returns(Task.FromResult(NvidiaProfile(availableVramBytes: 2000)));
        fixture.ProcessVramBudgetProbe.TryGetProcessBudgetBytesAsync(profile.Backend, Arg.Any<CancellationToken>())
               .Returns(Task.FromResult<long?>(2400));
        fixture.ProfileStore.MarkFrozenAsync(profile.Id, snapshotId, 2000, 2400, Arg.Any<CancellationToken>())
               .Returns(Task.FromResult<InferenceProfileRecord?>(profile with
               {
                   Status = InferenceProfileStatus.Frozen,
                   BenchmarkSnapshotId = snapshotId,
                   GlobalFreeVramAtFreezeBytes = 2000
               }));

        var result = await fixture.CreateService().FreezeAsync(profile.Id, CancellationToken.None);

        AssertEx.True(result.Success);
        var view = AssertEx.NotNull(result.Profile);
        AssertEx.Equal("Frozen", view.Status);
        AssertEx.Equal<Guid?>(snapshotId, view.BenchmarkSnapshotId);
        await fixture.ProfileStore.Received(1).MarkFrozenAsync(profile.Id, snapshotId, 2000, 2400, Arg.Any<CancellationToken>());
        await fixture.ProcessVramBudgetProbe.Received(1)
                     .TryGetProcessBudgetBytesAsync(profile.Backend, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Freeze_CpuProfile_DoesNotCaptureUnrelatedGpuVram()
    {
        var fixture = new ServiceFixture();
        var profile = ExploredRecord() with
        {
            Backend = InferenceBackends.Cpu
        };
        fixture.WithProfiles(profile);
        fixture.WithLocalModel();
        var snapshotId = Guid.NewGuid();
        fixture.BenchmarkStore.GetLatestSuccessfulForProfileAsync(profile.Id, Arg.Any<CancellationToken>())
               .Returns(Task.FromResult<ModelFitBenchmarkRecord?>(BenchmarkRowFor(profile, snapshotId)));
        fixture.HardwareProfiler.GetProfileAsync(forceRefresh: true, Arg.Any<CancellationToken>())
               .Returns(Task.FromResult(NvidiaProfile(availableVramBytes: 2000)));
        fixture.ProfileStore.MarkFrozenAsync(profile.Id, snapshotId, globalFreeVramAtFreezeBytes: null, processBudgetVramAtFreezeBytes: null, Arg.Any<CancellationToken>())
               .Returns(Task.FromResult<InferenceProfileRecord?>(profile with
               {
                   Status = InferenceProfileStatus.Frozen,
                   BenchmarkSnapshotId = snapshotId
               }));

        var result = await fixture.CreateService().FreezeAsync(profile.Id, CancellationToken.None);

        AssertEx.True(result.Success);
        await fixture.ProfileStore.Received(1)
                     .MarkFrozenAsync(profile.Id, snapshotId, globalFreeVramAtFreezeBytes: null, processBudgetVramAtFreezeBytes: null, Arg.Any<CancellationToken>());
        await fixture.ProcessVramBudgetProbe.DidNotReceive()
                     .TryGetProcessBudgetBytesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Freeze_WithoutSuccessfulBenchmark_ReturnsFailed_StaysExplored()
    {
        var fixture = new ServiceFixture();
        var profile = ExploredRecord();
        fixture.WithProfiles(profile);
        fixture.WithLocalModel();
        fixture.BenchmarkStore.GetLatestSuccessfulForProfileAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
               .Returns(Task.FromResult<ModelFitBenchmarkRecord?>(null));

        var result = await fixture.CreateService().FreezeAsync(profile.Id, CancellationToken.None);

        AssertEx.False(result.Success);
        AssertEx.NotNullOrEmpty(result.FailureReason);
        // The Explored profile is never transitioned.
        await fixture.ProfileStore.DidNotReceive().MarkFrozenAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<long?>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Freeze_WhenBenchmarkArgsChangedByReExplore_ReturnsFailed_NoFreeze()
    {
        var fixture = new ServiceFixture();
        var profile = ExploredRecord(); // current ctx = 8192
        fixture.WithProfiles(profile);
        fixture.WithLocalModel();
        var snapshotId = Guid.NewGuid();
        // The profile's latest successful benchmark was taken at a different ctx (a prior explore); a re-explore then
        // overwrote the profile's args in place (same id, new args), so the benchmark no longer matches the current
        // configuration and must NOT justify a freeze.
        var staleBenchmark = BenchmarkRowFor(profile, snapshotId) with
        {
            CtxSize = 4096
        };
        fixture.BenchmarkStore.GetLatestSuccessfulForProfileAsync(profile.Id, Arg.Any<CancellationToken>())
               .Returns(Task.FromResult<ModelFitBenchmarkRecord?>(staleBenchmark));

        var result = await fixture.CreateService().FreezeAsync(profile.Id, CancellationToken.None);

        AssertEx.False(result.Success);
        AssertEx.NotNullOrEmpty(result.FailureReason);
        await fixture.ProfileStore.DidNotReceive().MarkFrozenAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<long?>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Benchmark_GpuProfileWithoutMachineReadablePlacement_IsRejectedBeforeSpawn()
    {
        var fixture = new ServiceFixture();
        fixture.WithLocalModel();
        var profile = ExploredRecord() with
        {
            NGpuLayers = null
        };
        fixture.WithProfiles(profile);

        var result = await fixture.CreateService().BenchmarkAsync(profile.Id, CancellationToken.None);

        AssertEx.False(result.Success);
        AssertEx.True(result.FailureReason!.Contains("machine-readable GPU placement", StringComparison.Ordinal));
        await fixture.Supervisor.DidNotReceiveWithAnyArgs()
                     .RunExclusiveProfilingAsync(default!, default, default!, default,
                         default(Func<LlamaServerProfilingContext, CancellationToken, Task<InferenceBenchmarkMetrics>>)!,
                         default);
    }

    [Test]
    public async Task Freeze_GpuProfileWithoutMachineReadablePlacement_IsRejectedBeforeBenchmarkLookup()
    {
        var fixture = new ServiceFixture();
        var profile = ExploredRecord() with
        {
            NGpuLayers = null
        };
        fixture.WithProfiles(profile);

        var result = await fixture.CreateService().FreezeAsync(profile.Id, CancellationToken.None);

        AssertEx.False(result.Success);
        await fixture.BenchmarkStore.DidNotReceive()
                     .GetLatestSuccessfulForProfileAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await fixture.ProfileStore.DidNotReceive()
                     .MarkFrozenAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<long?>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
    }

    private static InferenceProfileRecord ExploredRecord()
    {
        return new InferenceProfileRecord(Id: Guid.NewGuid(),
            MachineKey: MachineKey,
            ModelName: Model,
            Role: (int)ModelRole.Chat,
            Backend: "cuda",
            LlamacppBuild: Build,
            Quant: "Q4_K_M",
            CtxSize: 8192,
            NGpuLayers: 33,
            TensorSplit: null,
            OverrideTensor: null,
            KvTypeK: null,
            KvTypeV: null,
            FlashAttn: false,
            NParams: 7_000_000_000,
            IsMoe: false,
            ExpertCount: null,
            GlobalFreeVramAtFreezeBytes: null,
            Status: InferenceProfileStatus.Explored,
            BenchmarkSnapshotId: null,
            CreatedAtUtc: 0,
            UpdatedAtUtc: 0,
            LaunchPolicyFingerprintVersion: LaunchPolicyFingerprintProvider.CurrentVersion,
            LaunchPolicyFingerprint: "fingerprint");
    }

    private static InferenceBenchmarkMetrics SuccessMetrics()
    {
        return new InferenceBenchmarkMetrics(Success: true,
            FailureReason: null,
            TokensPerSecond: 42d,
            PpTokensPerSecond: 100d,
            TtftMs: 50d,
            TotalLatencyMs: 500d,
            CacheHitRate: 0.8d,
            ToolLoopMs: 30d,
            VramLoadBytes: 1000,
            VramAfterBytes: 900,
            Runs: 1,
            RawJson: "raw-metrics",
            DiagnosticsJson: """{"vram":{"globalFreeBytes":1000,"processBudgetBytes":1200}}""");
    }

    private static HardwareProfile NvidiaProfile(long? availableVramBytes)
    {
        return new HardwareProfile
        {
            TotalRamBytes = 64L * 1024 * 1024 * 1024,
            AvailableRamBytes = 48L * 1024 * 1024 * 1024,
            VramBytes = 24L * 1024 * 1024 * 1024,
            AvailableVramBytes = availableVramBytes,
            VramKnown = true,
            GpuVendor = GpuVendor.Nvidia,
            GpuAccelAvailable = true,
            CpuCores = 16,
            FreeDiskBytes = 500L * 1024 * 1024 * 1024
        };
    }

    // A benchmark row whose recorded launch args match the profile exactly, bound to the profile's id — the shape the
    // profile-scoped store read returns for a benchmark that justifies a freeze.
    private static ModelFitBenchmarkRecord BenchmarkRowFor(InferenceProfileRecord profile, Guid snapshotId)
    {
        return new ModelFitBenchmarkRecord(Id: Guid.NewGuid(),
            SnapshotId: snapshotId,
            ModelName: profile.ModelName,
            ProviderName: "llamacpp",
            TokensPerSecond: 42d,
            TtftMs: 50d,
            TotalLatencyMs: 500d,
            Runs: 1,
            RawJson: null,
            DiagnosticsJson: null,
            LlamacppBuild: profile.LlamacppBuild,
            Quant: profile.Quant,
            CtxSize: profile.CtxSize,
            KvType: profile.KvTypeK,
            Backend: profile.Backend,
            MachineKey: profile.MachineKey,
            NGpuLayers: profile.NGpuLayers,
            TensorSplit: profile.TensorSplit,
            OverrideTensor: profile.OverrideTensor,
            KvTypeV: profile.KvTypeV,
            FlashAttn: profile.FlashAttn,
            ProfileId: profile.Id,
            LaunchPolicyFingerprintVersion: profile.LaunchPolicyFingerprintVersion,
            LaunchPolicyFingerprint: profile.LaunchPolicyFingerprint);
    }

    // Wires the substituted seams the orchestrator composes, with safe defaults, and exposes the doubles the tests assert on.
    private sealed class ServiceFixture
    {
        private static ModelFitSnapshotSummaryRecord Summary(Guid id, ModelFitRunStatus status)
        {
            return new ModelFitSnapshotSummaryRecord(Id: id,
                ApprovedImageId: Model,
                Operation: ModelFitOperation.Benchmark,
                UseCase: null,
                ProviderName: "llamacpp",
                ModelName: Model,
                Status: status,
                StartedAtUtc: 0,
                CompletedAtUtc: 1,
                DurationMs: 1,
                ExitCode: 0,
                IsLatestSuccessful: status == ModelFitRunStatus.Succeeded,
                CreatedByRunId: null,
                CreatedAtUtc: 0);
        }

        public ILlamaServerProcessSupervisor Supervisor { get; } = Substitute.For<ILlamaServerProcessSupervisor>();

        public IInferenceProfileStore ProfileStore { get; } = Substitute.For<IInferenceProfileStore>();

        public IModelFitSnapshotStore SnapshotStore { get; } = Substitute.For<IModelFitSnapshotStore>();

        public IModelFitBenchmarkStore BenchmarkStore { get; } = Substitute.For<IModelFitBenchmarkStore>();

        public IInferenceBenchmarkHarness Harness { get; } = Substitute.For<IInferenceBenchmarkHarness>();

        public IGgufModelStore GgufModelStore { get; } = Substitute.For<IGgufModelStore>();

        public IGgufMetadataReader MetadataReader { get; } = Substitute.For<IGgufMetadataReader>();

        public IMachineKeyProvider MachineKeyProvider { get; } = Substitute.For<IMachineKeyProvider>();

        public IGpuVariantSelector VariantSelector { get; } = Substitute.For<IGpuVariantSelector>();

        public IInstalledRuntimeStore RuntimeStore { get; } = Substitute.For<IInstalledRuntimeStore>();

        public IHardwareProfiler HardwareProfiler { get; } = Substitute.For<IHardwareProfiler>();

        public IProcessVramBudgetProbe ProcessVramBudgetProbe { get; } = Substitute.For<IProcessVramBudgetProbe>();

        public ILaunchPolicyFingerprintProvider LaunchPolicyFingerprintProvider { get; } =
            Substitute.For<ILaunchPolicyFingerprintProvider>();

        public IInferenceInvalidationEvaluator InvalidationEvaluator { get; } = Substitute.For<IInferenceInvalidationEvaluator>();

        public LlamaServerProfilingContext? CapturedBenchmarkContext { get; private set; }

        public InferenceBenchmarkSpec? CapturedBenchmarkSpec { get; private set; }

        public ServiceFixture()
        {
            MachineKeyProvider.GetMachineKeyAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(MachineKey));
            VariantSelector.SelectVariantAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(GpuVariant.Cuda));
            RuntimeStore.ReadAsync(Arg.Any<CancellationToken>())
                        .Returns(Task.FromResult<InstalledRuntimeState?>(new InstalledRuntimeState(Build, "asset.zip", "sha", GpuVariant.Cuda, DateTimeOffset.UnixEpoch)));
            HardwareProfiler.GetProfileAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
                            .Returns(Task.FromResult(NvidiaProfile(availableVramBytes: null)));
            ProfileStore.ListAsync(Arg.Any<CancellationToken>())
                        .Returns(Task.FromResult<IReadOnlyList<InferenceProfileRecord>>([]));
            LaunchPolicyFingerprintProvider.CaptureAsync(Arg.Any<InferenceProfileFingerprintInput>(), Arg.Any<CancellationToken>())
                                           .Returns(Task.FromResult(new LaunchPolicyFingerprint(Client.Services.Inference.LaunchPolicyFingerprintProvider.CurrentVersion,
                                               "fingerprint")));
            LaunchPolicyFingerprintProvider.CaptureAsync(Arg.Any<InferenceProfileRecord>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                                           .Returns(Task.FromResult(new LaunchPolicyFingerprint(Client.Services.Inference.LaunchPolicyFingerprintProvider.CurrentVersion,
                                               "fingerprint")));
            LaunchPolicyFingerprintProvider.MatchesAsync(Arg.Any<InferenceProfileRecord>(),
                                               Arg.Any<string>(),
                                               Arg.Any<CancellationToken>())
                                           .Returns(Task.FromResult(true));
        }

        public void WithLocalModel()
        {
            GgufModelStore.ResolveModelFilePathAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                          .Returns(Task.FromResult<string?>(ModelFilePath));
        }

        public void WithMetadata(GgufModelMetadata metadata)
        {
            MetadataReader.ReadMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(metadata));
        }

        public void WithProfiles(params InferenceProfileRecord[] profiles)
        {
            ProfileStore.ListAsync(Arg.Any<CancellationToken>())
                        .Returns(Task.FromResult<IReadOnlyList<InferenceProfileRecord>>(profiles));
        }

        public void WithRunningSnapshot(Guid snapshotId)
        {
            SnapshotStore.CreateRunningAsync(Arg.Any<ModelFitSnapshotInput>(), Arg.Any<CancellationToken>())
                         .Returns(Task.FromResult(Summary(snapshotId, ModelFitRunStatus.Running)));
        }

        // Drives the explore profiling body with machine-readable helper stdout so the real fit parser runs.
        public void WithExploreFitParamsOutput(params string[] fitParamsOutput)
        {
            WithExploreFitParamsOutput([], fitParamsOutput);
        }

        public void WithExploreFitParamsOutput(IReadOnlyList<string> startupOutput, params string[] fitParamsOutput)
        {
            WithExploreFitParamsOutput(startupOutput, successfulLaunchArguments: [], fitParamsOutput);
        }

        public void WithExploreFitParamsOutput(IReadOnlyList<string> startupOutput,
            IReadOnlyList<string> successfulLaunchArguments,
            params string[] fitParamsOutput)
        {
            var context = new LlamaServerProfilingContext(new LlamaServerEndpoint(Model, ModelRole.Chat, new Uri("http://127.0.0.1:18100/v1")),
                startupOutput,
                FitParamsOutput: fitParamsOutput)
            {
                SuccessfulLaunchArguments = successfulLaunchArguments
            };
            Supervisor.RunExclusiveProfilingAsync(Arg.Any<string>(),
                          Arg.Any<ModelRole>(),
                          Arg.Any<ResolvedLaunchArguments>(),
                          Arg.Any<bool>(),
                          Arg.Any<Func<LlamaServerProfilingContext, CancellationToken, Task<ResolvedLaunchArguments?>>>(),
                          Arg.Any<CancellationToken>())
                      .Returns(callInfo =>
                      {
                          var body = callInfo.Arg<Func<LlamaServerProfilingContext, CancellationToken, Task<ResolvedLaunchArguments?>>>();
                          return body(context, CancellationToken.None);
                      });
        }

        // Drives the benchmark profiling body: invokes it (running the substituted harness) and returns its metrics.
        public void WithBenchmarkResult(InferenceBenchmarkMetrics metrics)
        {
            Harness.RunAsync(Arg.Any<LlamaServerProfilingContext>(), Arg.Any<InferenceBenchmarkSpec>(), Arg.Any<CancellationToken>())
                   .Returns(callInfo =>
                   {
                       CapturedBenchmarkContext = callInfo.Arg<LlamaServerProfilingContext>();
                       CapturedBenchmarkSpec = callInfo.Arg<InferenceBenchmarkSpec>();
                       return Task.FromResult(metrics);
                   });

            Supervisor.RunExclusiveProfilingAsync(Arg.Any<string>(),
                          Arg.Any<ModelRole>(),
                          Arg.Any<ResolvedLaunchArguments>(),
                          Arg.Any<bool>(),
                          Arg.Any<Func<LlamaServerProfilingContext, CancellationToken, Task<InferenceBenchmarkMetrics>>>(),
                          Arg.Any<CancellationToken>(),
                          Arg.Any<Func<CancellationToken, Task<LlamaServerProfilingVramSnapshot>>>())
                      .Returns(async callInfo =>
                      {
                          var captureVram =
                              callInfo.Arg<Func<CancellationToken, Task<LlamaServerProfilingVramSnapshot>>>();
                          var preSpawnVram = await captureVram(CancellationToken.None);
                          var context = new LlamaServerProfilingContext(new LlamaServerEndpoint(Model, ModelRole.Chat, new Uri("http://127.0.0.1:18100/v1")),
                              [])
                          {
                              PreSpawnVram = preSpawnVram
                          };
                          var body = callInfo.Arg<Func<LlamaServerProfilingContext, CancellationToken, Task<InferenceBenchmarkMetrics>>>();
                          return await body(context, CancellationToken.None);
                      });
        }

        // The exclusive profiling spawn refuses because a warm role is serving in-flight inference.
        public void WithExploreProfilingRefusal(ModelRole busyRole, int activeLeases)
        {
            Supervisor.RunExclusiveProfilingAsync(Arg.Any<string>(),
                          Arg.Any<ModelRole>(),
                          Arg.Any<ResolvedLaunchArguments>(),
                          Arg.Any<bool>(),
                          Arg.Any<Func<LlamaServerProfilingContext, CancellationToken, Task<ResolvedLaunchArguments?>>>(),
                          Arg.Any<CancellationToken>())
                      .Returns<Task<ResolvedLaunchArguments?>>(_ => throw new LlamaServerProfilingRefusedException(Model, busyRole, activeLeases, LlamaServerProfilingRefusalReason.InUse));
        }

        public void WithBenchmarkProfilingRefusal(ModelRole busyRole, int activeLeases)
        {
            Supervisor.RunExclusiveProfilingAsync(Arg.Any<string>(),
                          Arg.Any<ModelRole>(),
                          Arg.Any<ResolvedLaunchArguments>(),
                          Arg.Any<bool>(),
                          Arg.Any<Func<LlamaServerProfilingContext, CancellationToken, Task<InferenceBenchmarkMetrics>>>(),
                          Arg.Any<CancellationToken>(),
                          Arg.Any<Func<CancellationToken, Task<LlamaServerProfilingVramSnapshot>>>())
                      .Returns<Task<InferenceBenchmarkMetrics>>(_ => throw new LlamaServerProfilingRefusedException(Model, busyRole, activeLeases, LlamaServerProfilingRefusalReason.InUse));
        }

        public void EchoExploredUpsert()
        {
            ProfileStore.CreateOrUpdateExploredAsync(Arg.Any<InferenceProfileInput>(), Arg.Any<CancellationToken>())
                        .Returns(callInfo => Task.FromResult(RecordFromInput(callInfo.Arg<InferenceProfileInput>())));
        }

        public InferenceProfileService CreateService()
        {
            return new InferenceProfileService(Supervisor,
                ProfileStore,
                SnapshotStore,
                BenchmarkStore,
                Harness,
                new FittedArgsParser(),
                GgufModelStore,
                MetadataReader,
                MachineKeyProvider,
                VariantSelector,
                RuntimeStore,
                HardwareProfiler,
                ProcessVramBudgetProbe,
                LaunchPolicyFingerprintProvider,
                InvalidationEvaluator,
                Options.Create(new InferenceBenchmarkVramAdmissionOptions()),
                NullLogger<InferenceProfileService>.Instance);
        }

        private static InferenceProfileRecord RecordFromInput(InferenceProfileInput input)
        {
            return new InferenceProfileRecord(Id: Guid.NewGuid(),
                input.MachineKey,
                input.ModelName,
                input.Role,
                input.Backend,
                input.LlamacppBuild,
                input.Quant,
                input.CtxSize,
                input.NGpuLayers,
                input.TensorSplit,
                input.OverrideTensor,
                input.KvTypeK,
                input.KvTypeV,
                input.FlashAttn,
                input.NParams,
                input.IsMoe,
                input.ExpertCount,
                Status: InferenceProfileStatus.Explored,
                BenchmarkSnapshotId: null,
                CreatedAtUtc: 0,
                UpdatedAtUtc: 0,
                input.LaunchPolicyFingerprintVersion,
                input.LaunchPolicyFingerprint,
                GlobalFreeVramAtFreezeBytes: null);
        }
    }
}
