namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Keystone coverage for the profile-driven launch-spec seam: the supervisor no longer forces
///     <c>--n-gpu-layers 999</c>. A GPU spawn with no frozen profile emits <c>--fit on</c> + <c>--metrics</c> (auto-fit);
///     a replay profile emits its explicit <c>-c/-ngl/-ts/-ot</c> (and matched <c>-ctk/-ctv</c> + <c>--flash-attn</c>)
///     verbatim with NO <c>--fit</c> but WITH <c>--metrics</c>, and gets the same one-shot KV-stripped retry an explore
///     spawn gets when its optimized config cannot launch; the CPU variant emits no gpu/fit args at all. Flag names verified against the
///     pinned llama.cpp release <c>b9692</c> (<c>--fit</c>, <c>--metrics</c>, <c>-c</c>, <c>--n-gpu-layers</c>,
///     <c>-ts</c>, <c>-ot</c>, <c>-ctk/-ctv</c>, <c>--flash-attn</c>).
/// </summary>
public sealed class SupervisorLaunchSpecProfileTests
{
    private static readonly LlamaServerProcessSupervisor.ProcessKey ChatKey = new("llama3", ModelRole.Chat);
    private static readonly LlamaServerProcessSupervisor.ProcessKey EmbeddingKey = new("nomic-embed", ModelRole.Embedding);

    [Test]
    public void LaunchSpec_WhenExploreMode_EmitsFitOnAndMetrics_NoExplicitFitArgs()
    {
        var spec = BuildGpuSpec(ResolvedLaunchArguments.Explore());

        AssertEx.Contains(spec.Arguments, "--fit");
        AssertEx.Equal("on", spec.Arguments[IndexOf(spec.Arguments, "--fit") + 1]);
        AssertEx.Contains(spec.Arguments, "--metrics");
        // Any explicit fit-arg disables llama.cpp auto-fit, so explore mode must emit none of them.
        AssertEx.False(spec.Arguments.Contains("-c"), "Explore mode must not emit an explicit -c.");
        AssertEx.False(spec.Arguments.Contains("--n-gpu-layers"), "Explore mode must not emit an explicit -ngl.");
        AssertEx.False(spec.Arguments.Contains("-ts"), "Explore mode must not emit an explicit -ts.");
        AssertEx.False(spec.Arguments.Contains("-ot"), "Explore mode must not emit an explicit -ot.");
        AssertEx.False(spec.Arguments.Contains("999"), "The forced -ngl 999 placement is removed.");
    }

    [Test]
    public void LaunchSpec_WhenReplayProfile_ReplaysArgsVerbatim_NoFit()
    {
        var resolved = ResolvedLaunchArguments.Replay(ctxSize: 8192,
            nGpuLayers: 24,
            tensorSplit: "0.6,0.4",
            overrideTensor: "exps=CPU");

        var spec = BuildGpuSpec(resolved);

        AssertEx.Equal("8192", spec.Arguments[IndexOf(spec.Arguments, "-c") + 1]);
        AssertEx.Equal("24", spec.Arguments[IndexOf(spec.Arguments, "--n-gpu-layers") + 1]);
        AssertEx.Equal("0.6,0.4", spec.Arguments[IndexOf(spec.Arguments, "-ts") + 1]);
        AssertEx.Equal("exps=CPU", spec.Arguments[IndexOf(spec.Arguments, "-ot") + 1]);
        // Replay and auto-fit are mutually exclusive per run — never emit --fit when replaying frozen args.
        AssertEx.False(spec.Arguments.Contains("--fit"), "A replayed profile must not emit --fit.");
        AssertEx.False(spec.Arguments.Contains("999"), "A replayed profile must not carry the old forced -ngl 999.");
    }

    [Test]
    public void LaunchSpec_WhenReplayProfile_StillEmitsMetrics()
    {
        // A frozen-profile replay is the steady state on a tuned machine, so it must expose the same /metrics gauges
        // an explore spawn does — otherwise the machines that have a profile are exactly the ones with no observability.
        var replay = BuildGpuSpec(ResolvedLaunchArguments.Replay(ctxSize: 8192, nGpuLayers: 24));

        AssertEx.Contains(replay.Arguments, "--metrics");
        AssertEx.Equal(expected: 1, replay.Arguments.Count(a => string.Equals(a, "--metrics", StringComparison.Ordinal)));
    }

