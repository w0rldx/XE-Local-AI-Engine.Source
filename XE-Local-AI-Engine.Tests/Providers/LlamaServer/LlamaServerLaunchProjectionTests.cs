namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using System.Text.Json;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Pins the single canonical <see cref="LlamaServerLaunchProjection" /> the launch-spec builder now emits from.
///     Two things must hold and are asserted as whole argument vectors rather than spot-checks, because a projection
///     that renders ALMOST the right flags is worse than none: every launch mode (GPU explore, GPU replay, CPU replay,
///     pooled role) still produces byte-identical argv, and the projection's identity is stable for equal inputs and
///     moves for any differing field.
/// </summary>
public sealed class LlamaServerLaunchProjectionTests
{
    private const string ExecutablePath = "/fake/bin/llama-server";
    private const string ModelFilePath = "/fake/models/model.gguf";
    private const int Port = 8080;

    private static readonly LlamaServerProcessSupervisor.ProcessKey ChatKey = new("llama3", ModelRole.Chat);
    private static readonly LlamaServerProcessSupervisor.ProcessKey EmbeddingKey = new("nomic-embed", ModelRole.Embedding);

    [Test]
    public void ArgumentVector_GpuExploreWithPolicyPlan_IsUnchanged()
    {
        var plan = new LlamaServerLaunchPlan(RequestedContextTokens: 16384,
            UseKvCacheQuantization: true,
            "q8_0",
            CpuThreads: null,
            CpuThreadsBatch: null);

        var spec = LlamaServerLaunchArgumentComposer.BuildLaunchSpec(ChatKey,
            ExecutablePath,
            ModelFilePath,
            Port,
            GpuVariant.Cuda,
            ResolvedLaunchArguments.Explore(),
            chatCacheReuse: 256,
            plan: plan,
            chatCacheRamMiB: 512);

        AssertVector(spec,
        [
            "-m", ModelFilePath, "--host", "127.0.0.1", "--port", "8080", "--parallel", "1", "--no-warmup",
            "--metrics", "--fit", "on", "-c", "16384", "-fa", "on", "-ctk", "q8_0", "-ctv", "q8_0",
            "--jinja", "--cache-reuse", "256", "--cache-ram", "512"
        ]);
    }

    [Test]
    public void ArgumentVector_KvTypeF16_EmitsNoKvOrFlashArgs()
    {
        // Selecting f16 collapses to EnableGpuKvCacheQuantization = false in the DI seed, so the plan carries
        // UseKvCacheQuantization: false and the vector loses -fa/-ctk/-ctv entirely — the same KV segment (none) a CPU
        // spawn emits today.
        var plan = new LlamaServerLaunchPlan(RequestedContextTokens: 16384,
            UseKvCacheQuantization: false,
            LlamaServerKvCacheTypes.F16,
            CpuThreads: null,
            CpuThreadsBatch: null);

        var spec = LlamaServerLaunchArgumentComposer.BuildLaunchSpec(ChatKey,
            ExecutablePath,
            ModelFilePath,
            Port,
            GpuVariant.Cuda,
            ResolvedLaunchArguments.Explore(),
            chatCacheReuse: 256,
            plan: plan,
            chatCacheRamMiB: 512);

        AssertVector(spec,
        [
            "-m", ModelFilePath, "--host", "127.0.0.1", "--port", "8080", "--parallel", "1", "--no-warmup",
            "--metrics", "--fit", "on", "-c", "16384",
            "--jinja", "--cache-reuse", "256", "--cache-ram", "512"
        ]);
        AssertEx.False(spec.Arguments.Contains("-ctk"), "f16 must emit no -ctk.");
        AssertEx.False(spec.Arguments.Contains("-ctv"), "f16 must emit no -ctv.");
        AssertEx.False(spec.Arguments.Contains("-fa"), "f16 must leave flash attention at the runtime default.");
    }

