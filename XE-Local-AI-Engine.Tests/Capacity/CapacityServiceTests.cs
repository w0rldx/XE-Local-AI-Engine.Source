namespace XE_Local_AI_Engine.Tests.Capacity;

using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.Ollama.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <see cref="CapacityService" /> tests: the single admission gate. Cloud short-circuits without a probe; a local
///     model already running queues; a fitting local model with process headroom is admitted (and reserves its
///     footprint); the process cap and byte budget each reject independently; an unknown footprint rejects; CPU mode
///     does not double-count resident models; concurrent different-model spawns cannot both pass (TOCTOU); and a
///     non-fitting Ollama model rejects with the eviction warning flagged; GPU mode fits against measured FREE VRAM
///     (not total) and falls back to total when free is unmeasurable; the decision reads a fresh (forceRefresh) profile.
///     Every probe is mocked — no Ollama/network.
/// </summary>
public sealed class CapacityServiceTests
{
    private const long Gb = 1024L * 1024 * 1024;
    private const string Model = "bartowski/Model-GGUF:Q4_K_M";
    private const string Llamacpp = LlamaServerProviderConstants.ProviderName;

    [Test]
    public async Task Capacity_WhenCloudModel_ReturnsAllow_WithoutProbe()
    {
        var harness = new Harness
        {
            CloudSelected = true
        };
        var service = harness.Build();

        var decision = await service.DecideAsync(Model, ModelRole.Chat, CancellationToken.None);

        AssertEx.Equal(CapacityVerdict.Allow, decision.Verdict);
        AssertEx.False(decision.OllamaEvictionWarning);
        AssertEx.Null(decision.Reservation);
        // No hardware/supervisor/footprint probe may run on the cloud path.
        await harness.HardwareProfiler.DidNotReceive().GetProfileAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await harness.Supervisor.DidNotReceive().CheckHealthAsync(Arg.Any<CancellationToken>());
        await harness.FootprintProvider.DidNotReceive()
                     .ResolveFootprintAsync(Arg.Any<string>(), Arg.Any<HardwareProfile>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Capacity_WhenLocalFitsAndProcessHeadroom_ReturnsAllow()
    {
        var harness = new Harness
        {
            Profile = GpuProfile(64 * Gb),
            Footprint = ModelFootprint.Known(4 * Gb)
        };
        var service = harness.Build();

        var decision = await service.DecideAsync(Model, ModelRole.Chat, CancellationToken.None);

        AssertEx.Equal(CapacityVerdict.Allow, decision.Verdict);
        AssertEx.NotNull(decision.Reservation);
        // The footprint was reserved in the ledger so a concurrent decision sees the in-flight load.
        AssertEx.Equal(4 * Gb, harness.Ledger.ReservedBytes);
    }

    [Test]
    public async Task Capacity_WhenProcessCapReached_ReturnsReject_EvenIfBytesFit()
    {
        // Two distinct processes already loaded, cap = 2 → adding a third exceeds the cap even though the bytes fit.
        var harness = new Harness
        {
            Profile = GpuProfile(64 * Gb),
            Footprint = ModelFootprint.Known(1 * Gb),
            MaxLoadedProcesses = 2,
            RunningLlama =
            [
                new LlamaServerProcessHealth("running/a:Q4_K_M", ModelRole.Chat, IsResponsive: true, "ok"),
                new LlamaServerProcessHealth("running/b:Q4_K_M", ModelRole.Chat, IsResponsive: true, "ok")
            ]
        };
        var service = harness.Build();

        var decision = await service.DecideAsync(Model, ModelRole.Chat, CancellationToken.None);

        AssertEx.Equal(CapacityVerdict.RejectInsufficient, decision.Verdict);
        AssertEx.Equal(0, harness.Ledger.ReservedBytes);
    }

    [Test]
    public async Task Capacity_WhenLocalSameAsRunning_ReturnsQueueSameModel()
    {
        var harness = new Harness
        {
            Profile = GpuProfile(64 * Gb),
            RunningLlama = [new LlamaServerProcessHealth(Model, ModelRole.Chat, IsResponsive: true, "ok")]
        };
        var service = harness.Build();

        var decision = await service.DecideAsync(Model, ModelRole.Chat, CancellationToken.None);

        AssertEx.Equal(CapacityVerdict.QueueSameModel, decision.Verdict);
        // No fit math runs on the same-model path.
        await harness.FootprintProvider.DidNotReceive()
                     .ResolveFootprintAsync(Arg.Any<string>(), Arg.Any<HardwareProfile>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Capacity_WhenLocalDifferentNoFit_ReturnsRejectInsufficient()
    {
        var harness = new Harness
        {
            Profile = GpuProfile(4 * Gb),
            Footprint = ModelFootprint.Known(40 * Gb)
        };
        var service = harness.Build();

        var decision = await service.DecideAsync(Model, ModelRole.Chat, CancellationToken.None);

        AssertEx.Equal(CapacityVerdict.RejectInsufficient, decision.Verdict);
        AssertEx.True(decision.Reason.Length > 0);
        // Sanitized reason carries no model identity, path, or byte figure.
        AssertEx.False(decision.Reason.Contains(Model, StringComparison.Ordinal));
    }

    [Test]
    public async Task Capacity_WhenFootprintUnknown_ReturnsReject()
    {
        var harness = new Harness
        {
            Profile = GpuProfile(64 * Gb),
            Footprint = ModelFootprint.Unknown
        };
        var service = harness.Build();

        var decision = await service.DecideAsync(Model, ModelRole.Chat, CancellationToken.None);

        AssertEx.Equal(CapacityVerdict.RejectInsufficient, decision.Verdict);
    }

    [Test]
    public async Task Capacity_WhenVramUnknown_UsesRamBudget()
    {
        // VRAM unknown → the byte budget is AvailableRamBytes; a footprint under it is admitted.
        var harness = new Harness
        {
            Profile = CpuProfile(availableRam: 24 * Gb),
            Footprint = ModelFootprint.Known(8 * Gb)
        };
        var service = harness.Build();

        var decision = await service.DecideAsync(Model, ModelRole.Chat, CancellationToken.None);

        AssertEx.Equal(CapacityVerdict.Allow, decision.Verdict);
    }

    [Test]
    public async Task Capacity_CpuMode_DoesNotDoubleCountResidentModels()
    {
        // CPU mode: AvailableRamBytes already nets out the resident running model, so a footprint that fits the
        // available RAM is admitted even though a large model is "running" — its bytes must NOT be subtracted again.
        var harness = new Harness
        {
            Profile = CpuProfile(availableRam: 10 * Gb),
            Footprint = ModelFootprint.Known(8 * Gb),
            MaxLoadedProcesses = 5,
            RunningLlama = [new LlamaServerProcessHealth("resident/big:Q4_K_M", ModelRole.Chat, IsResponsive: true, "ok")]
        };
        var service = harness.Build();

        var decision = await service.DecideAsync(Model, ModelRole.Chat, CancellationToken.None);

        AssertEx.Equal(CapacityVerdict.Allow, decision.Verdict);
    }

    [Test]
    public async Task Capacity_GpuMode_UsesFreeVramBaseline_NotTotalVram()
    {
        // The bug: a GPU whose TOTAL VRAM fits the model but whose FREE VRAM does not (residents outside the ledger —
        // the main chat model / warm sub-agents — hold the rest). Total-based math would over-admit; the free baseline
        // must reject. Total = 64 GB, free = 4 GB, footprint = 8 GB → reject.
        var harness = new Harness
        {
            Profile = GpuProfileWithFreeVram(64 * Gb, availableVramBytes: 4 * Gb),
            Footprint = ModelFootprint.Known(8 * Gb)
        };
        var service = harness.Build();

        var decision = await service.DecideAsync(Model, ModelRole.Chat, CancellationToken.None);

        AssertEx.Equal(CapacityVerdict.RejectInsufficient, decision.Verdict);
        AssertEx.Equal(0, harness.Ledger.ReservedBytes);
    }

    [Test]
    public async Task Capacity_GpuMode_WhenFreeVramFits_Admits()
    {
        // Free baseline comfortably fits the footprint → admit and reserve. Total is larger but irrelevant now.
        var harness = new Harness
        {
            Profile = GpuProfileWithFreeVram(64 * Gb, availableVramBytes: 20 * Gb),
            Footprint = ModelFootprint.Known(8 * Gb)
        };
        var service = harness.Build();

        var decision = await service.DecideAsync(Model, ModelRole.Chat, CancellationToken.None);

        AssertEx.Equal(CapacityVerdict.Allow, decision.Verdict);
        AssertEx.Equal(8 * Gb, harness.Ledger.ReservedBytes);
    }

    [Test]
    public async Task Capacity_GpuMode_WhenFreeVramUnknown_FallsBackToTotalVram()
    {
        // Free-VRAM probe unavailable (AvailableVramBytes null) → degraded fallback uses total VRAM minus the ledger.
        // Total = 64 GB, footprint = 8 GB → admit (the documented over-admission risk; process cap is the backstop).
        var harness = new Harness
        {
            Profile = GpuProfileWithFreeVram(64 * Gb, availableVramBytes: null),
            Footprint = ModelFootprint.Known(8 * Gb)
        };
        var service = harness.Build();

        var decision = await service.DecideAsync(Model, ModelRole.Chat, CancellationToken.None);

        AssertEx.Equal(CapacityVerdict.Allow, decision.Verdict);
    }

    [Test]
    public async Task Capacity_LocalDecision_ReadsFreshProfile_NotBootCache()
    {
        // A capacity decision must re-probe live VRAM/RAM (forceRefresh:true) — a boot-time cached free-VRAM figure
        // would defeat the resident-model accounting.
        var harness = new Harness
        {
            Profile = GpuProfile(64 * Gb),
            Footprint = ModelFootprint.Known(4 * Gb)
        };
        var service = harness.Build();

        await service.DecideAsync(Model, ModelRole.Chat, CancellationToken.None);

        await harness.HardwareProfiler.Received().GetProfileAsync(forceRefresh: true, Arg.Any<CancellationToken>());
        await harness.HardwareProfiler.DidNotReceive().GetProfileAsync(forceRefresh: false, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Capacity_WhenTwoDifferentModelsRaceTheGate_DoesNotDoubleAdmit()
    {
        // Budget = 10 GB, each model = 6 GB → at most one of two concurrent different-model spawns can be admitted; the
        // ledger reservation from the first must make the second see only 4 GB free.
        var harness = new Harness
        {
            Profile = GpuProfile(10 * Gb),
            Footprint = ModelFootprint.Known(6 * Gb),
            MaxLoadedProcesses = 5
        };
        var service = harness.Build();

        var first = service.DecideAsync("model/a:Q4_K_M", ModelRole.Chat, CancellationToken.None);
        var second = service.DecideAsync("model/b:Q4_K_M", ModelRole.Chat, CancellationToken.None);
        var decisions = await Task.WhenAll(first, second);

        var allowed = decisions.Count(d => d.Verdict == CapacityVerdict.Allow);
        var rejected = decisions.Count(d => d.Verdict == CapacityVerdict.RejectInsufficient);
        AssertEx.Equal(1, allowed);
        AssertEx.Equal(1, rejected);
        AssertEx.Equal(6 * Gb, harness.Ledger.ReservedBytes);
    }

    [Test]
    public async Task Capacity_WhenOllamaDifferentModelNoFit_RejectsAndFlagsWarning()
    {
        var harness = new Harness
        {
            ProviderName = OllamaLocalModelProvider.OllamaProviderName,
            Profile = GpuProfile(4 * Gb),
            Footprint = ModelFootprint.Known(40 * Gb),
            RunningOllama =
            [
                new RunningModelSnapshot("other-model", "other-model", ExpiresAt: null, SizeBytes: 3 * Gb, SizeVramBytes: 3 * Gb)
            ]
        };
        var service = harness.Build();

        var decision = await service.DecideAsync(Model, ModelRole.Chat, CancellationToken.None);

        AssertEx.Equal(CapacityVerdict.RejectInsufficient, decision.Verdict);
        AssertEx.True(decision.OllamaEvictionWarning, "a different running Ollama model that won't fit must flag the eviction warning.");
    }

    // An empty-GPU profile: the measured free-VRAM baseline equals total, so existing fit expectations are unchanged.
    private static HardwareProfile GpuProfile(long vramBytes)
    {
        return GpuProfileWithFreeVram(vramBytes, availableVramBytes: vramBytes);
    }

    // A GPU profile with an explicit free-VRAM baseline (or null to force the total-VRAM fallback). Total VRAM is held
    // separate from free so a test can model residents already holding VRAM the ledger never saw.
    private static HardwareProfile GpuProfileWithFreeVram(long vramBytes, long? availableVramBytes)
    {
        return new HardwareProfile
        {
            TotalRamBytes = 64 * Gb,
            AvailableRamBytes = 48 * Gb,
            VramBytes = vramBytes,
            AvailableVramBytes = availableVramBytes,
            VramKnown = true,
            GpuVendor = GpuVendor.Nvidia,
            GpuAccelAvailable = true,
            CpuCores = 16,
            FreeDiskBytes = 500 * Gb
        };
    }

    private static HardwareProfile CpuProfile(long availableRam)
    {
        return new HardwareProfile
        {
            TotalRamBytes = 32 * Gb,
            AvailableRamBytes = availableRam,
            VramBytes = null,
            VramKnown = false,
            GpuVendor = GpuVendor.Unknown,
            GpuAccelAvailable = false,
            CpuCores = 16,
            FreeDiskBytes = 500 * Gb
        };
    }

    // Assembles the capacity service over mocked probes + a real ledger. Mutate the public fields before Build().
    private sealed class Harness
    {
        public bool CloudSelected { get; init; }
        public string ProviderName { get; init; } = Llamacpp;
        public int MaxLoadedProcesses { get; init; } = 3;
        public HardwareProfile Profile { get; init; } = null!;
        public ModelFootprint Footprint { get; init; } = ModelFootprint.Unknown;
        public IReadOnlyList<LlamaServerProcessHealth> RunningLlama { get; init; } = [];
        public IReadOnlyList<RunningModelSnapshot> RunningOllama { get; init; } = [];

        public IHardwareProfiler HardwareProfiler { get; } = Substitute.For<IHardwareProfiler>();
        public ILlamaServerProcessSupervisor Supervisor { get; } = Substitute.For<ILlamaServerProcessSupervisor>();
        public IModelFootprintProvider FootprintProvider { get; } = Substitute.For<IModelFootprintProvider>();
        public PendingFootprintLedger Ledger { get; } = new();

        public CapacityService Build()
        {
            var cloud = Substitute.For<IActiveCloudChatClientFactory>();
            cloud.IsCloudProviderSelected(Arg.Any<string?>()).Returns(CloudSelected);

            var resolver = Substitute.For<ILocalModelProviderResolver>();
            resolver.ResolveProviderNameForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(ProviderName));
            resolver.MaxLoadedProcesses.Returns(MaxLoadedProcesses);

            HardwareProfiler.GetProfileAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
                            .Returns(Task.FromResult(Profile));
            Supervisor.CheckHealthAsync(Arg.Any<CancellationToken>())
                      .Returns(Task.FromResult(RunningLlama));
            FootprintProvider.ResolveFootprintAsync(Arg.Any<string>(), Arg.Any<HardwareProfile>(), Arg.Any<CancellationToken>())
                             .Returns(Task.FromResult(Footprint));

            var ollama = Substitute.For<IOllamaModelService>();
            ollama.ListRunningModelsAsync(Arg.Any<CancellationToken>())
                  .Returns(Task.FromResult(RunningOllama));

            return new CapacityService(cloud, resolver, HardwareProfiler, Supervisor, ollama, FootprintProvider, Ledger);
        }
    }
}