    [Test]
    public void LaunchSpec_WhenKvQuant_RequiresFlashAttnAndMatchingTypes()
    {
        var resolved = ResolvedLaunchArguments.Replay(ctxSize: 4096,
            nGpuLayers: 32,
            kvTypeK: "q8_0",
            kvTypeV: "q8_0",
            flashAttn: true);

        var spec = BuildGpuSpec(resolved);

        var kvKey = spec.Arguments[IndexOf(spec.Arguments, "-ctk") + 1];
        var kvValue = spec.Arguments[IndexOf(spec.Arguments, "-ctv") + 1];
        AssertEx.Equal("q8_0", kvKey);
        AssertEx.Equal(kvKey, kvValue); // matching-type rule
        AssertEx.Contains(spec.Arguments, "--flash-attn");
        AssertEx.Equal("on", spec.Arguments[IndexOf(spec.Arguments, "--flash-attn") + 1]);
    }

    [Test]
    public async Task LaunchSpec_Replay_RejectsMismatchedKvTypes_AndKvWithoutFlashAttn()
    {
        // Matching-type rule: one KV type without the other is rejected.
        await AssertEx.ThrowsAsync<ArgumentException>(() =>
        {
            _ = ResolvedLaunchArguments.Replay(ctxSize: 4096, kvTypeK: "q8_0", flashAttn: true);
            return Task.CompletedTask;
        });

        // Flash-attention invariant: quantized/explicit KV requires --flash-attn.
        await AssertEx.ThrowsAsync<ArgumentException>(() =>
        {
            _ = ResolvedLaunchArguments.Replay(ctxSize: 4096, kvTypeK: "q8_0", kvTypeV: "q8_0", flashAttn: false);
            return Task.CompletedTask;
        });

        // Matching-type rule, tightened: both-set is not enough — the fused path needs the SAME type on K and V, and an
        // asymmetric pair otherwise reaches the launch line as two conflicting -ctk/-ctv values.
        await AssertEx.ThrowsAsync<ArgumentException>(() =>
        {
            _ = ResolvedLaunchArguments.Replay(ctxSize: 4096, kvTypeK: "q8_0", kvTypeV: "q4_0", flashAttn: true);
            return Task.CompletedTask;
        });
    }

    [Test]
    public void LaunchSpec_AlwaysEmitsSingleSlotAndNoWarmup()
    {
        // Single-slot serving (locked design) + skip the empty-run warmup so a large model becomes ready before the
        // readiness budget elapses (otherwise it tree-kills + respawns). Both apply to every spawn regardless of mode.
        var explore = BuildGpuSpec(ResolvedLaunchArguments.Explore());
        AssertEx.Equal("1", explore.Arguments[IndexOf(explore.Arguments, "--parallel") + 1]);
        AssertEx.Contains(explore.Arguments, "--no-warmup");

        var cpu = LlamaServerLaunchArgumentComposer.BuildLaunchSpec(ChatKey,
            "/fake/bin/llama-server",
            "/fake/models/model.gguf",
            port: 8080,
            GpuVariant.Cpu,
            ResolvedLaunchArguments.Explore(),
            chatCacheReuse: 256);
        AssertEx.Equal("1", cpu.Arguments[IndexOf(cpu.Arguments, "--parallel") + 1]);
        AssertEx.Contains(cpu.Arguments, "--no-warmup");
    }