    [Test]
    public void ArgumentVector_KvTypeQ4_EmitsCtkCtvQ4()
    {
        // What the explore spawn following a q4_0-induced stale mark actually launches with.
        var plan = new LlamaServerLaunchPlan(RequestedContextTokens: 16384,
            UseKvCacheQuantization: true,
            LlamaServerKvCacheTypes.Q4_0,
            CpuThreads: null,
            CpuThreadsBatch: null);

        var spec = LlamaServerLaunchArgumentComposer.BuildLaunchSpec(ChatKey,
            ExecutablePath,
            ModelFilePath,
            Port,
            GpuVariant.Cuda,
            ResolvedLaunchArguments.Explore(),
            chatCacheReuse: 256,
            plan: plan,
            chatCacheRamMiB: 512);

        AssertVector(spec,
        [
            "-m", ModelFilePath, "--host", "127.0.0.1", "--port", "8080", "--parallel", "1", "--no-warmup",
            "--metrics", "--fit", "on", "-c", "16384", "-fa", "on", "-ctk", "q4_0", "-ctv", "q4_0",
            "--jinja", "--cache-reuse", "256", "--cache-ram", "512"
        ]);
    }

    [Test]
    public void ArgumentVector_GpuReplayWithNoPlan_IsUnchanged()
    {
        // The benchmark/replay-profiling shape: policy bypassed entirely, frozen args emitted verbatim. This is the
        // vector the CPU fix must NOT perturb.
        var resolved = ResolvedLaunchArguments.Replay(ctxSize: 8192,
            nGpuLayers: 24,
            tensorSplit: "0.6,0.4",
            overrideTensor: "exps=CPU",
            kvTypeK: "q8_0",
            kvTypeV: "q8_0",
            flashAttn: true);

        var spec = LlamaServerLaunchArgumentComposer.BuildLaunchSpec(ChatKey,
            ExecutablePath,
            ModelFilePath,
            Port,
            GpuVariant.Cuda,
            resolved,
            chatCacheReuse: 0);

        AssertVector(spec,
        [
            "-m", ModelFilePath, "--host", "127.0.0.1", "--port", "8080", "--parallel", "1", "--no-warmup",
            "--metrics", "-c", "8192", "--n-gpu-layers", "24", "-ts", "0.6,0.4", "-ot", "exps=CPU",
            "-ctk", "q8_0", "-ctv", "q8_0", "--flash-attn", "on",
            "--jinja", "--cache-ram", "0"
        ]);
    }

    [Test]
    public void ArgumentVector_CpuReplayWithCpuReplayPlan_CarriesContextAndThreads()
    {
        // The pre-existing gap this closes: a CPU spawn emits none of a GPU profile's replay args, so with no plan it
        // previously emitted NO -c and NO thread counts and ran at llama.cpp's own defaults.
        var policy = NewPolicy(cpuThreads: 6, cpuThreadsBatch: 8);
        var resolved = ResolvedLaunchArguments.Replay(ctxSize: 4096, nGpuLayers: 24);

        var spec = LlamaServerLaunchArgumentComposer.BuildLaunchSpec(ChatKey,
            ExecutablePath,
            ModelFilePath,
            Port,
            GpuVariant.Cpu,
            resolved,
            chatCacheReuse: 0,
            plan: policy.ResolveCpuReplayPlan(resolved));

        AssertVector(spec,
        [
            "-m", ModelFilePath, "--host", "127.0.0.1", "--port", "8080", "--parallel", "1", "--no-warmup",
            "-c", "4096", "-t", "6", "-tb", "8",
            "--jinja", "--cache-ram", "0"
        ]);

        // A CPU build never inherits GPU placement, auto-fit or the KV vector from a frozen GPU profile.
        AssertEx.False(spec.Arguments.Contains("--n-gpu-layers"), "A CPU spawn must not replay GPU placement.");
        AssertEx.False(spec.Arguments.Contains("--fit"), "A CPU spawn must not auto-fit.");
        AssertEx.False(spec.Arguments.Contains("--metrics"), "A CPU spawn exposes no /metrics.");
        AssertEx.False(spec.Arguments.Contains("-ctk"), "A CPU spawn keeps the f16 KV default.");
    }

