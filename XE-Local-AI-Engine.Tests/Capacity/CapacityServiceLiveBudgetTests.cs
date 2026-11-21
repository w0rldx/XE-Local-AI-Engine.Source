namespace XE_Local_AI_Engine.Tests.Capacity;

using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Capacity.Implementation;
using XE_Local_AI_Engine.Client.Services.ModelFit.Fit;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <see cref="CapacityService" /> over the real <see cref="ModelFootprintProvider" /> and <see cref="ProcessContextAllocationResolver" />, so a
///     decision is priced the way a node prices it: the stable per-process budget sizes the booking, global free VRAM admits it.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class CapacityServiceLiveBudgetTests
{
    private const long Mib = 1024L * 1024;
    private const long Gb = 1024L * Mib;
    private const string Dense = "repo/dense-70b:Q4_K_M";
    private const string SecondDense = "repo/dense-70b-instruct:Q4_K_M";
    private const string Qwen38 = "unsloth/Qwen3.8-27B-GGUF:UD-Q4_K_M";
    private const string Qwen36Moe = "unsloth/Qwen3.6-35B-A3B-GGUF:UD-Q4_K_M";

    // Measured on a WSL2 (WDDM) host on 2026-11-20: the --list-devices process budget and nvidia-smi global free, idle desktop.
    private const long WddmProcessBudget = 30927 * Mib;
    private const long IdleWddmGlobalFree = 28796 * Mib;

    [Test]
    public async Task Decide_IdleWddmHost_HybridChatModel_GpuAxisRefusesOnBase_AdmittedAtTheLiveShare()
    {
        // Without the live share (base behaviour) the stable Hybrid booking binds on the GPU axis.
        var stableOnly = Harness(IdleWddmGlobalFree, liveShareHybrid: false);
        var refused = await stableOnly.Service.DecideAsync(ChatRequest(Dense), CancellationToken.None);
        AssertEx.Equal(CapacityVerdict.RejectInsufficient, refused.Verdict);
        AssertEx.True(refused.IsMemoryShortfall, "the refusal is a memory shortfall.");
        var stable = AssertEx.NotNull(refused.Admission).Allocation;
        AssertEx.Equal(ProcessPlacementMode.Hybrid, stable.Placement);
        AssertEx.True(stable.Footprint.GpuBytes > IdleWddmGlobalFree, "the Hybrid booking is the process budget less its reserve, above global free.");

        var harness = Harness(IdleWddmGlobalFree);
        var decision = await harness.Service.DecideAsync(ChatRequest(Dense), CancellationToken.None);

        AssertEx.Equal(CapacityVerdict.Allow, decision.Verdict);
        var placed = AssertEx.NotNull(decision.Admission).Allocation;
        AssertEx.Equal(ProcessPlacementMode.Hybrid, placed.Placement);
        AssertEx.Equal(expected: 1, placed.ChatSlots);
        AssertEx.Equal(UsableGpuBudget(IdleWddmGlobalFree), placed.Footprint.GpuBytes);
        AssertEx.Equal(IdleWddmGlobalFree, placed.GpuPlacementBudgetBytes);
        AssertEx.Equal(WddmProcessBudget - placed.Footprint.GpuBytes, placed.FitTargetBytes);
        AssertEx.Equal(placed.Footprint, harness.Ledger.Reserved);
        decision.Reservation?.Dispose();
    }

    [Test]
    public async Task Decide_DenseModelThatFitsResidentSmaller_IsRescuedByTheLadder()
    {
        // Resident at 65,536 two-slot books about 24 GiB; with 22 GiB free the ladder finds a smaller resident window.
        var harness = Harness(globalFree: 22 * Gb);

        var decision = await harness.Service.DecideAsync(ChatRequest(Qwen38), CancellationToken.None);

        AssertEx.Equal(CapacityVerdict.Allow, decision.Verdict);
        var allocation = AssertEx.NotNull(decision.Admission).Allocation;
        AssertEx.Equal(ProcessPlacementMode.GpuResident, allocation.Placement);
        AssertEx.True(allocation.ProcessContextTokens < 65536, $"the ladder steps below the stable window, got {allocation.ProcessContextTokens}.");
        decision.Reservation?.Dispose();
    }

    [Test]
    public async Task Decide_DenseModelUnderPressure_WindowsDownResident_NotHybrid()
    {
        // Stage 1 never takes a dense model, so the ladder decides against raw free VRAM: no reserve band shrinks the window or drops a slot.
        var withoutReplacement = Harness(globalFree: 22 * Gb, liveShareHybrid: false);
        var expected = AssertEx.NotNull((await withoutReplacement.Service.DecideAsync(ChatRequest(Qwen38) with
        {
            PublishLaunchAdmission = false
        }, CancellationToken.None)).Admission).Allocation;
        var harness = Harness(globalFree: 22 * Gb);

        var decision = await harness.Service.DecideAsync(ChatRequest(Qwen38), CancellationToken.None);

        AssertEx.Equal(CapacityVerdict.Allow, decision.Verdict);
        var allocation = AssertEx.NotNull(decision.Admission).Allocation;
        AssertEx.Equal(ProcessPlacementMode.GpuResident, allocation.Placement);
        AssertEx.Equal(expected.ProcessContextTokens, allocation.ProcessContextTokens);
        AssertEx.Equal(expected.ChatSlots, allocation.ChatSlots);
        AssertEx.Equal(expected.Footprint, allocation.Footprint);
        decision.Reservation?.Dispose();
    }

    [Test]
    public async Task Decide_MoeSmallestResidentWindowFails_OffloadFits_AdmitsExpertOffload()
    {
        // 12 GiB free: the 35B-A3B's ~23 GiB of weights fit resident at no window, its non-expert share and KV fit easily. Native Linux (no live share).
        var harness = Harness(globalFree: 12 * Gb, liveShareHybrid: false);

        var decision = await harness.Service.DecideAsync(ChatRequest(Qwen36Moe), CancellationToken.None);

        AssertExpertOffloadAdmitted(decision);
    }

    [Test]
    public async Task Decide_MoeWithLedgerReservedResident_OffloadFits_AdmitsExpertOffload()
    {
        // Global free looks roomy, but another admission's booking still sits in the ledger: the live budget nets it out.
        var harness = Harness(globalFree: 30 * Gb, liveShareHybrid: false);
        using var inFlight = harness.Ledger.Reserve(new ResourceFootprint(18 * Gb, RamBytes: 0));

        var decision = await harness.Service.DecideAsync(ChatRequest(Qwen36Moe), CancellationToken.None);

        AssertExpertOffloadAdmitted(decision);
    }

    [Test]
    public async Task Decide_HybridLiveShare_RamBetweenFileAndTopTierSpill_TakesTheLargestWindowThatFits()
    {
        // 37 GiB free RAM: the 65,536 share spills about 37.5 GiB to RAM, 32,768 books the 36.7 GiB file.
        var harness = Harness(IdleWddmGlobalFree, availableRam: 37 * Gb);

        var decision = await harness.Service.DecideAsync(ChatRequest(Dense), CancellationToken.None);

        AssertEx.Equal(CapacityVerdict.Allow, decision.Verdict);
        var placed = AssertEx.NotNull(decision.Admission).Allocation;
        AssertEx.Equal(ProcessPlacementMode.Hybrid, placed.Placement);
        AssertEx.Equal(expected: 32768, placed.ProcessContextTokens);
        AssertEx.True(placed.Footprint.RamBytes <= 37 * Gb, $"the booking must fit live RAM, got {placed.Footprint.RamBytes / Mib} MiB.");
        decision.Reservation?.Dispose();
    }

    [Test]
    public async Task Decide_HybridLiveShare_RamBelowFile_Refuses()
    {
        // Every Hybrid window books at least the 36.7 GiB file in RAM.
        var harness = Harness(IdleWddmGlobalFree, availableRam: 30 * Gb);

        var decision = await harness.Service.DecideAsync(ChatRequest(Dense), CancellationToken.None);

        AssertEx.Equal(CapacityVerdict.RejectInsufficient, decision.Verdict);
        AssertEx.Equal(ResourceFootprint.Zero, harness.Ledger.Reserved);
    }

    [Test]
    public async Task Decide_HybridLiveShare_NativeLinux_StageThreeOff()
    {
        var harness = Harness(IdleWddmGlobalFree, liveShareHybrid: false);

        var decision = await harness.Service.DecideAsync(ChatRequest(Dense), CancellationToken.None);

        AssertEx.Equal(CapacityVerdict.RejectInsufficient, decision.Verdict);
        AssertEx.True(decision.IsMemoryShortfall);
    }

    [Test]
    public async Task Decide_HybridLiveShare_ExternalDraftModel_StagesOff()
    {
        // The draft's VRAM is not in the booking, so neither stage may place against live memory beside it.
        var harness = Harness(IdleWddmGlobalFree,
            supervisorOptions: new LlamaServerSupervisorOptions
            {
                SpeculativeMode = "draft-simple",
                SpeculativeDraftModelPath = "/models/draft.gguf"
            });

        var dense = await harness.Service.DecideAsync(ChatRequest(Dense), CancellationToken.None);
        var moe = await Harness(globalFree: 12 * Gb,
            supervisorOptions: new LlamaServerSupervisorOptions
            {
                SpeculativeMode = "draft-simple",
                SpeculativeDraftModelPath = "/models/draft.gguf"
            }).Service.DecideAsync(ChatRequest(Qwen36Moe), CancellationToken.None);

        AssertEx.Equal(CapacityVerdict.RejectInsufficient, dense.Verdict);
        AssertEx.Equal(CapacityVerdict.RejectInsufficient, moe.Verdict);
    }

    [Test]
    public async Task Decide_HybridLiveShare_ReplayWithZeroCtx_NotReplaced()
    {
        // A replay that pins no -c resolves as a hardware tier, but its argv is fixed: only an explore launch is re-placed.
        var harness = Harness(IdleWddmGlobalFree, resolved: ResolvedLaunchArguments.Replay(ctxSize: 0, nGpuLayers: 60));

        var decision = await harness.Service.DecideAsync(ChatRequest(Dense), CancellationToken.None);

        AssertEx.Equal(CapacityVerdict.RejectInsufficient, decision.Verdict);
        AssertEx.Equal(ProcessContextAllocationSource.HardwareTier, AssertEx.NotNull(decision.Admission).Allocation.Source);
    }

    [Test]
    public async Task Decide_FrozenDenseHybridOnIdleWddm_StillRefused()
    {
        // Known limit: a frozen partial -ngl keeps today's GPU booking (the stable Hybrid share), so an idle WDDM desktop still refuses it.
        var harness = Harness(IdleWddmGlobalFree, resolved: ResolvedLaunchArguments.Replay(ctxSize: 8192, nGpuLayers: 60));

        var decision = await harness.Service.DecideAsync(ChatRequest(Dense), CancellationToken.None);

        AssertEx.Equal(CapacityVerdict.RejectInsufficient, decision.Verdict);
        AssertEx.True(decision.IsMemoryShortfall, "the refusal is a memory shortfall.");
        var booked = AssertEx.NotNull(decision.Admission).Allocation;
        AssertEx.Equal(ProcessContextAllocationSource.FrozenProfile, booked.Source);
        AssertEx.Equal(ProcessPlacementMode.Hybrid, booked.Placement);
        AssertEx.True(booked.Footprint.GpuBytes > IdleWddmGlobalFree, "the frozen booking stays at the stable share, above global free.");
        AssertEx.Equal(ResourceFootprint.Zero, harness.Ledger.Reserved);
    }

    [Test]
    public async Task Decide_HybridLiveShare_Benchmark_PublishFalse_NotReplaced()
    {
        var harness = Harness(IdleWddmGlobalFree);

        var decision = await harness.Service.DecideAsync(ChatRequest(Dense) with
        {
            PublishLaunchAdmission = false
        }, CancellationToken.None);

        AssertEx.Equal(CapacityVerdict.RejectInsufficient, decision.Verdict);
    }

    [Test]
    public async Task Decide_TwoKeys_SecondHybridSeesTheFirstReservation()
    {
        var harness = Harness(IdleWddmGlobalFree, availableRam: 120 * Gb);

        var first = await harness.Service.DecideAsync(ChatRequest(Dense), CancellationToken.None);
        AssertEx.Equal(CapacityVerdict.Allow, first.Verdict);
        var firstGpu = AssertEx.NotNull(first.Admission).Allocation.Footprint.GpuBytes;

        var second = await harness.Service.DecideAsync(ChatRequest(SecondDense), CancellationToken.None);

        // The first booking still sits in the ledger (its launch has not started), so the second share is what is left of global free.
        AssertEx.Equal(CapacityVerdict.Allow, second.Verdict);
        AssertEx.Equal(UsableGpuBudget(IdleWddmGlobalFree - firstGpu), AssertEx.NotNull(second.Admission).Allocation.Footprint.GpuBytes);
        AssertEx.True(harness.Ledger.Reserved.GpuBytes <= IdleWddmGlobalFree,
            $"two live shares overbook global free: {harness.Ledger.Reserved.GpuBytes / Mib} MiB reserved.");
        second.Reservation?.Dispose();
        first.Reservation?.Dispose();
    }

    private static long UsableGpuBudget(long budget) =>
        LiveBudgetCapacityHarness.UsableGpuBudget(budget);

    private static void AssertExpertOffloadAdmitted(CapacityDecision decision)
    {
        AssertEx.Equal(CapacityVerdict.Allow, decision.Verdict);
        var allocation = AssertEx.NotNull(decision.Admission).Allocation;
        AssertEx.Equal(ProcessPlacementMode.ExpertOffload, allocation.Placement);
        AssertEx.True(allocation.ProcessContextTokens >= 16384, $"offload keeps a window at or above the resident floor, got {allocation.ProcessContextTokens}.");
        decision.Reservation?.Dispose();
    }

    private static CapacityRequest ChatRequest(string model) =>
        new()
        {
            ModelName = model,
            Role = ModelRole.Chat
        };

    // A dense 70B at Q4_K_M: 36.67 GiB of weights, larger than any single card here, so its stable allocation is Hybrid.
    private static GgufModelFootprintFacts SeventyBillionParameterFacts(string contentIdentity) =>
        new()
        {
            Quant = "Q4_K_M",
            FileSizeBytes = (long)(70_000_000_000 * MemoryFitEstimator.BytesPerWeight("Q4_K_M")),
            ParamCount = 70_000_000_000,
            BlockCount = 80,
            AttentionHeadCount = 64,
            AttentionHeadCountKV = 8,
            EmbeddingLength = 8192,
            ContextLength = 131072,
            ContentIdentity = contentIdentity,
            Architecture = "llama"
        };

    // Qwen3.8-27B UD-Q4_K_M-like: 24,093 MiB at 65,536 two-slot, 21,656 MiB at 32,768 two-slot.
    private static GgufModelFootprintFacts Qwen38DenseFacts() =>
        new()
        {
            Quant = "UD-Q4_K_M",
            FileSizeBytes = 16_464_440_224,
            ParamCount = null,
            BlockCount = 65,
            AttentionHeadCount = 24,
            AttentionHeadCountKV = 4,
            EmbeddingLength = 5120,
            ContextLength = 262144,
            AttentionKeyLength = 256,
            AttentionValueLength = 256,
            FullAttentionInterval = 4,
            ContentIdentity = "sha256:qwen38-27b",
            Architecture = "qwen35"
        };

    // The real unsloth Qwen3.6-35B-A3B UD-Q4_K_M header: no general.parameter_count (weights = file size), 40 blocks of which every 4th is attention.
    private static GgufModelFootprintFacts Qwen36MoeFacts() =>
        new()
        {
            Quant = "UD-Q4_K_M",
            FileSizeBytes = 22_134_528_992,
            ParamCount = null,
            BlockCount = 40,
            AttentionHeadCount = 16,
            AttentionHeadCountKV = 2,
            EmbeddingLength = 2048,
            ContextLength = 262144,
            AttentionKeyLength = 256,
            AttentionValueLength = 256,
            FullAttentionInterval = 4,
            ContentIdentity = "sha256:qwen36-35b-a3b",
            Architecture = "qwen35moe",
            ExpertCount = 256,
            ExpertUsedCount = 8
        };

    private static readonly Dictionary<string, GgufModelFootprintFacts> Models = new(StringComparer.Ordinal)
    {
        [Dense] = SeventyBillionParameterFacts("sha256:dense-70b"),
        [SecondDense] = SeventyBillionParameterFacts("sha256:dense-70b-instruct"),
        [Qwen38] = Qwen38DenseFacts(),
        [Qwen36Moe] = Qwen36MoeFacts()
    };

    private static LiveBudgetCapacityHarness Harness(long globalFree,
        bool liveShareHybrid = true,
        long availableRam = 60 * Gb,
        LlamaServerSupervisorOptions? supervisorOptions = null,
        ResolvedLaunchArguments? resolved = null) =>
        new(Models, globalFree, liveShareHybrid, availableRam, supervisorOptions, resolved);
}