    [Test]
    public void LaunchSpec_WhenCpuVariant_EmitsNoGpuOrFitArgs()
    {
        // Even with a replay profile, the CPU variant stays a pure CPU run: no gpu/fit args at all.
        var resolved = ResolvedLaunchArguments.Replay(ctxSize: 8192, nGpuLayers: 24);
        var spec = LlamaServerLaunchArgumentComposer.BuildLaunchSpec(ChatKey,
            "/fake/bin/llama-server",
            "/fake/models/model.gguf",
            port: 8080,
            GpuVariant.Cpu,
            resolved,
            chatCacheReuse: 256);

        AssertEx.False(spec.Arguments.Contains("--fit"), "CPU must not emit --fit.");
        AssertEx.False(spec.Arguments.Contains("--metrics"), "CPU must not emit --metrics.");
        AssertEx.False(spec.Arguments.Contains("--n-gpu-layers"), "CPU must not emit -ngl.");
        AssertEx.False(spec.Arguments.Contains("-c"), "CPU must not emit the replay -c (gpu/fit block is GPU-only).");
        AssertEx.Contains(spec.Arguments, "--jinja"); // mandatory chat flag stays
    }

    [Test]
    public void LaunchSpec_WhenChatRole_EmitsCacheReuse_WithConfiguredWindow()
    {
        // Chat gets --cache-reuse N (prompt-prefix KV reuse) regardless of profile source; emitted in both explore
        // and replay modes so a frozen profile still benefits from prefix reuse without being part of its identity.
        var explore = BuildGpuSpec(ResolvedLaunchArguments.Explore(), chatCacheReuse: 256);
        AssertEx.Equal("256", explore.Arguments[IndexOf(explore.Arguments, "--cache-reuse") + 1]);

        var replay = BuildGpuSpec(ResolvedLaunchArguments.Replay(ctxSize: 8192, nGpuLayers: 24), chatCacheReuse: 256);
        AssertEx.Equal("256", replay.Arguments[IndexOf(replay.Arguments, "--cache-reuse") + 1]);
    }

    [Test]
    public void LaunchSpec_WhenChatCacheReuseZero_OmitsCacheReuse()
    {
        // 0 is the upstream default (disabled): the flag must be absent entirely, not "--cache-reuse 0".
        var spec = BuildGpuSpec(ResolvedLaunchArguments.Explore(), chatCacheReuse: 0);
        AssertEx.False(spec.Arguments.Contains("--cache-reuse"), "chatCacheReuse=0 must omit --cache-reuse entirely.");
    }

    [Test]
    public void LaunchSpec_WhenEmbeddingRole_NeverEmitsCacheReuse()
    {
        // Embedding servers do one-shot forward passes with no shared conversational prefix — cache-reuse is
        // meaningless there and must never be emitted, even with a positive window configured.
        var spec = LlamaServerLaunchArgumentComposer.BuildLaunchSpec(EmbeddingKey,
            "/fake/bin/llama-server",
            "/fake/models/embed.gguf",
            port: 8080,
            GpuVariant.Cuda,
            ResolvedLaunchArguments.Explore(),
            chatCacheReuse: 256);

        AssertEx.False(spec.Arguments.Contains("--cache-reuse"), "Embedding role must never emit --cache-reuse.");
        AssertEx.False(spec.Arguments.Contains("--jinja"), "Embedding role must not emit --jinja.");
        AssertEx.Contains(spec.Arguments, "--embeddings");
    }

    [Test]
    public void LaunchSpec_WhenNgramSpeculative_EmitsSpecTypeOnly_NoDraftFlags()
    {
        // ngram-* modes self-speculate from context — only --spec-type, never a draft-model/n-max/ngl flag.
        var spec = BuildGpuSpec(ResolvedLaunchArguments.Explore(),
            speculative: new SpeculativeDecodingSettings("ngram-mod", DraftModelPath: null, DraftMaxTokens: 0, DraftGpuLayers: null));

        AssertEx.Equal("ngram-mod", spec.Arguments[IndexOf(spec.Arguments, "--spec-type") + 1]);
        AssertEx.False(spec.Arguments.Contains("--spec-draft-model"), "ngram modes must not emit --spec-draft-model.");
        AssertEx.False(spec.Arguments.Contains("--spec-draft-n-max"), "ngram modes must not emit --spec-draft-n-max.");
        AssertEx.False(spec.Arguments.Contains("--spec-draft-ngl"), "ngram modes must not emit --spec-draft-ngl.");
    }