    [Test]
    public void ArgumentVector_PooledRole_StillSizesBatchToTheEmittedContext()
    {
        var plan = new LlamaServerLaunchPlan(RequestedContextTokens: 2048,
            UseKvCacheQuantization: false,
            "q8_0",
            CpuThreads: null,
            CpuThreadsBatch: null);

        var spec = LlamaServerLaunchArgumentComposer.BuildLaunchSpec(EmbeddingKey,
            ExecutablePath,
            ModelFilePath,
            Port,
            GpuVariant.Cuda,
            ResolvedLaunchArguments.Explore(),
            chatCacheReuse: 256,
            plan: plan);

        AssertVector(spec,
        [
            "-m", ModelFilePath, "--host", "127.0.0.1", "--port", "8080", "--parallel", "1", "--no-warmup",
            "--metrics", "--fit", "on", "-c", "2048",
            "--embeddings", "--pooling", "mean", "-b", "2048", "-ub", "2048", "--cache-ram", "0"
        ]);
    }

    [Test]
    public void ArgumentVector_PooledCpuReplayWithNoPlan_KeepsTheFrozenContextBatch()
    {
        // A pooled role sizes -b/-ub off whichever context the spawn emits, falling back to the frozen replay's own
        // when no plan supplies one. That fallback is load-bearing for input length and must survive the refactor.
        var spec = LlamaServerLaunchArgumentComposer.BuildLaunchSpec(EmbeddingKey,
            ExecutablePath,
            ModelFilePath,
            Port,
            GpuVariant.Cpu,
            ResolvedLaunchArguments.Replay(ctxSize: 3072),
            chatCacheReuse: 0);

        AssertVector(spec,
        [
            "-m", ModelFilePath, "--host", "127.0.0.1", "--port", "8080", "--parallel", "1", "--no-warmup",
            "--embeddings", "--pooling", "mean", "-b", "3072", "-ub", "3072", "--cache-ram", "0"
        ]);
    }

    [Test]
    public void From_GpuReplay_ProjectsTheFrozenVectorAndPinsFlashAttentionOn()
    {
        var resolved = ResolvedLaunchArguments.Replay(ctxSize: 8192,
            nGpuLayers: 24,
            tensorSplit: "0.6,0.4",
            overrideTensor: "exps=CPU",
            kvTypeK: "q4_0",
            kvTypeV: "q4_0",
            flashAttn: true);

        var projection = LlamaServerLaunchProjection.From(GpuVariant.Cuda, resolved, plan: null);

        AssertEx.False(projection.AutoFit);
        AssertEx.True(projection.Metrics);
        AssertEx.Equal<int?>(expected: 8192, projection.ContextTokens);
        AssertEx.Equal<int?>(expected: 24, projection.GpuLayers);
        AssertEx.Equal("0.6,0.4", projection.TensorSplit);
        AssertEx.Equal("exps=CPU", projection.OverrideTensor);
        AssertEx.Equal("q4_0", projection.KvCacheTypeK);
        AssertEx.Equal("q4_0", projection.KvCacheTypeV);
        AssertEx.Equal(LlamaServerLaunchProjection.FlashAttentionOn, projection.FlashAttentionMode);
        AssertEx.Null(projection.Threads);
        AssertEx.Equal(expected: 1, projection.Parallel);
        AssertEx.True(projection.Jinja);
        AssertEx.Null(projection.Pooling);
    }

    [Test]
    public void From_WithoutExplicitKvTypes_RecordsFlashAttentionAsAuto()
    {
        // f16 KV emits no -ctk/-ctv and no -fa at all, so the honest record of the flash-attention decision is "auto"
        // (llama.cpp chose), never "off" (which would claim a flag nobody passed).
        var projection = LlamaServerLaunchProjection.From(GpuVariant.Cuda,
            ResolvedLaunchArguments.Replay(ctxSize: 8192, nGpuLayers: 24),
            plan: null);

        AssertEx.Null(projection.KvCacheTypeK);
        AssertEx.Null(projection.KvCacheTypeV);
        AssertEx.Equal(LlamaServerLaunchProjection.FlashAttentionAuto, projection.FlashAttentionMode);
    }

