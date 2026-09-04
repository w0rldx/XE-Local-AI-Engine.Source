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
        AssertEx.Equal(allocation.Placement == ProcessPlacementMode.GpuResident,
            allocation.Footprint.RamBytes == 0,
            "only placements with CPU-resident model data reserve RAM; a fully GPU-resident mmap does not.");
    }

    [Test]
    public async Task Resolve_NoTierFits_FloorsChatAtTheDefaultConversationWindow()
    {
        // 16 GiB VRAM / 32 GiB RAM (the target desktop) with a dense 70B at Q4_K_M. Usable budgets are 15.20 GiB GPU and
        // 27.20 GiB RAM, but the weights alone are 36.67 GiB — so the RAM side overflows at EVERY tier and the walk
        // exhausts. Dropping to 2048 there is the worst possible answer: the weights term that overflowed does not
        // shrink with the window (2048 saves 2.1 GiB of KV against a 9.5 GiB shortfall), while the reserved output floor
        // then claims half the window and the agent scaffold alone overflows the always-keep set, so every send fails
        // with a context-budget error. llama.cpp splits the layers and launches regardless, so the window must not
        // collapse with the weights.
        var resolver = BuildResolver(Profile(32 * Gb, 16 * Gb, vramKnown: true),
            processBudget: 16 * Gb,
            facts: SeventyBillionParameterFacts());

        var allocation = AssertEx.NotNull(await resolver.ResolveAsync(Model,
            ModelRole.Chat,
            GpuVariant.Cuda,
            ResolvedLaunchArguments.Explore(),
            CancellationToken.None));

        AssertEx.Equal(expected: 8192, allocation.ProcessContextTokens);
        AssertEx.Equal(ProcessContextAllocationSource.HardwareTier, allocation.Source);
        AssertEx.Equal(ProcessPlacementMode.Hybrid, allocation.Placement);
    }

    [Test]
    public async Task Resolve_NoTierFits_StillDegradesBelowTheFloor_WhenEvenTheKvCacheDoesNotFit()
    {
        // The floor is a floor, not a guarantee. A 3 GiB CPU-only host leaves 1.0 GiB usable; an 8B Q4_K_M needs 4.19 GiB
        // of weights, so nothing fits. The 8192-token KV cache alone costs 1.12 GiB — over budget — so the fallback keeps
        // walking down and lands on 4096 (0.56 GiB) rather than blindly allocating a window the host cannot hold.
        var resolver = BuildResolver(Profile(3 * Gb, vram: 0, vramKnown: false));

        var allocation = AssertEx.NotNull(await resolver.ResolveAsync(Model,
            ModelRole.Chat,
            GpuVariant.Cpu,
            ResolvedLaunchArguments.Explore(),
            CancellationToken.None));

        AssertEx.Equal(expected: 4096, allocation.ProcessContextTokens);
        AssertEx.Equal(ProcessPlacementMode.Cpu, allocation.Placement);
    }

    [Test]
    public async Task Resolve_NoTierFits_LeavesAuxiliaryRolesAtTheirConfiguredWindow()
    {
        // Embedding/reranker carry a single configured window rather than a tier ladder, and their requests are single
        // short forward passes — the chat floor must not inflate them.
        var resolver = BuildResolver(Profile(32 * Gb, 16 * Gb, vramKnown: true),
            processBudget: 16 * Gb,
            facts: SeventyBillionParameterFacts());

        var allocation = AssertEx.NotNull(await resolver.ResolveAsync(Model,
            ModelRole.Embedding,
            GpuVariant.Cuda,
            ResolvedLaunchArguments.Explore(),
            CancellationToken.None));

        AssertEx.Equal(expected: 2048, allocation.ProcessContextTokens);
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
    [Arguments(ModelRole.Embedding, 3072)]
    [Arguments(ModelRole.Reranker, 1536)]
    public async Task Resolve_AuxiliaryRoleUsesConfiguredContextTokens(ModelRole role, int expectedContextTokens)
    {
        var resolver = BuildResolver(Profile(64 * Gb, 32 * Gb, vramKnown: true),
            processBudget: 32 * Gb,
            options: new LlamaServerLaunchPolicyOptions
            {
                EmbeddingContextTokens = 3072,
                RerankerContextTokens = 1536
            });

        var allocation = AssertEx.NotNull(await resolver.ResolveAsync(Model,
            role,
            GpuVariant.Cuda,
            ResolvedLaunchArguments.Explore(),
            CancellationToken.None));

        AssertEx.Equal(expectedContextTokens, allocation.ProcessContextTokens);
    }

    [Test]
    public async Task Resolve_TrainCeilingUsesConfiguredSafetyMargin()
    {
        var resolver = BuildResolver(Profile(64 * Gb, 32 * Gb, vramKnown: true),
            processBudget: 32 * Gb,
            facts: Facts(contextLength: 10000),
            options: new LlamaServerLaunchPolicyOptions
            {
                ContextSafetyMarginTokens = 512
            });

        var allocation = AssertEx.NotNull(await resolver.ResolveAsync(Model,
            ModelRole.Chat,
            GpuVariant.Cuda,
            ResolvedLaunchArguments.Explore(),
            CancellationToken.None));

        AssertEx.Equal(expected: 9472, allocation.ProcessContextTokens);
        AssertEx.Equal(expected: 9488, allocation.ModelTrainContextTokens);
    }

    [Test]
    public async Task Resolve_AuxiliaryRoleContextPolicyChangesCacheIdentity()
    {
        var baseline = AssertEx.NotNull(await BuildResolver(Profile(64 * Gb, 32 * Gb, vramKnown: true),
                processBudget: 32 * Gb,
                options: new LlamaServerLaunchPolicyOptions
                {
                    EmbeddingContextTokens = 2048
                })
            .ResolveAsync(Model,
                ModelRole.Embedding,
                GpuVariant.Cuda,
                ResolvedLaunchArguments.Explore(),
                CancellationToken.None));
        var changed = AssertEx.NotNull(await BuildResolver(Profile(64 * Gb, 32 * Gb, vramKnown: true),
                processBudget: 32 * Gb,
                options: new LlamaServerLaunchPolicyOptions
                {
                    EmbeddingContextTokens = 4096
                })
            .ResolveAsync(Model,
                ModelRole.Embedding,
                GpuVariant.Cuda,
                ResolvedLaunchArguments.Explore(),
                CancellationToken.None));

        AssertEx.NotEqual(baseline.CacheKey, changed.CacheKey);
    }

    [Test]
    public async Task Resolve_WithAQuantizedKvType_ReservesStrictlyFewerBytesAtTheSameWindow()
    {
        // A benchmark run frozen at q8_0/q4_0 holds a smaller KV cache than the fp16 figure the ledger used to book,
        // and the two quantized sizes are themselves ordered. The window is identical in all three cases: only the KV
        // term moves.
        var resolver = BuildResolver(Profile(64 * Gb, 32 * Gb, vramKnown: true), processBudget: 32 * Gb);
        var frozen = ResolvedLaunchArguments.Replay(ctxSize: 32768);

        var fp16 = AssertEx.NotNull(await Resolve(resolver, frozen, kvCacheType: null));
        var q8 = AssertEx.NotNull(await Resolve(resolver, frozen, "q8_0"));
        var q4 = AssertEx.NotNull(await Resolve(resolver, frozen, "q4_0"));

        AssertEx.True(q8.Footprint.GpuBytes < fp16.Footprint.GpuBytes,
            $"q8_0 must reserve less than fp16 ({q8.Footprint.GpuBytes} vs {fp16.Footprint.GpuBytes}).");
        AssertEx.True(q4.Footprint.GpuBytes < q8.Footprint.GpuBytes,
            $"q4_0 must reserve less than q8_0 ({q4.Footprint.GpuBytes} vs {q8.Footprint.GpuBytes}).");
        AssertEx.Equal(fp16.ProcessContextTokens, q8.ProcessContextTokens);
        AssertEx.Equal(fp16.ProcessContextTokens, q4.ProcessContextTokens);
    }

    [Test]
    [Arguments(null)]
    [Arguments("f16")]
    [Arguments("not-a-cache-type")]
    public async Task Resolve_WithNoOrUnrecognizedKvType_IsTheAllocationEveryOtherCallerAlreadyGot(string? kvCacheType)
    {
        // The default has to stay byte-identical — same allocation AND the same cache entry — or every chat spawn on
        // the box quietly re-resolves against a second key. Anything the estimator cannot read is fp16, conservatively.
        var resolver = BuildResolver(Profile(64 * Gb, 32 * Gb, vramKnown: true), processBudget: 32 * Gb);
        var frozen = ResolvedLaunchArguments.Replay(ctxSize: 32768);

        var baseline = AssertEx.NotNull(await Resolve(resolver, frozen, kvCacheType: null));
        var candidate = AssertEx.NotNull(await Resolve(resolver, frozen, kvCacheType));

        AssertEx.Equal(baseline.CacheKey, candidate.CacheKey);
        AssertEx.Equal(baseline.Footprint, candidate.Footprint);
        AssertEx.Equal(baseline.ProcessContextTokens, candidate.ProcessContextTokens);
    }

    [Test]
    public async Task Resolve_HardwareTierWithAQuantizedKvType_KeepsTheFp16WindowAndOnlyShrinksTheBytes()
    {
        // The guard the whole option rests on: a quantized request may never reserve MORE than fp16. It could, if the
        // tier walk scored against the smaller estimate and so selected a LARGER window — so the tier is still chosen
        // against fp16 and only the resulting allocation is re-sized.
        var resolver = BuildResolver(Profile(64 * Gb, 16 * Gb, vramKnown: true), processBudget: 16 * Gb);

        var fp16 = AssertEx.NotNull(await Resolve(resolver, ResolvedLaunchArguments.Explore(), kvCacheType: null));
        var q4 = AssertEx.NotNull(await Resolve(resolver, ResolvedLaunchArguments.Explore(), "q4_0"));

        AssertEx.Equal(fp16.ProcessContextTokens, q4.ProcessContextTokens);
        AssertEx.True(q4.Footprint.GpuBytes < fp16.Footprint.GpuBytes);
        AssertEx.True(q4.Footprint.RamBytes <= fp16.Footprint.RamBytes);
    }

    private static Task<ProcessContextAllocation?> Resolve(ProcessContextAllocationResolver resolver,
        ResolvedLaunchArguments resolved,
        string? kvCacheType) =>
        resolver.ResolveAsync(Model, ModelRole.Chat, GpuVariant.Cuda, resolved, kvCacheType, CancellationToken.None);

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
    public async Task Resolve_GpuResident_DoesNotReserveMemoryMappedWeightsAsRam()
    {
        var resolver = BuildResolver(Profile(64 * Gb, 32 * Gb, vramKnown: true),
            processBudget: 32 * Gb,
            facts: Facts(fileSizeBytes: 12 * Gb, paramCount: 1_000_000_000));

        var allocation = AssertEx.NotNull(await resolver.ResolveAsync(Model,
            ModelRole.Chat,
            GpuVariant.Cuda,
            ResolvedLaunchArguments.Replay(ctxSize: 2048),
            CancellationToken.None));

        AssertEx.Equal(ProcessPlacementMode.GpuResident, allocation.Placement);
        AssertEx.True(allocation.Footprint.GpuBytes > 0);
        AssertEx.Equal(expected: 0, allocation.Footprint.RamBytes);
    }

    [Test]
    public async Task Resolve_FaultedCachedComputation_IsEvictedAndRetried()
    {
        var audit = Substitute.For<IRuntimeDeviceAudit>();
        var calls = 0;
        audit.GetEffectiveProfileAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
             .Returns(_ =>
             {
                 calls++;
                 return calls == 1
                     ? Task.FromException<HardwareProfile>(new InvalidOperationException("transient probe failure"))
                     : Task.FromResult(Profile(64 * Gb, 32 * Gb, vramKnown: true));
             });
        var resolver = BuildResolver(Profile(64 * Gb, 32 * Gb, vramKnown: true),
            processBudget: 32 * Gb,
            audit: audit);

        await AssertEx.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveAsync(Model,
            ModelRole.Chat,
            GpuVariant.Cuda,
            ResolvedLaunchArguments.Explore(),
            CancellationToken.None));

        var allocation = await resolver.ResolveAsync(Model,
            ModelRole.Chat,
            GpuVariant.Cuda,
            ResolvedLaunchArguments.Explore(),
            CancellationToken.None);

        AssertEx.NotNull(allocation);
        AssertEx.Equal(2, calls);
    }

    [Test]
    public async Task Resolve_UsesRegistryQuantLabelForWeightDensity()
    {
        const long paramCount = 1_000_000_000;
        var q6 = AssertEx.NotNull(await BuildResolver(Profile(64 * Gb, vram: 0, vramKnown: false),
                facts: Facts(quant: "Q6_K", paramCount: paramCount))
            .ResolveAsync(Model,
                ModelRole.Chat,
                GpuVariant.Cpu,
                ResolvedLaunchArguments.Replay(ctxSize: 2048),
                CancellationToken.None));
        var q4 = AssertEx.NotNull(await BuildResolver(Profile(64 * Gb, vram: 0, vramKnown: false),
                facts: Facts(quant: "Q4_K_M", paramCount: paramCount))
            .ResolveAsync(Model,
                ModelRole.Chat,
                GpuVariant.Cpu,
                ResolvedLaunchArguments.Replay(ctxSize: 2048),
                CancellationToken.None));

        AssertEx.True(q6.Footprint.RamBytes > q4.Footprint.RamBytes,
            "Q6_K must price above Q4_K_M for the same parameter count.");
        var expectedWeights = (long)(paramCount * MemoryFitEstimator.BytesPerWeight("Q6_K"));
        AssertEx.True(q6.Footprint.RamBytes > expectedWeights,
            "the production allocation must include weights, KV cache, safety margin, and runtime overhead.");
    }

    [Test]
    public async Task Resolve_FromHeaderFacts_ComputesWeightsPlusKv()
    {
        var profile = Profile(64 * Gb, vram: 0, vramKnown: false);
        var facts = Facts(quant: "Q4_K_M",
            fileSizeBytes: 2 * Gb,
            paramCount: 1_000_000_000,
            blockCount: 4,
            attentionHeadCount: 4,
            attentionHeadCountKv: 2,
            embeddingLength: 16);
        var resolver = BuildResolver(profile, facts: facts);

        var allocation = AssertEx.NotNull(await resolver.ResolveAsync(Model,
            ModelRole.Chat,
            GpuVariant.Cpu,
            ResolvedLaunchArguments.Replay(ctxSize: 2048),
            CancellationToken.None));

        var expected = new MemoryFitEstimator().Estimate("Q4_K_M",
            paramCount: 1_000_000_000,
            fileSizeBytes: 2 * Gb,
            blockCount: 4,
            attentionHeadCountKV: 2,
            embeddingLength: 16,
            attentionHeadCount: 4,
            ctxTarget: 2048,
            profile with
            {
                AvailableRamBytes = UsableRamBudget(profile.TotalRamBytes)
            },
            kvCacheQuantized: false,
            moeFacts: new MoeFacts(ActiveParamCount: null, ExpertCount: null, ExpertUsedCount: null),
            attention: new GgufAttentionShape(KeyLength: null,
                ValueLength: null,
                SlidingWindow: null,
                SlidingWindowPattern: null),
            nativeQuantFormat: false);

        AssertEx.Equal(expected.EstimatedBytes, allocation.Footprint.RamBytes);
    }

    [Test]
    public async Task Resolve_FromHeaderFacts_CarriesTheMlaLatentLengths()
    {
        // The admission path is the reason the MLA branch is clamped, so the two latent lengths must survive the
        // GgufModelFootprintFacts -> GgufAttentionShape hop. This geometry is chosen so the latent row (576) dominates
        // the derived head_dim term (embedding 16 / 4 heads = 4), which makes a dropped length visible as a byte
        // difference rather than being hidden by the clamp.
        var profile = Profile(64 * Gb, vram: 0, vramKnown: false);
        var facts = Facts(quant: "Q4_K_M",
            fileSizeBytes: 2 * Gb,
            paramCount: 1_000_000_000,
            blockCount: 4,
            attentionHeadCount: 4,
            attentionHeadCountKv: 2,
            embeddingLength: 16,
            attentionKeyLengthMla: 576,
            attentionValueLengthMla: 512);
        var resolver = BuildResolver(profile, facts: facts);

        var allocation = AssertEx.NotNull(await resolver.ResolveAsync(Model,
            ModelRole.Chat,
            GpuVariant.Cpu,
            ResolvedLaunchArguments.Replay(ctxSize: 2048),
            CancellationToken.None));

        var budgeted = profile with
        {
            AvailableRamBytes = UsableRamBudget(profile.TotalRamBytes)
        };
        var estimator = new MemoryFitEstimator();
        var expected = estimator.Estimate("Q4_K_M", paramCount: 1_000_000_000, fileSizeBytes: 2 * Gb, blockCount: 4,
            attentionHeadCountKV: 2, embeddingLength: 16, attentionHeadCount: 4, ctxTarget: 2048, budgeted,
            kvCacheQuantized: false,
            moeFacts: new MoeFacts(ActiveParamCount: null, ExpertCount: null, ExpertUsedCount: null),
            attention: new GgufAttentionShape(KeyLength: null, ValueLength: null, SlidingWindow: null, SlidingWindowPattern: null,
                KeyLengthMla: 576, ValueLengthMla: 512),
            nativeQuantFormat: false);
        var withoutMla = estimator.Estimate("Q4_K_M", paramCount: 1_000_000_000, fileSizeBytes: 2 * Gb, blockCount: 4,
            attentionHeadCountKV: 2, embeddingLength: 16, attentionHeadCount: 4, ctxTarget: 2048, budgeted,
            kvCacheQuantized: false,
            moeFacts: new MoeFacts(ActiveParamCount: null, ExpertCount: null, ExpertUsedCount: null),
            attention: new GgufAttentionShape(KeyLength: null, ValueLength: null, SlidingWindow: null, SlidingWindowPattern: null),
            nativeQuantFormat: false);

        AssertEx.Equal(expected.EstimatedBytes, allocation.Footprint.RamBytes);
        AssertEx.True(expected.EstimatedBytes > withoutMla.EstimatedBytes,
            "the latent row dominates this geometry, so dropping the MLA lengths on the way through would be visible.");
    }

    [Test]
    public async Task Resolve_FromFileSize_WhenParamCountMissing()
    {
        var resolver = BuildResolver(Profile(64 * Gb, vram: 0, vramKnown: false),
            facts: Facts(fileSizeBytes: 3 * Gb, paramCount: null));

        var allocation = AssertEx.NotNull(await resolver.ResolveAsync(Model,
            ModelRole.Chat,
            GpuVariant.Cpu,
            ResolvedLaunchArguments.Replay(ctxSize: 2048),
            CancellationToken.None));

        AssertEx.True(allocation.Footprint.RamBytes >= 3 * Gb,
            "the on-disk file size must remain the weights fallback when the GGUF parameter count is unavailable.");
    }

    [Test]
    public async Task Resolve_StripsDynamicQuantPrefixBeforeDensityMapping()
    {
        const long paramCount = 1_000_000_000;
        var dynamic = AssertEx.NotNull(await BuildResolver(Profile(64 * Gb, vram: 0, vramKnown: false),
                facts: Facts(quant: "UD-Q6_K", paramCount: paramCount))
            .ResolveAsync(Model,
                ModelRole.Chat,
                GpuVariant.Cpu,
                ResolvedLaunchArguments.Replay(ctxSize: 2048),
                CancellationToken.None));
        var plain = AssertEx.NotNull(await BuildResolver(Profile(64 * Gb, vram: 0, vramKnown: false),
                facts: Facts(quant: "Q6_K", paramCount: paramCount))
            .ResolveAsync(Model,
                ModelRole.Chat,
                GpuVariant.Cpu,
                ResolvedLaunchArguments.Replay(ctxSize: 2048),
                CancellationToken.None));

        AssertEx.Equal(plain.Footprint.RamBytes, dynamic.Footprint.RamBytes);
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
        AssertEx.True(first.Footprint.GpuBytes < automatic.Footprint.GpuBytes,
            "the first lower context tier must recompute a smaller capacity footprint");
        AssertEx.True(second.Footprint.GpuBytes < first.Footprint.GpuBytes,
            "the second lower context tier must recompute its capacity footprint too");

        var cachedAdjusted = AssertEx.NotNull(await resolver.ResolveAsync(Model,
            ModelRole.Chat,
            GpuVariant.Cuda,
            ResolvedLaunchArguments.Explore(),
            CancellationToken.None));
        AssertEx.Equal(second, cachedAdjusted,
            "subsequent capacity admission and launch must consume the same recomputed OOM-adjusted allocation");

        var frozen = automatic with
        {
            Source = ProcessContextAllocationSource.FrozenProfile
        };
        AssertEx.False(resolver.TryDownTierAfterOutOfMemory(frozen, out _));
    }

    [Test]
    public async Task AdmissionDownTier_IsPureUntilCommitted_ThenPersistsMonotonicallyWithoutConsumingOomRetries()
    {
        var resolver = BuildResolver(Profile(64 * Gb, 32 * Gb, vramKnown: true), processBudget: 32 * Gb);
        var automatic = AssertEx.NotNull(await resolver.ResolveAsync(Model,
            ModelRole.Chat,
            GpuVariant.Cuda,
            ResolvedLaunchArguments.Explore(),
            CancellationToken.None));

        AssertEx.True(resolver.TryDownTierForAdmission(automatic, out var first));
        AssertEx.True(resolver.TryDownTierForAdmission(first, out var second));
        AssertEx.True(first.ProcessContextTokens < automatic.ProcessContextTokens);
        AssertEx.True(first.Footprint.GpuBytes < automatic.Footprint.GpuBytes);

        var unchanged = AssertEx.NotNull(await resolver.ResolveAsync(Model,
            ModelRole.Chat,
            GpuVariant.Cuda,
            ResolvedLaunchArguments.Explore(),
            CancellationToken.None));
        AssertEx.Equal(automatic, unchanged, "candidate generation must not mutate shared launch state");

        AssertEx.True(resolver.TryCommitAdmissionAllocation(second, out var committedLow));
        AssertEx.Equal(second, committedLow);
        AssertEx.True(resolver.TryCommitAdmissionAllocation(first, out var committedAfterStaleHigh));
        AssertEx.Equal(second, committedAfterStaleHigh, "a stale higher candidate must not overwrite a committed lower tier");

        var persisted = AssertEx.NotNull(await resolver.ResolveAsync(Model,
            ModelRole.Chat,
            GpuVariant.Cuda,
            ResolvedLaunchArguments.Explore(),
            CancellationToken.None));
        AssertEx.Equal(second, persisted);

        AssertEx.True(resolver.TryDownTierAfterOutOfMemory(second, out var firstOom));
        AssertEx.True(resolver.TryDownTierAfterOutOfMemory(firstOom, out var secondOom));
        AssertEx.False(resolver.TryDownTierAfterOutOfMemory(secondOom, out _));
    }

    [Test]
    public async Task AdmissionDownTier_NoFitWalkLeavesCachedAllocationUnchanged()
    {
        var resolver = BuildResolver(Profile(64 * Gb, 32 * Gb, vramKnown: true), processBudget: 32 * Gb);
        var automatic = AssertEx.NotNull(await resolver.ResolveAsync(Model,
            ModelRole.Chat,
            GpuVariant.Cuda,
            ResolvedLaunchArguments.Explore(),
            CancellationToken.None));
        var candidate = automatic;
        while (resolver.TryDownTierForAdmission(candidate, out var lower))
        {
            candidate = lower;
        }

        var unchanged = AssertEx.NotNull(await resolver.ResolveAsync(Model,
            ModelRole.Chat,
            GpuVariant.Cuda,
            ResolvedLaunchArguments.Explore(),
            CancellationToken.None));
        AssertEx.Equal(automatic, unchanged);
    }

    [Test]
    public async Task OomDownTier_StaleHigherCallerContinuesFromCommittedLowerAllocation()
    {
        var resolver = BuildResolver(Profile(64 * Gb, 32 * Gb, vramKnown: true), processBudget: 32 * Gb);
        var automatic = AssertEx.NotNull(await resolver.ResolveAsync(Model,
            ModelRole.Chat,
            GpuVariant.Cuda,
            ResolvedLaunchArguments.Explore(),
            CancellationToken.None));
        var candidate = automatic;
        while (candidate.ProcessContextTokens > 8192
               && resolver.TryDownTierForAdmission(candidate, out var lower))
        {
            candidate = lower;
        }

        AssertEx.Equal(expected: 8192, candidate.ProcessContextTokens);
        AssertEx.True(resolver.TryCommitAdmissionAllocation(candidate, out _));
        AssertEx.True(resolver.TryDownTierAfterOutOfMemory(automatic, out var first));
        AssertEx.Equal(expected: 4096, first.ProcessContextTokens);
        AssertEx.True(resolver.TryDownTierAfterOutOfMemory(automatic, out var second));
        AssertEx.Equal(expected: 2048, second.ProcessContextTokens);
        AssertEx.False(resolver.TryDownTierAfterOutOfMemory(automatic, out _));
    }

    [Test]
    public async Task AdmissionDownTier_RejectsFrozenAndDeterministicAllocations()
    {
        var frozenResolver = BuildResolver(Profile(64 * Gb, 32 * Gb, vramKnown: true), processBudget: 32 * Gb);
        var frozen = AssertEx.NotNull(await frozenResolver.ResolveAsync(Model,
            ModelRole.Chat,
            GpuVariant.Cuda,
            ResolvedLaunchArguments.Replay(ctxSize: 65536),
            CancellationToken.None));
        AssertEx.False(frozenResolver.TryDownTierForAdmission(frozen, out _));

        var deterministicResolver = BuildResolver(Profile(64 * Gb, 32 * Gb, vramKnown: true),
            processBudget: 32 * Gb,
            options: new LlamaServerLaunchPolicyOptions
            {
                DeterministicContextTokensOverride = 65536
            });
        var deterministic = AssertEx.NotNull(await deterministicResolver.ResolveAsync(Model,
            ModelRole.Chat,
            GpuVariant.Cuda,
            ResolvedLaunchArguments.Explore(),
            CancellationToken.None));
        AssertEx.False(deterministicResolver.TryDownTierForAdmission(deterministic, out _));

        var auxiliary = AssertEx.NotNull(await frozenResolver.ResolveAsync(Model,
            ModelRole.Embedding,
            GpuVariant.Cuda,
            ResolvedLaunchArguments.Explore(),
            CancellationToken.None));
        AssertEx.False(frozenResolver.TryDownTierForAdmission(auxiliary, out _));
    }

    private static ProcessContextAllocationResolver BuildResolver(HardwareProfile profile,
        long? processBudget = null,
        GgufModelFootprintFacts? facts = null,
        LlamaServerLaunchPolicyOptions? options = null,
        IRuntimeDeviceAudit? audit = null)
    {
        var store = Substitute.For<IGgufModelStore>();
        store.ResolveModelFootprintFactsAsync(Model, Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<GgufModelFootprintFacts?>(facts ?? Facts()));

        if (audit is null)
        {
            audit = Substitute.For<IRuntimeDeviceAudit>();
            audit.GetEffectiveProfileAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult(profile));
        }

        var probe = Substitute.For<IProcessVramBudgetProbe>();
        probe.TryGetProcessBudgetBytesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult(processBudget));

        return new ProcessContextAllocationResolver(store,
            audit,
            probe,
            new MemoryFitEstimator(),
            options ?? new LlamaServerLaunchPolicyOptions());
    }

    private static GgufModelFootprintFacts Facts(string quant = "Q4_K_M",
        long contextLength = 131072,
        long? expertCount = null,
        long? expertUsedCount = null,
        long fileSizeBytes = 5 * Gb,
        long? paramCount = 8_000_000_000,
        long? blockCount = 32,
        long? attentionHeadCount = 32,
        long? attentionHeadCountKv = 8,
        long? embeddingLength = 4096,
        long? attentionKeyLengthMla = null,
        long? attentionValueLengthMla = null) =>
        new(quant,
            FileSizeBytes: fileSizeBytes,
            ParamCount: paramCount,
            BlockCount: blockCount,
            AttentionHeadCount: attentionHeadCount,
            AttentionHeadCountKV: attentionHeadCountKv,
            EmbeddingLength: embeddingLength,
            ContextLength: contextLength,
            ContentIdentity: "sha256:model",
            Architecture: expertCount is > 0 ? "qwen3moe" : "llama",
            ExpertCount: expertCount,
            ExpertUsedCount: expertUsedCount,
            AttentionKeyLengthMla: attentionKeyLengthMla,
            AttentionValueLengthMla: attentionValueLengthMla);

    // A dense 70B at Q4_K_M: 39.4 GB of weights (36.67 GiB) over 80 layers with GQA (8 kv-heads, head_dim 128). Nothing
    // this size fits a 16 GiB / 32 GiB desktop, which is exactly the point — it exercises the exhausted-tier-walk path.
    private static GgufModelFootprintFacts SeventyBillionParameterFacts() =>
        Facts(paramCount: 70_000_000_000,
            fileSizeBytes: (long)(70_000_000_000 * MemoryFitEstimator.BytesPerWeight("Q4_K_M")),
            blockCount: 80,
            attentionHeadCount: 64,
            attentionHeadCountKv: 8,
            embeddingLength: 8192);

    private static long UsableRamBudget(long total) =>
        Math.Max(0, total - Math.Max(LlamaServerLaunchPolicyOptions.MinimumRamReserveBytes,
            (long)(total * LlamaServerLaunchPolicyOptions.RamReserveFraction)));

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