    [Test]
    public void LaunchSpec_WhenDraftSpeculative_EmitsTypeModelAndNMax()
    {
        // draft-* modes run a second GGUF: --spec-type + --spec-draft-model + --spec-draft-n-max (+ -ngl when set).
        var spec = BuildGpuSpec(ResolvedLaunchArguments.Explore(),
            speculative: new SpeculativeDecodingSettings("draft-simple", DraftModelPath: "/fake/models/draft.gguf", DraftMaxTokens: 3, DraftGpuLayers: 16));

        AssertEx.Equal("draft-simple", spec.Arguments[IndexOf(spec.Arguments, "--spec-type") + 1]);
        AssertEx.Equal("/fake/models/draft.gguf", spec.Arguments[IndexOf(spec.Arguments, "--spec-draft-model") + 1]);
        AssertEx.Equal("3", spec.Arguments[IndexOf(spec.Arguments, "--spec-draft-n-max") + 1]);
        AssertEx.Equal("16", spec.Arguments[IndexOf(spec.Arguments, "--spec-draft-ngl") + 1]);
    }

    [Test]
    public void LaunchSpec_WhenDraftMaxTokensZero_OmitsNMax()
    {
        // 0 is the "omit" sentinel: the flag must be absent, not "--spec-draft-n-max 0".
        var spec = BuildGpuSpec(ResolvedLaunchArguments.Explore(),
            speculative: new SpeculativeDecodingSettings("draft-simple", DraftModelPath: "/fake/models/draft.gguf", DraftMaxTokens: 0, DraftGpuLayers: null));

        AssertEx.Contains(spec.Arguments, "--spec-draft-model");
        AssertEx.False(spec.Arguments.Contains("--spec-draft-n-max"), "DraftMaxTokens=0 must omit --spec-draft-n-max.");
        AssertEx.False(spec.Arguments.Contains("--spec-draft-ngl"), "null DraftGpuLayers must omit --spec-draft-ngl.");
    }

    [Test]
    public void LaunchSpec_WhenMtpSpeculative_EmitsTypeAndNMax_ButNeverADraftModel()
    {
        // draft-mtp drafts from MTP heads in the MAIN model GGUF — there is no second model, so --spec-draft-model must
        // never appear (nor --spec-draft-ngl, which sizes a draft-model load that never happens). --spec-draft-n-max IS
        // honoured by MTP in the pinned build, so it stays.
        var spec = BuildGpuSpec(ResolvedLaunchArguments.Explore(),
            speculative: new SpeculativeDecodingSettings("draft-mtp", DraftModelPath: null, DraftMaxTokens: 4, DraftGpuLayers: 16));

        AssertEx.Equal("draft-mtp", spec.Arguments[IndexOf(spec.Arguments, "--spec-type") + 1]);
        AssertEx.Equal("4", spec.Arguments[IndexOf(spec.Arguments, "--spec-draft-n-max") + 1]);
        AssertEx.False(spec.Arguments.Contains("--spec-draft-model"), "draft-mtp must never emit --spec-draft-model.");
        AssertEx.False(spec.Arguments.Contains("--spec-draft-ngl"), "draft-mtp has no draft model to offload.");
    }

    [Test]
    public void LaunchSpec_WhenMtpSpeculativeWithDraftPath_IgnoresIt()
    {
        // Settings saved before the contract was corrected were REQUIRED to carry a draft model for draft-mtp. Such a
        // path is ignored, not rejected, so those installs keep launching — and it must not leak into the args.
        var spec = BuildGpuSpec(ResolvedLaunchArguments.Explore(),
            speculative: new SpeculativeDecodingSettings("draft-mtp", DraftModelPath: "/fake/models/draft.gguf", DraftMaxTokens: 3, DraftGpuLayers: null));

        AssertEx.Equal("draft-mtp", spec.Arguments[IndexOf(spec.Arguments, "--spec-type") + 1]);
        AssertEx.False(spec.Arguments.Contains("--spec-draft-model"), "A draft path configured for draft-mtp must be ignored.");
        AssertEx.False(spec.Arguments.Contains("/fake/models/draft.gguf"), "The ignored draft path must not reach the launch args.");
    }