    [Test]
    public void ComputeIdentity_IsStableForEqualInputs_AndMovesWhenTheKvTypeChanges()
    {
        var f16 = ResolvedLaunchArguments.Replay(ctxSize: 8192, nGpuLayers: 24);
        var q8 = ResolvedLaunchArguments.Replay(ctxSize: 8192, nGpuLayers: 24, kvTypeK: "q8_0", kvTypeV: "q8_0", flashAttn: true);
        var q4 = ResolvedLaunchArguments.Replay(ctxSize: 8192, nGpuLayers: 24, kvTypeK: "q4_0", kvTypeV: "q4_0", flashAttn: true);

        var first = LlamaServerLaunchProjection.From(GpuVariant.Cuda, q8, plan: null).ComputeIdentity();
        var second = LlamaServerLaunchProjection.From(GpuVariant.Cuda, q8, plan: null).ComputeIdentity();

        AssertEx.Equal(first, second);
        AssertEx.Equal(expected: 64, first.Length);
        AssertEx.NotEqual(first, LlamaServerLaunchProjection.From(GpuVariant.Cuda, q4, plan: null).ComputeIdentity());
        AssertEx.NotEqual(first, LlamaServerLaunchProjection.From(GpuVariant.Cuda, f16, plan: null).ComputeIdentity());
    }

    [Test]
    public void ComputeIdentity_IsSensitiveToEveryProjectedField()
    {
        // The identity is only useful if it moves for anything that changes the launch. Walk one field at a time off a
        // baseline and assert each mutation produces a distinct hash.
        var baseline = FullyPopulatedProjection();

        LlamaServerLaunchProjection[] mutations =
        [
            baseline with
            {
                AutoFit = true
            },
            baseline with
            {
                Metrics = false
            },
            baseline with
            {
                ContextTokens = 8193
            },
            baseline with
            {
                GpuLayers = 25
            },
            baseline with
            {
                TensorSplit = "0.5,0.5"
            },
            baseline with
            {
                CpuMoe = false
            },
            baseline with
            {
                OverrideTensor = "exps=GPU"
            },
            baseline with
            {
                KvCacheTypeK = "q4_0"
            },
            baseline with
            {
                KvCacheTypeV = "q4_0"
            },
            baseline with
            {
                FlashAttentionMode = LlamaServerLaunchProjection.FlashAttentionAuto
            },
            baseline with
            {
                Threads = 7
            },
            baseline with
            {
                ThreadsBatch = 9
            },
            baseline with
            {
                BatchSize = 4096
            },
            baseline with
            {
                UbatchSize = 4096
            },
            baseline with
            {
                Parallel = 2
            },
            baseline with
            {
                CacheReuse = null
            },
            baseline with
            {
                CacheRamMiB = 0
            },
            baseline with
            {
                Jinja = false
            },
            baseline with
            {
                Pooling = "rank"
            }
        ];

        var identities = new HashSet<string>(StringComparer.Ordinal)
        {
            baseline.ComputeIdentity()
        };

        foreach (var mutation in mutations)
        {
            AssertEx.True(identities.Add(mutation.ComputeIdentity()),
                $"A mutated projection reused an existing identity: {mutation}");
        }

        AssertEx.Equal(mutations.Length + 1, identities.Count);
    }

    [Test]
    public void Projection_CarriesNoPathHostOrPort()
    {
        var plan = new LlamaServerLaunchPlan(RequestedContextTokens: 16384,
            UseKvCacheQuantization: true,
            "q8_0",
            CpuThreads: null,
            CpuThreadsBatch: null);
        var projection = LlamaServerLaunchProjection.From(GpuVariant.Cuda,
            ResolvedLaunchArguments.Explore(),
            plan,
            ModelRole.Chat,
            chatCacheReuse: 256,
            chatCacheRamMiB: 512);

        var serialized = JsonSerializer.Serialize(projection);

        foreach (var forbidden in new[]
                 {
                     ModelFilePath,
                     ExecutablePath,
                     "/fake",
                     "127.0.0.1",
                     "8080"
                 })
        {
            AssertEx.False(serialized.Contains(forbidden, StringComparison.Ordinal),
                $"The launch projection leaked '{forbidden}': {serialized}");
        }
    }

