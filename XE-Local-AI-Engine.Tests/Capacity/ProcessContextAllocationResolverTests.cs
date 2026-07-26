namespace XE_Local_AI_Engine.Tests.Capacity;

using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.ModelFit.Fit;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class ProcessContextAllocationResolverTests
{
    private const long Gb = 1024L * 1024 * 1024;
    private const string Model = "repo/model:Q4_K_M";

    [Test]
    public async Task Resolve_CpuOrPhantomVram_UsesCpuRamOnly()
    {
        var resolver = BuildResolver(Profile(32 * Gb, vram: 32 * Gb, vramKnown: false));

        var allocation = AssertEx.NotNull(await resolver.ResolveAsync(Model,
            ModelRole.Chat,
            GpuVariant.Cuda,
            ResolvedLaunchArguments.Explore(),
            CancellationToken.None));

        AssertEx.Equal(ProcessPlacementMode.Cpu, allocation.Placement);
        AssertEx.Equal(expected: 0, allocation.Footprint.GpuBytes);
        AssertEx.True(allocation.Footprint.RamBytes > 0);
    }

    [Test]
    [Arguments(8)]
    [Arguments(16)]
    [Arguments(32)]
    public async Task Resolve_SyntheticGpuBudget_SelectsDeclaredChatTier(int vramGb)
    {
        var resolver = BuildResolver(Profile(64 * Gb, vramGb * Gb, vramKnown: true), processBudget: vramGb * Gb);

        var allocation = AssertEx.NotNull(await resolver.ResolveAsync(Model,
            ModelRole.Chat,
            GpuVariant.Cuda,
            ResolvedLaunchArguments.Explore(),
            CancellationToken.None));

        AssertEx.Contains(LlamaServerLaunchPolicyOptions.ChatContextTiers, tier => tier == allocation.ProcessContextTokens);
        AssertEx.Equal(ProcessContextAllocationSource.HardwareTier, allocation.Source);
        AssertEx.True(allocation.Footprint.GpuBytes > 0);
        AssertEx.True(allocation.Footprint.RamBytes > 0);
    }

    [Test]
    public async Task Resolve_TrainCeilingSubtractsMarginAndAligns()
    {
        var resolver = BuildResolver(Profile(64 * Gb, 32 * Gb, vramKnown: true),
            processBudget: 32 * Gb,
            facts: Facts(contextLength: 10000));

        var allocation = AssertEx.NotNull(await resolver.ResolveAsync(Model,
            ModelRole.Chat,
            GpuVariant.Cuda,
            ResolvedLaunchArguments.Explore(),
            CancellationToken.None));

        AssertEx.Equal(expected: 9728, allocation.ProcessContextTokens);
        AssertEx.Equal(expected: 9744, allocation.ModelTrainContextTokens);
    }

    [Test]
    public async Task Resolve_FrozenProfileWinsOverDeterministicOverride()
    {
        var resolver = BuildResolver(Profile(64 * Gb, 32 * Gb, vramKnown: true),
            processBudget: 32 * Gb,
            options: new LlamaServerLaunchPolicyOptions
            {
                DeterministicContextTokensOverride = 4096
            });

        var allocation = AssertEx.NotNull(await resolver.ResolveAsync(Model,
            ModelRole.Chat,
            GpuVariant.Cuda,
            ResolvedLaunchArguments.Replay(ctxSize: 8192),
            CancellationToken.None));

        AssertEx.Equal(expected: 8192, allocation.ProcessContextTokens);
        AssertEx.Equal(ProcessContextAllocationSource.FrozenProfile, allocation.Source);
    }

    [Test]
    public async Task Resolve_DeterministicOverrideWinsOverHardwareTier()
    {
        var resolver = BuildResolver(Profile(64 * Gb, 32 * Gb, vramKnown: true),
            processBudget: 32 * Gb,
            options: new LlamaServerLaunchPolicyOptions
            {
                DeterministicContextTokensOverride = 12288
            });

        var allocation = AssertEx.NotNull(await resolver.ResolveAsync(Model,
            ModelRole.Chat,
            GpuVariant.Cuda,
            ResolvedLaunchArguments.Explore(),
            CancellationToken.None));

        AssertEx.Equal(expected: 12288, allocation.ProcessContextTokens);
        AssertEx.Equal(ProcessContextAllocationSource.DeterministicOverride, allocation.Source);
    }

    [Test]
    public async Task Resolve_MoeProjectionAccountsForBothAxes()
    {
        var resolver = BuildResolver(Profile(64 * Gb, 8 * Gb, vramKnown: true),
            processBudget: 8 * Gb,
            facts: Facts(expertCount: 64, expertUsedCount: 8));

        var allocation = AssertEx.NotNull(await resolver.ResolveAsync(Model,
            ModelRole.Chat,
            GpuVariant.Cuda,
            ResolvedLaunchArguments.Explore(),
            CancellationToken.None));

        AssertEx.True(allocation.Footprint.GpuBytes > 0);
        AssertEx.True(allocation.Footprint.RamBytes > 0);
        AssertEx.True(allocation.Placement is ProcessPlacementMode.ExpertOffload or ProcessPlacementMode.Hybrid);
    }

    [Test]
    public async Task DownTier_IsAutomaticOnlyAndBoundedToTwo()
    {
        var resolver = BuildResolver(Profile(64 * Gb, 32 * Gb, vramKnown: true), processBudget: 32 * Gb);
        var automatic = AssertEx.NotNull(await resolver.ResolveAsync(Model,
            ModelRole.Chat,
            GpuVariant.Cuda,
            ResolvedLaunchArguments.Explore(),
            CancellationToken.None));

        AssertEx.True(resolver.TryDownTierAfterOutOfMemory(automatic, out var first));
        AssertEx.True(resolver.TryDownTierAfterOutOfMemory(first, out var second));
        AssertEx.False(resolver.TryDownTierAfterOutOfMemory(second, out _));

        var frozen = automatic with
        {
            Source = ProcessContextAllocationSource.FrozenProfile
        };
        AssertEx.False(resolver.TryDownTierAfterOutOfMemory(frozen, out _));
    }

    private static ProcessContextAllocationResolver BuildResolver(
        HardwareProfile profile,
        long? processBudget = null,
        GgufModelFootprintFacts? facts = null,
        LlamaServerLaunchPolicyOptions? options = null)
    {
        var store = Substitute.For<IGgufModelStore>();
        store.ResolveModelFootprintFactsAsync(Model, Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<GgufModelFootprintFacts?>(facts ?? Facts()));

        var audit = Substitute.For<IRuntimeDeviceAudit>();
        audit.GetEffectiveProfileAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult(profile));

        var probe = Substitute.For<IProcessVramBudgetProbe>();
        probe.TryGetProcessBudgetBytesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult(processBudget));

        return new ProcessContextAllocationResolver(store,
            audit,
            probe,
            new MemoryFitEstimator(),
            options ?? new LlamaServerLaunchPolicyOptions());
    }

    private static GgufModelFootprintFacts Facts(long contextLength = 131072, long? expertCount = null, long? expertUsedCount = null) =>
        new("Q4_K_M",
            FileSizeBytes: 5 * Gb,
            ParamCount: 8_000_000_000,
            BlockCount: 32,
            AttentionHeadCount: 32,
            AttentionHeadCountKV: 8,
            EmbeddingLength: 4096,
            ContextLength: contextLength,
            ContentIdentity: "sha256:model",
            Architecture: expertCount is > 0 ? "qwen3moe" : "llama",
            ExpertCount: expertCount,
            ExpertUsedCount: expertUsedCount);

    private static HardwareProfile Profile(long ram, long vram, bool vramKnown) =>
        new()
        {
            TotalRamBytes = ram,
            AvailableRamBytes = ram,
            VramBytes = vram,
            AvailableVramBytes = vramKnown ? vram : null,
            VramKnown = vramKnown,
            GpuVendor = vramKnown ? GpuVendor.Nvidia : GpuVendor.Unknown,
            GpuAccelAvailable = vramKnown,
            CpuCores = 16,
            FreeDiskBytes = 500 * Gb
        };
}