    [Test]
    public void SpeculativeSettings_MtpValidatesWithoutADraftPath_AndIsNotAnExternalDraftMode()
    {
        // The classification the whole contract hangs on: draft-mtp is MainModelHeads, so no boundary — validator, save
        // endpoint, or launch — may demand a draft model for it, while draft-simple still must.
        var mtp = new SpeculativeDecodingSettings("draft-mtp", DraftModelPath: null, DraftMaxTokens: 3, DraftGpuLayers: null);
        AssertEx.True(mtp.TryValidate(out var mtpError), "draft-mtp must validate with no draft model path.");
        AssertEx.Null(mtpError);
        AssertEx.False(mtp.RequiresExternalDraftModel, "draft-mtp drafts from the main model, not a second GGUF.");
        AssertEx.Equal<SpeculativeModeClass?>(SpeculativeModeClass.MainModelHeads, mtp.ModeClass);
        AssertEx.False(SpeculativeDecodingSettings.ModeRequiresDraftModel("draft-mtp"),
            "The settings boundary must not require a draft model for draft-mtp.");

        var external = new SpeculativeDecodingSettings("draft-simple", DraftModelPath: null, DraftMaxTokens: 3, DraftGpuLayers: null);
        AssertEx.False(external.TryValidate(out _), "draft-simple still requires a draft model path.");
        AssertEx.True(SpeculativeDecodingSettings.ModeRequiresDraftModel("DRAFT-SIMPLE"), "Classification is case-insensitive.");
        AssertEx.Equal<SpeculativeModeClass?>(SpeculativeModeClass.ExternalDraft, external.ModeClass);

        // Draftless and unknown modes keep their existing answers.
        AssertEx.Equal<SpeculativeModeClass?>(SpeculativeModeClass.Draftless,
            new SpeculativeDecodingSettings("ngram-mod", DraftModelPath: null, DraftMaxTokens: 0, DraftGpuLayers: null).ModeClass);
        AssertEx.Null(SpeculativeDecodingSettings.ClassOf("draft-bogus"));
        AssertEx.False(SpeculativeDecodingSettings.IsAllowedMode("draft-bogus"));

        // draft-dflash / draft-dspark are exposed as external-draft modes; SpeculativeDecodingSettingsTests owns that pin.
    }

    [Test]
    public void LaunchSpec_WhenSpeculativeDisabled_EmitsNoSpecFlags()
    {
        // Default (none) is the ship-off state: no --spec-* flag anywhere.
        var spec = BuildGpuSpec(ResolvedLaunchArguments.Explore());
        AssertEx.False(spec.Arguments.Contains("--spec-type"), "Disabled speculative must omit --spec-type.");

        var explicitNone = BuildGpuSpec(ResolvedLaunchArguments.Explore(), speculative: SpeculativeDecodingSettings.Disabled);
        AssertEx.False(explicitNone.Arguments.Contains("--spec-type"), "mode=none must omit --spec-type.");
    }

    [Test]
    public void LaunchSpec_WhenEmbeddingRole_NeverEmitsSpecFlags()
    {
        // Speculative decoding is chat-only; an embedding server must never carry --spec-* even if a mode is configured.
        var spec = LlamaServerLaunchArgumentComposer.BuildLaunchSpec(EmbeddingKey,
            "/fake/bin/llama-server",
            "/fake/models/embed.gguf",
            port: 8080,
            GpuVariant.Cuda,
            ResolvedLaunchArguments.Explore(),
            chatCacheReuse: 256,
            speculative: new SpeculativeDecodingSettings("draft-simple", DraftModelPath: "/fake/models/draft.gguf", DraftMaxTokens: 3, DraftGpuLayers: null));

        AssertEx.False(spec.Arguments.Contains("--spec-type"), "Embedding role must never emit --spec-type.");
        AssertEx.False(spec.Arguments.Contains("--spec-draft-model"), "Embedding role must never emit --spec-draft-model.");
    }