    [Test]
    public void TryFromArguments_ForAnEmittedVector_RoundTripsToTheProjectionItWasRenderedFrom()
    {
        // The whole point of reading the argv back: for a vector nothing perturbed, the effective projection must be
        // identical to the intended one — otherwise every receipt would report a spurious difference.
        var plan = new LlamaServerLaunchPlan(RequestedContextTokens: 16384,
            UseKvCacheQuantization: true,
            "q8_0",
            CpuThreads: null,
            CpuThreadsBatch: null);
        var intended = LlamaServerLaunchProjection.From(GpuVariant.Cuda,
            ResolvedLaunchArguments.Explore(),
            plan,
            ModelRole.Chat,
            chatCacheReuse: 256,
            chatCacheRamMiB: 512);
        var spec = LlamaServerLaunchArgumentComposer.BuildLaunchSpec(ChatKey,
            ExecutablePath,
            ModelFilePath,
            Port,
            GpuVariant.Cuda,
            ResolvedLaunchArguments.Explore(),
            chatCacheReuse: 256,
            plan: plan,
            chatCacheRamMiB: 512);

        var effective = AssertEx.NotNull(LlamaServerLaunchProjection.TryFromArguments(spec.Arguments));

        AssertEx.Equal(intended, effective);
        AssertEx.Equal(intended.ComputeIdentity(), effective.ComputeIdentity());
    }

    [Test]
    [Arguments(GpuVariant.Cuda)]
    [Arguments(GpuVariant.Cpu)]
    public void TryFromArguments_ForAPooledReplayVector_MatchesTheIntendedProjection(GpuVariant variant)
    {
        var resolved = ResolvedLaunchArguments.Replay(ctxSize: 3072, nGpuLayers: 24);
        var plan = variant == GpuVariant.Cpu
            ? NewPolicy(cpuThreads: 6, cpuThreadsBatch: 8).ResolveCpuReplayPlan(resolved)
            : (LlamaServerLaunchPlan?)null;
        var spec = LlamaServerLaunchArgumentComposer.BuildLaunchSpec(EmbeddingKey,
            ExecutablePath,
            ModelFilePath,
            Port,
            variant,
            resolved,
            chatCacheReuse: 0,
            plan: plan);

        AssertEx.Equal(LlamaServerLaunchProjection.From(variant, resolved, plan, ModelRole.Embedding),
            AssertEx.NotNull(LlamaServerLaunchProjection.TryFromArguments(spec.Arguments)));
    }

    [Test]
    public void TryFromArguments_WhenAFlagTheProjectionClaimsWasDropped_ReportsTheVectorNotTheIntent()
    {
        // The capability gate removes optional options a runtime does not advertise, AFTER the projection that rendered
        // them. Re-reading the argv is what stops a receipt from claiming a flag the process never received.
        var plan = new LlamaServerLaunchPlan(RequestedContextTokens: 16384,
            UseKvCacheQuantization: false,
            "q8_0",
            CpuThreads: null,
            CpuThreadsBatch: null);
        var intended = LlamaServerLaunchProjection.From(GpuVariant.Cuda,
            ResolvedLaunchArguments.Explore(),
            plan,
            ModelRole.Chat,
            chatCacheReuse: 256,
            chatCacheRamMiB: 512);
        var spec = LlamaServerLaunchArgumentComposer.BuildLaunchSpec(ChatKey,
            ExecutablePath,
            ModelFilePath,
            Port,
            GpuVariant.Cuda,
            ResolvedLaunchArguments.Explore(),
            chatCacheReuse: 256,
            plan: plan,
            chatCacheRamMiB: 512);
        var gated = spec.Arguments
                        .Where(static argument => !string.Equals(argument, "--metrics", StringComparison.Ordinal))
                        .ToArray();

        var effective = AssertEx.NotNull(LlamaServerLaunchProjection.TryFromArguments(gated));

        AssertEx.True(intended.Metrics);
        AssertEx.False(effective.Metrics, "A dropped --metrics must read as absent, not as the intent that rendered it.");
        AssertEx.NotEqual(intended.ComputeIdentity(), effective.ComputeIdentity());
    }