    [Test]
    public void BenchmarkLaunchPolicy_IgnoresLiveCacheAndSpeculativeOptions()
    {
        var liveOptions = new LlamaServerSupervisorOptions
        {
            ChatCacheReuse = 777,
            ChatCacheRamMiB = 4096,
            SpeculativeMode = "draft-simple",
            SpeculativeDraftModelPath = "/fake/models/live-draft.gguf",
            SpeculativeDraftMaxTokens = 5,
            SpeculativeDraftGpuLayers = 12
        };

        var benchmark = LlamaServerLaunchArgumentComposer.ResolveChatLaunchTuning(LlamaServerBenchmarkLaunchPolicy.DeterministicV1,
            liveOptions);
        var spec = BuildGpuSpec(ResolvedLaunchArguments.Replay(ctxSize: 4096, nGpuLayers: 24),
            benchmark.ChatCacheReuse,
            benchmark.Speculative,
            benchmark.ChatCacheRamMiB);

        AssertEx.False(spec.Arguments.Contains("--cache-reuse"),
            "The frozen benchmark policy must not inherit live prompt-prefix cache reuse.");
        AssertEx.Equal("0", spec.Arguments[IndexOf(spec.Arguments, "--cache-ram") + 1]);
        AssertEx.False(spec.Arguments.Any(argument => argument.StartsWith("--spec-", StringComparison.Ordinal)),
            "The frozen benchmark policy must not inherit live speculative decoding or its draft model.");

        var ordinary = LlamaServerLaunchArgumentComposer.ResolveChatLaunchTuning(benchmarkPolicy: null, liveOptions);
        AssertEx.Equal(777, ordinary.ChatCacheReuse);
        AssertEx.Equal(4096, ordinary.ChatCacheRamMiB);
        AssertEx.Equal("draft-simple", ordinary.Speculative.Mode);
        AssertEx.Equal("/fake/models/live-draft.gguf", ordinary.Speculative.DraftModelPath);
    }

    [Test]
    public async Task LaunchSpec_WhenDraftModeMissingPath_FailsValidation()
    {
        // A draft-* mode with no draft model path is a deterministic misconfig — reject at build time, don't spawn.
        await AssertEx.ThrowsAsync<LlamaRuntimeException>(() =>
        {
            _ = BuildGpuSpec(ResolvedLaunchArguments.Explore(),
                speculative: new SpeculativeDecodingSettings("draft-eagle3", DraftModelPath: null, DraftMaxTokens: 3, DraftGpuLayers: null));
            return Task.CompletedTask;
        });
    }

    [Test]
    public async Task LaunchSpec_WhenUnknownSpecMode_FailsValidation()
    {
        // An unrecognized --spec-type value is a config error surfaced clearly, not passed through to the server.
        await AssertEx.ThrowsAsync<LlamaRuntimeException>(() =>
        {
            _ = BuildGpuSpec(ResolvedLaunchArguments.Explore(),
                speculative: new SpeculativeDecodingSettings("draft-bogus", DraftModelPath: "/fake/models/draft.gguf", DraftMaxTokens: 3, DraftGpuLayers: null));
            return Task.CompletedTask;
        });
    }

    [Test]
    public async Task SpawnPath_AwaitsResolver_AndAppliesReplayArgs()
    {
        var launcher = new FakeProcessLauncher();
        var resolver = new FakeInferenceProfileResolver(ResolvedLaunchArguments.Replay(ctxSize: 4096, nGpuLayers: 20));
        await using var supervisor = SupervisorFactory.Create(launcher,
            variantSelector: new FakeVariantSelector(GpuVariant.Cuda),
            profileResolver: resolver);

        await supervisor.EnsureRunningAsync("llama3", ModelRole.Chat, CancellationToken.None);

        // The supervisor awaited the resolver for this (model, role, backend) on the spawn path.
        AssertEx.True(resolver.Calls.TryDequeue(out var call));
        AssertEx.Equal("llama3", call.ModelName);
        AssertEx.Equal(ModelRole.Chat, call.Role);
        AssertEx.Equal(GpuVariant.Cuda, call.Backend);

        // And threaded the resolved replay args into the launched spec (no auto-fit, no forced 999).
        AssertEx.True(launcher.Launches.TryDequeue(out var spec));
        AssertEx.Equal("4096", spec!.Arguments[IndexOf(spec.Arguments, "-c") + 1]);
        AssertEx.Equal("20", spec.Arguments[IndexOf(spec.Arguments, "--n-gpu-layers") + 1]);
        AssertEx.False(spec.Arguments.Contains("--fit"), "Replay spawn must not emit --fit.");
        AssertEx.False(spec.Arguments.Contains("999"), "Replay spawn must not carry the old forced -ngl 999.");
    }

    [Test]
    public async Task SpawnPath_ResolvesDraftModelNameToPath_AndEmitsIt()
    {
        // A draft-* mode stores the draft model by NAME; the supervisor must resolve it to an on-disk GGUF (the same way
        // the target model is resolved) and emit that resolved path as --spec-draft-model. A real temp file is used so
        // the spawn-path File.Exists guard passes.
        var draftFile = Path.GetTempFileName();
        try
        {
            var launcher = new FakeProcessLauncher();
            var options = new LlamaServerSupervisorOptions
            {
                IdleTimeToLive = TimeSpan.FromHours(1),
                MaxLoadedProcesses = 3,
                SpeculativeMode = "draft-simple",
                SpeculativeDraftModelName = "my-draft"
            };
            await using var supervisor = SupervisorFactory.Create(launcher,
                modelStore: new FakeModelStore(fixedPath: draftFile),
                variantSelector: new FakeVariantSelector(GpuVariant.Cuda),
                options: options);

            await supervisor.EnsureRunningAsync("llama3", ModelRole.Chat, CancellationToken.None);

            AssertEx.True(launcher.Launches.TryDequeue(out var spec));
            AssertEx.Equal("draft-simple", spec!.Arguments[IndexOf(spec.Arguments, "--spec-type") + 1]);
            AssertEx.Equal(draftFile, spec.Arguments[IndexOf(spec.Arguments, "--spec-draft-model") + 1]);
        }
        finally
        {
            File.Delete(draftFile);
        }
    }

    [Test]
    public async Task SpawnPath_WhenMtpMode_LaunchesWithoutADraftFile_EvenWhenOneIsConfigured()
    {
        // draft-mtp needs no draft GGUF, so the spawn path must neither resolve one nor hard-fail on a missing file.
        // A leftover draft-model NAME (settings saved when draft-* still demanded one) is ignored, not fatal — the fake
        // store resolves to a path that does not exist, which the old contract turned into a non-retryable launch error.
        var launcher = new FakeProcessLauncher();
        var options = new LlamaServerSupervisorOptions
        {
            IdleTimeToLive = TimeSpan.FromHours(1),
            MaxLoadedProcesses = 3,
            SpeculativeMode = "draft-mtp",
            SpeculativeDraftModelName = "my-draft"
        };
        await using var supervisor = SupervisorFactory.Create(launcher,
            variantSelector: new FakeVariantSelector(GpuVariant.Cuda),
            options: options);

        await supervisor.EnsureRunningAsync("llama3", ModelRole.Chat, CancellationToken.None);

        AssertEx.True(launcher.Launches.TryDequeue(out var spec));
        AssertEx.Equal("draft-mtp", spec!.Arguments[IndexOf(spec.Arguments, "--spec-type") + 1]);
        AssertEx.False(spec.Arguments.Contains("--spec-draft-model"), "draft-mtp must never emit --spec-draft-model.");
    }