    [Test]
    public void CpuMoe_RoundTripsThroughTheArgumentVector()
    {
        // --cpu-moe is a valueless flag like --jinja/--metrics, so the effective reading must recover it and its
        // absence must read as absent rather than as the intent that rendered it.
        var withFlag = AssertEx.NotNull(LlamaServerLaunchProjection.TryFromArguments([
            "-m", ModelFilePath, "--parallel", "1", "--fit", "on", "--cpu-moe", "-c", "8192"
        ]));
        var withoutFlag = AssertEx.NotNull(LlamaServerLaunchProjection.TryFromArguments([
            "-m", ModelFilePath, "--parallel", "1", "--fit", "on", "-c", "8192"
        ]));

        AssertEx.True(withFlag.CpuMoe);
        AssertEx.False(withoutFlag.CpuMoe);
        AssertEx.NotEqual(withFlag.ComputeIdentity(), withoutFlag.ComputeIdentity());
    }

    [Test]
    public void From_GpuExploreWithAnExpertOffloadPlan_ProjectsCpuMoe_AndAReplayNeverDoes()
    {
        var offloadPlan = new LlamaServerLaunchPlan(RequestedContextTokens: 8192,
            UseKvCacheQuantization: true,
            LlamaServerKvCacheTypes.Q8_0,
            CpuThreads: null,
            CpuThreadsBatch: null,
            CpuMoe: true);

        var explore = LlamaServerLaunchProjection.From(GpuVariant.Cuda, ResolvedLaunchArguments.Explore(), offloadPlan);
        var replay = LlamaServerLaunchProjection.From(GpuVariant.Cuda,
            ResolvedLaunchArguments.Replay(ctxSize: 8192, overrideTensor: "exps=CPU"),
            offloadPlan);

        AssertEx.True(explore.CpuMoe);
        AssertEx.False(replay.CpuMoe, "a frozen replay owns its own placement; --cpu-moe must never join its -ot.");
    }

    [Test]
    public void TryFromArguments_IsTolerantOfUnknownArgumentsAndLastWinsForARepeatedFlag()
    {
        // Operator extra arguments are appended after everything else and llama.cpp is last-wins for a scalar flag, so
        // the honest reading of the vector is the last value — and an argument this projection does not model is skipped.
        var effective = AssertEx.NotNull(LlamaServerLaunchProjection.TryFromArguments([
            "-m", ModelFilePath, "--host", "127.0.0.1", "--port", "8080", "--parallel", "1", "--no-warmup",
            "--metrics", "-c", "8192", "--jinja", "--cache-ram", "0", "-lv", "4", "-c", "4096"
        ]));

        AssertEx.Equal<int?>(expected: 4096, effective.ContextTokens);
        AssertEx.True(effective.Metrics);
        AssertEx.True(effective.Jinja);
        AssertEx.Equal(expected: 1, effective.Parallel);
        AssertEx.Null(effective.Pooling);
    }

    [Test]
    [Arguments("-c")]
    [Arguments("--cache-ram")]
    public void TryFromArguments_ForAnUnreadableVector_IsNullRatherThanAWrongFact(string flag)
    {
        AssertEx.Null(LlamaServerLaunchProjection.TryFromArguments(["--metrics", flag, "not-a-number"]),
            "A value that cannot be read must degrade to no projection, never to a fabricated one.");
        AssertEx.Null(LlamaServerLaunchProjection.TryFromArguments(["--metrics", flag]),
            "A trailing flag with no value must degrade to no projection.");
        AssertEx.Null(LlamaServerLaunchProjection.TryFromArguments(["-fa", "--jinja"]),
            "A value flag followed by another flag must not swallow the flag as its value.");
    }