    [Test]
    public async Task SpawnPath_WhenQuantizedKvReplayFailsFirstLaunch_RetriesOnceWithKvStripped()
    {
        var launches = 0;
        // Fail the FIRST launch only. MaxRestartAttempts=1 removes the outer restart loop, so a second launch can only
        // come from an in-spawn fallback candidate.
        var launcher = new FakeProcessLauncher(_ => Interlocked.Increment(ref launches) == 1
            ? throw new InvalidOperationException("simulated launch failure")
            : new FakeProcessHandle(pid: 4242));
        var fallbackStore = new FakeLaunchFallbackStore();
        await using var supervisor = SupervisorFactory.Create(launcher,
            options: SingleAttemptOptions,
            variantSelector: new FakeVariantSelector(GpuVariant.Cuda),
            profileResolver: new FakeInferenceProfileResolver(ResolvedLaunchArguments.Replay(ctxSize: 4096,
                nGpuLayers: 32,
                kvTypeK: "q8_0",
                kvTypeV: "q8_0",
                flashAttn: true)),
            launchFallbackStore: fallbackStore);

        await supervisor.EnsureRunningAsync("llama3", ModelRole.Chat, CancellationToken.None);

        AssertEx.Equal(expected: 2, launcher.LaunchCount);
        AssertEx.True(launcher.Launches.TryDequeue(out var optimized));
        AssertEx.Contains(optimized!.Arguments, "-ctk", "the first replay attempt emits the frozen KV-cache quant.");
        AssertEx.True(launcher.Launches.TryDequeue(out var safe));
        AssertEx.False(safe!.Arguments.Contains("-ctk"), "the safe retry strips the frozen KV-cache quant.");
        AssertEx.False(safe.Arguments.Contains("-ctv"), "the safe retry strips both KV-cache types (they are coupled).");
        AssertEx.False(safe.Arguments.Contains("--flash-attn"), "the safe retry drops flash attention with the KV quant.");
        // Placement is untouched — only the KV config is the suspect.
        AssertEx.Equal("4096", safe.Arguments[IndexOf(safe.Arguments, "-c") + 1]);
        AssertEx.Equal("32", safe.Arguments[IndexOf(safe.Arguments, "--n-gpu-layers") + 1]);

        // Same store the explore-mode fallback uses: the backend, not the model, is the culprit.
        AssertEx.True(await fallbackStore.IsOptimizedConfigDisabledAsync(GpuVariant.Cuda, LlamaServerKvCacheTypes.Q8_0, CancellationToken.None),
            "a successful safe retry must record the optimized-config fallback for the backend.");
    }

    [Test]
    public async Task SpawnPath_WhenUnquantizedReplayFailsLaunch_HasNoSecondCandidate()
    {
        var launcher = new FakeProcessLauncher(_ => throw new InvalidOperationException("simulated launch failure"));
        await using var supervisor = SupervisorFactory.Create(launcher,
            options: SingleAttemptOptions,
            variantSelector: new FakeVariantSelector(GpuVariant.Cuda),
            profileResolver: new FakeInferenceProfileResolver(ResolvedLaunchArguments.Replay(ctxSize: 4096, nGpuLayers: 32)));

        await AssertEx.ThrowsAsync<LlamaRuntimeException>(() => supervisor.EnsureRunningAsync("llama3", ModelRole.Chat, CancellationToken.None));

        // Nothing to strip means nothing to retry: a replay with no frozen KV quant gets exactly one launch attempt.
        AssertEx.Equal(expected: 1, launcher.LaunchCount);
    }

    /// <summary>Options with the outer restart loop reduced to a single attempt, so every launch is an in-spawn candidate.</summary>
    private static LlamaServerSupervisorOptions SingleAttemptOptions =>
        new()
        {
            MaxRestartAttempts = 1,
            IdleTimeToLive = TimeSpan.FromHours(1)
        };

    private static LlamaServerLaunchSpec BuildGpuSpec(ResolvedLaunchArguments resolved,
        int chatCacheReuse = 256,
        SpeculativeDecodingSettings speculative = default,
        int chatCacheRamMiB = 0)
    {
        return LlamaServerLaunchArgumentComposer.BuildLaunchSpec(ChatKey,
            "/fake/bin/llama-server",
            "/fake/models/model.gguf",
            port: 8080,
            GpuVariant.Cuda,
            resolved,
            chatCacheReuse,
            speculative,
            chatCacheRamMiB: chatCacheRamMiB);
    }

    private static int IndexOf(IReadOnlyList<string> args, string flag)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (string.Equals(args[i], flag, StringComparison.Ordinal))
            {
                return i;
            }
        }

        throw new AssertionException($"Expected flag '{flag}' in argument vector.");
    }
}