    [Test]
    public void ResolveCpuReplayPlan_ForExploreArguments_RequestsNoContext()
    {
        // Explore pins no context of its own, so there is nothing for a policy-free CPU spawn to carry over — and a
        // -c 0 would be a launch failure, not a default.
        var plan = NewPolicy(cpuThreads: 6, cpuThreadsBatch: 8).ResolveCpuReplayPlan(ResolvedLaunchArguments.Explore());

        AssertEx.Null(plan.RequestedContextTokens);
        AssertEx.Equal<int?>(expected: 6, plan.CpuThreads);
        AssertEx.Equal<int?>(expected: 8, plan.CpuThreadsBatch);
        AssertEx.False(plan.UseKvCacheQuantization, "The CPU replay plan never touches the KV vector.");
    }

    [Test]
    public void ResolveCpuReplayPlan_WithThreadPolicyDisabled_StillCarriesTheFrozenContext()
    {
        var policy = new LlamaServerLaunchPolicy(new LlamaServerLaunchPolicyOptions
            {
                EnableCpuThreadPolicy = false
            },
            new FakeLaunchFallbackStore());

        var plan = policy.ResolveCpuReplayPlan(ResolvedLaunchArguments.Replay(ctxSize: 4096));

        AssertEx.Equal<int?>(expected: 4096, plan.RequestedContextTokens);
        AssertEx.Null(plan.CpuThreads);
        AssertEx.Null(plan.CpuThreadsBatch);
    }

    [Test]
    public void ComputeIdentity_ForAFixedProjection_IsTheHashItHasAlwaysProduced()
    {
        // Identities are persisted alongside benchmark runs, so reordering, renaming or adding a projected member would
        // silently invalidate every stored one while every other test here still passed. This literal is the tripwire.
        // RE-BASELINED when CpuMoe joined the record (identity scheme 1 -> 2). The scheme-1 literal was
        // f642c972396108f8129c6a01812775f43f15047eca05248886986f6c52b737b4; work frozen under it is failed at cutover
        // rather than compared, and LlamaServerLaunchReceipt.CurrentVersion moved 1 -> 2 in the same change so a
        // pre-slice and a post-slice identity are DISTINGUISHABLE rather than merely unequal.
        AssertEx.Equal("3d17edf6e09739e9ad0a56b5b238e604ec11f49954ca9bcfd4cccf535081ef4f",
            FullyPopulatedProjection().ComputeIdentity());
        AssertEx.Equal(expected: 2, LlamaServerLaunchProjection.IdentitySchemeVersion);
    }

    private static LlamaServerLaunchProjection FullyPopulatedProjection()
    {
        return new LlamaServerLaunchProjection(AutoFit: false,
            Metrics: true,
            ContextTokens: 8192,
            GpuLayers: 24,
            TensorSplit: "0.6,0.4",
            OverrideTensor: "exps=CPU",
            CpuMoe: true,
            KvCacheTypeK: "q8_0",
            KvCacheTypeV: "q8_0",
            LlamaServerLaunchProjection.FlashAttentionOn,
            Threads: 6,
            ThreadsBatch: 8,
            BatchSize: 2048,
            UbatchSize: 2048,
            Parallel: 1,
            CacheReuse: 256,
            CacheRamMiB: 512,
            Jinja: true,
            Pooling: "mean");
    }

    private static LlamaServerLaunchPolicy NewPolicy(int cpuThreads, int cpuThreadsBatch)
    {
        // Pin the thread counts explicitly: the derived counts read Environment.ProcessorCount, which differs per box.
        return new LlamaServerLaunchPolicy(new LlamaServerLaunchPolicyOptions
            {
                CpuThreadCount = cpuThreads,
                CpuThreadsBatchCount = cpuThreadsBatch
            },
            new FakeLaunchFallbackStore());
    }

    private static void AssertVector(LlamaServerLaunchSpec spec, IReadOnlyList<string> expected)
    {
        AssertEx.Equal(string.Join(' ', expected), string.Join(' ', spec.Arguments));
    }
}
