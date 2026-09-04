namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using System.Globalization;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;

/// <summary>
///     Renders the <c>llama-server</c> command line. Pure functions over a spawn's
///     <see cref="LlamaServerLaunchProjection" /> and <see cref="LlamaServerLaunchPlan" />: no process, port or
///     supervisor state is read here, so the argument vector a spawn receives is reproducible from its inputs alone.
/// </summary>
internal static class LlamaServerLaunchArgumentComposer
{
    /// <summary>
    ///     Builds the exact, ordered llama-server argument vector for a <c>(model, role)</c> on a port.
    ///     <paramref name="chatCacheReuse" /> is the chat-role <c>--cache-reuse</c> window
    ///     (<see cref="LlamaServerSupervisorOptions.ChatCacheReuse" />); <c>0</c> omits the flag.
    ///     <paramref name="speculative" /> is the chat-role speculative-decoding config
    ///     (<see cref="LlamaServerSupervisorOptions.Speculative" />); disabled/default emits no <c>--spec-*</c> flags.
    /// </summary>
    internal static LlamaServerLaunchSpec BuildLaunchSpec(LlamaServerProcessSupervisor.ProcessKey key,
        string executablePath,
        string modelFilePath,
        int port,
        GpuVariant variant,
        ResolvedLaunchArguments resolved,
        int chatCacheReuse,
        SpeculativeDecodingSettings speculative = default,
        LlamaServerLaunchPlan? plan = null,
        int chatCacheRamMiB = 0,
        string? projectorFilePath = null,
        string? adapterFilePath = null)
    {
        var args = new List<string>
        {
            "-m",
            modelFilePath,
            "--host",
            "127.0.0.1", // localhost-only bind
            "--port",
            port.ToString(CultureInfo.InvariantCulture),

            // Single-slot serving (the locked design — one in-flight request per (model, role) process). Pinning
            // --parallel 1 stops llama-server from auto-selecting n_parallel=4, which reserves 4x the KV cache and
            // starves --fit's weight offload: with the auto default, fit spills weights to system RAM to make room for
            // KV slots that are never used, so a model that would fit on the GPU runs slow on the CPU instead.
            "--parallel",
            "1",

            // Skip the empty-run warmup. On a large model it can take 45-110s and overrun the readiness budget, which
            // tree-kills the half-ready process and respawns it in a loop (observed as a chat inter-chunk stall and an
            // explore "did not become ready in time"). The model serves correctly without it — the readiness probe and
            // the first real request warm it naturally — so dropping it makes startup fast and reliable at any size.
            "--no-warmup"
        };

        // Context (-c), placement, KV-cache/flash-attention, and CPU threads. The variant selects the llama.cpp BUILD
        // (Cuda/Vulkan vs pure CPU). Precedence lives in the launch policy that produced `plan`; here we just emit:
        //  - GPU explore: --fit on (auto-fit places layers/experts around the explicit -c), plus the policy -c and the
        //    KV-quant + flash-attention optimization; GPU replay: the frozen profile args verbatim. Both carry --metrics.
        //  - CPU: the policy -c (explore) or the frozen -c (replay), plus the CPU thread policy; NO --fit/--metrics/-ngl,
        //    KV stays f16 and flash-attention stays auto.
        // A null plan (replay profiling) reproduces the supplied replay vector byte-for-byte.
        //
        // Everything below is emitted from ONE projection of this spawn's launch shape, so the vector that goes to the
        // process and the shape a receipt records can never drift apart into two independent derivations.
        var projection = LlamaServerLaunchProjection.From(variant, resolved, plan, key.Role, chatCacheReuse, chatCacheRamMiB);
        AppendContextPlacementAndThreadArgs(args, projection);

        // LoRA adapter. `-m` above is the BASE model this adapter was trained against (resolved by the caller); the
        // adapter is applied on top at load. Role-agnostic on purpose: an adapter changes the weights, not the serving
        // mode, so it belongs on whatever role the merged model would have served.
        if (!string.IsNullOrWhiteSpace(adapterFilePath))
        {
            args.Add("--lora");
            args.Add(adapterFilePath);
        }

        if (key.Role == ModelRole.Chat)
        {
            // Mandatory for tool/function calling — without it llama-server ignores the GGUF tool grammar.
            args.Add("--jinja");

            // Vision model: the mmproj projector is what makes llama-server accept image input (OpenAI image_url content
            // parts) — without it an image in the request body is rejected. Present only for a model whose projector
            // companion was resolved locally (see IGgufModelStore.ResolveProjectorFilePathAsync); a text-only model
            // passes null and gets no flag. Chat role only — embedding/reranker servers never take images.
            if (!string.IsNullOrWhiteSpace(projectorFilePath))
            {
                args.Add("--mmproj");
                args.Add(projectorFilePath);
            }

            // Prompt-cache prefix reuse. The app resends the full selected-path history every turn, so cache-reuse
            // lets llama-server reuse the unchanged prompt prefix via KV cache shifting instead of reprocessing it —
            // a large time-to-first-token win on multi-turn chat/agent conversations. A positive window enables the
            // flag; 0 (upstream default) omits it. Chat role only: an embedding server does one-shot forward passes
            // with no shared conversational prefix to reuse, so the flag is meaningless there. This is a server-launch
            // flag independent of the OpenAI-compat request body (which exposes no cache_prompt/n_keep field).
            if (chatCacheReuse > 0)
            {
                args.Add("--cache-reuse");
                args.Add(chatCacheReuse.ToString(CultureInfo.InvariantCulture));
            }

            // Host-RAM prompt-cache budget. Emitted EXPLICITLY on every chat spawn because the pinned build's
            // implicit default is 8192 MiB — half the RAM of a 16 GB machine — and its limit enforcement is
            // known-ineffective on Linux under default overcommit (upstream #22629: the OOM killer fires before
            // std::bad_alloc, SIGKILLing the server past its own eviction). 0 disables the cache.
            args.Add("--cache-ram");
            args.Add(chatCacheRamMiB.ToString(CultureInfo.InvariantCulture));

            AppendSpeculativeArgs(args, speculative);
        }
        else if (key.Role == ModelRole.Embedding)
        {
            // /v1/embeddings is exposed only with --embeddings + a non-`none` pooling type.
            args.Add("--embeddings");
            args.Add("--pooling");
            args.Add("mean");
            AppendPooledForwardPassBatchArgs(args, projection);

            // One-shot forward passes have no prompt state worth caching — disable the host prompt cache instead of
            // inheriting the upstream 8192 MiB default (see the chat branch).
            args.Add("--cache-ram");
            args.Add("0");
        }
        else if (key.Role == ModelRole.Reranker)
        {
            // Reranker role. POST /v1/rerank is exposed only with --rerank (alias --reranking) + `--pooling rank`
            // (verified against b9692, re-confirmed against the pinned b10201 --help). This is MUTUALLY EXCLUSIVE with
            // the embedding branch above —
            // a rerank server scores (query, document) pairs and never gets --embeddings — and carries none of the
            // chat-only flags (--jinja, --cache-reuse, speculative). Because each role is its own branch, a single
            // process can only ever receive one role's flags, so --embeddings and --rerank never coexist.
            args.Add("--rerank");
            args.Add("--pooling");
            args.Add("rank");
            AppendPooledForwardPassBatchArgs(args, projection);

            // One-shot scoring passes have no prompt state worth caching — disable the host prompt cache instead of
            // inheriting the upstream 8192 MiB default (see the chat branch).
            args.Add("--cache-ram");
            args.Add("0");
        }
        else
        {
            // Explicit guard: a ModelRole added later must not silently inherit the reranker flags. Fail loudly so the
            // new role's launch args are a deliberate decision here rather than an accident of the branch order.
            throw new ArgumentOutOfRangeException(nameof(key),
                key.Role,
                $"No llama-server launch arguments are defined for model role '{key.Role}'.");
        }

        var workingDirectory = Path.GetDirectoryName(Path.GetFullPath(executablePath)) ?? Environment.CurrentDirectory;
        return new LlamaServerLaunchSpec(key.ModelName, key.Role, executablePath, args, port, workingDirectory);
    }

    /// <summary>
    ///     Emits the context (<c>-c</c>), placement, KV-cache/flash-attention, and CPU-thread args for a spawn from its
    ///     <see cref="LlamaServerLaunchProjection" />. The projection already encodes the variant + explore/replay
    ///     matrix, so the vector below is a straight rendering of it rather than a second copy of the precedence rules.
    /// </summary>
    /// <remarks>
    ///     Two spellings of the same KV vector exist upstream and both are kept: an auto-fit spawn writes
    ///     <c>-fa on -ctk T -ctv T</c>, a replay writes <c>-ctk K -ctv V --flash-attn on</c>.
    ///     <see cref="LlamaServerLaunchProjection.AutoFit" /> is what tells them apart. CPU carries no
    ///     <c>--metrics</c>/<c>--fit</c>/placement/KV args at all — a frozen GPU profile does not transfer to a CPU
    ///     spawn — and is the only shape that carries thread counts.
    /// </remarks>
    private static void AppendContextPlacementAndThreadArgs(List<string> args, LlamaServerLaunchProjection projection)
    {
        // --metrics on BOTH GPU modes. The /metrics gauges (KV bytes, slot state, cache-reused tokens) are the only
        // in-app view of what a spawn actually did, and a frozen-profile replay — the steady state on a machine that
        // has been tuned once — was previously the one GPU path that exposed none of them.
        if (projection.Metrics)
        {
            args.Add("--metrics");
        }

        if (projection.AutoFit)
        {
            // Let llama.cpp auto-fit choose + print placement. The explicit -c is RESPECTED by --fit, which fits
            // ngl/batch around it, and the KV/FA flags are not placement flags, so auto-fit stays active. Verified
            // against b9692; the pinned b10201 --help confirms --fit adjusts only UNSET arguments.
            args.Add("--fit");
            args.Add("on");
        }

        if (projection.ContextTokens is { } contextTokens)
        {
            args.Add("-c");
            args.Add(contextTokens.ToString(CultureInfo.InvariantCulture));
        }

        if (projection.GpuLayers is { } gpuLayers)
        {
            args.Add("--n-gpu-layers");
            args.Add(gpuLayers.ToString(CultureInfo.InvariantCulture));
        }

        if (projection.TensorSplit is { } tensorSplit)
        {
            args.Add("-ts");
            args.Add(tensorSplit);
        }

        if (projection.OverrideTensor is { } overrideTensor)
        {
            args.Add("-ot");
            args.Add(overrideTensor);
        }

        // Every expert to system RAM. Emitted only when the admitted allocation placed them there, so this is the flag
        // that MAKES the reserved footprint true rather than an optimization. --fit adjusts only UNSET arguments, so
        // auto-fit sizes ngl/batch around it instead of putting the experts back on the GPU; the projection sets it on
        // explore only, so it can never appear beside a frozen replay's -ot.
        if (projection.CpuMoe)
        {
            args.Add("--cpu-moe");
        }

        // Matching-type rule + flash-attention invariant (enforced upstream in ResolvedLaunchArguments.Replay and in the
        // launch policy): the fused FA path needs equal K/V types and flash attention on.
        if (projection.KvCacheTypeK is { } kvCacheTypeK && projection.KvCacheTypeV is { } kvCacheTypeV)
        {
            if (projection.AutoFit)
            {
                args.Add("-fa");
                args.Add("on");
                args.Add("-ctk");
                args.Add(kvCacheTypeK);
                args.Add("-ctv");
                args.Add(kvCacheTypeV);
            }
            else
            {
                args.Add("-ctk");
                args.Add(kvCacheTypeK);
                args.Add("-ctv");
                args.Add(kvCacheTypeV);
                args.Add("--flash-attn");
                args.Add("on");
            }
        }

        if (projection.Threads is { } threads)
        {
            args.Add("-t");
            args.Add(threads.ToString(CultureInfo.InvariantCulture));
        }

        if (projection.ThreadsBatch is { } threadsBatch)
        {
            args.Add("-tb");
            args.Add(threadsBatch.ToString(CultureInfo.InvariantCulture));
        }
    }

    /// <summary>
    ///     Appends the physical/logical batch sizes (<c>-b</c>/<c>-ub</c>) for the POOLED roles (Embedding, Reranker),
    ///     raising them from llama.cpp's 512-token default to this spawn's context size.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <strong>This is a correctness flag, not a tuning flag.</strong> A pooled embedding/rerank forward pass is
    ///         non-causal: the whole input must sit inside ONE physical micro-batch, because pooling has no way to carry
    ///         attention state across <c>n_ubatch</c> boundaries. llama-server therefore rejects — it does not split —
    ///         any single input longer than <c>n_ubatch</c>, with
    ///         <c>500 {"error":{"code":500,"message":"input (N tokens) is too large to process. increase the physical
    ///         batch size (current batch size: 512)"}}</c>.
    ///     </para>
    ///     <para>
    ///         Without this, the usable embedding input was llama.cpp's DEFAULT <c>n_ubatch</c> of <strong>512</strong>
    ///         tokens — NOT the <c>-c</c> we ask for (2048 by default, see
    ///         <c>LlamaServerLaunchPolicyOptions.EmbeddingContextTokens</c>) and NOT the window the model advertises.
    ///         Nothing upstream knew that: the knowledge-base chunker sizes chunks against the model's CONTEXT window,
    ///         so ordinary 2000-character markdown chunks (~520-680 real tokens) exceeded the silent 512 ceiling and
    ///         every knowledge-base document failed to index on a default node. Measured against
    ///         <c>nomic-embed-text-v1.5.Q4_K_M</c>: 11 of 12 consecutive real markdown chunks were rejected at the
    ///         default, 0 of 12 with these flags.
    ///     </para>
    ///     <para>
    ///         Safe by construction: llama.cpp CLAMPS both values down to the effective context, so requesting more than
    ///         the model supports is a no-op rather than an error (verified: <c>-ub 8192</c> against a 2048-window model
    ///         starts and reports <c>n_ctx_slot = 2048</c>). The flags also compose with <c>--fit on</c> — auto-fit sizes
    ///         placement around them rather than overriding them (verified against the in-app source build, pin b10201).
    ///         Chat is deliberately excluded: a causal decode splits across micro-batches correctly, so raising its batch
    ///         is a memory/throughput trade-off rather than a correctness fix, and <c>--fit</c> owns that decision.
    ///     </para>
    /// </remarks>
    private static void AppendPooledForwardPassBatchArgs(List<string> args, LlamaServerLaunchProjection projection)
    {
        // The projection already mirrors whichever -c this spawn emits (the policy context, or the frozen replay's own)
        // and leaves both sizes null for a non-pooled role. A pooled role must be able to embed anything that fits the
        // context it advertises. -b (logical) must be >= -ub (physical); the projection pins both to the same context.
        if (projection.BatchSize is not { } batchSize || projection.UbatchSize is not { } ubatchSize)
        {
            return;
        }

        args.Add("-b");
        args.Add(batchSize.ToString(CultureInfo.InvariantCulture));
        args.Add("-ub");
        args.Add(ubatchSize.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    ///     Appends the chat-role speculative-decoding flags, one branch per <see cref="SpeculativeModeClass" />.
    ///     Disabled/default (<c>none</c>) emits nothing. A configured mode is validated first (unknown mode, or an
    ///     external-draft mode with no draft path, is a deterministic misconfiguration surfaced as a NON-RETRYABLE error
    ///     rather than a server that dies cryptically on launch). Then:
    ///     <list type="bullet">
    ///         <item>
    ///             <see cref="SpeculativeModeClass.Draftless" /> (<c>ngram-*</c>) self-speculates from context: only
    ///             <c>--spec-type</c>.
    ///         </item>
    ///         <item>
    ///             <see cref="SpeculativeModeClass.MainModelHeads" /> (<c>draft-mtp</c>) drafts from MTP heads in the main
    ///             GGUF, so NO <c>--spec-draft-model</c> and no <c>--spec-draft-ngl</c> (that knob sizes a draft-model load
    ///             that never happens). <c>--spec-draft-n-max</c> IS honoured — b10201's <c>common_speculative_n_max</c>
    ///             reads <c>draft.n_max</c> for <c>DRAFT_MTP</c> — so it is still emitted.
    ///         </item>
    ///         <item>
    ///             <see cref="SpeculativeModeClass.ExternalDraft" /> additionally emits <c>--spec-draft-model</c> and
    ///             <c>--spec-draft-ngl</c>. That drafter loads inside the chat process and is never separately ledgered or
    ///             footprint-estimated; on the primary NVIDIA path its resident VRAM is still reflected in
    ///             <c>CapacityService</c>'s free-VRAM baseline (<c>nvidia-smi memory.free</c>) so a later sub-agent
    ///             admission accounts for it, but on the non-NVIDIA total-minus-ledger fallback it stays invisible.
    ///         </item>
    ///     </list>
    /// </summary>
    private static void AppendSpeculativeArgs(List<string> args, in SpeculativeDecodingSettings speculative)
    {
        if (!speculative.IsEnabled)
        {
            return;
        }

        if (!speculative.TryValidate(out var error))
        {
            throw LlamaServerProcessSupervisor.NonRetryable(error!);
        }

        args.Add("--spec-type");
        args.Add(speculative.NormalizedMode);

        if (speculative.ModeClass is SpeculativeModeClass.Draftless)
        {
            return;
        }

        if (speculative.RequiresExternalDraftModel)
        {
            // Validated non-empty above; the file's existence on disk is enforced on the spawn path before launch.
            args.Add("--spec-draft-model");
            args.Add(speculative.DraftModelPath!);
        }

        if (speculative.DraftMaxTokens > 0)
        {
            args.Add("--spec-draft-n-max");
            args.Add(speculative.DraftMaxTokens.ToString(CultureInfo.InvariantCulture));
        }

        if (speculative.RequiresExternalDraftModel && speculative.DraftGpuLayers is { } draftGpuLayers)
        {
            args.Add("--spec-draft-ngl");
            args.Add(draftGpuLayers.ToString(CultureInfo.InvariantCulture));
        }
    }

    /// <summary>A compact, path-free launch-plan summary appended to the spawn log line (empty for a policy-less spawn).</summary>
    internal static string DescribeLaunchPlan(LlamaServerLaunchPlan? plan)
    {
        if (plan is not { } resolvedPlan)
        {
            return string.Empty;
        }

        var parts = new List<string>(capacity: 4);
        if (resolvedPlan.RequestedContextTokens is { } ctx)
        {
            parts.Add($"ctx={ctx.ToString(CultureInfo.InvariantCulture)}");
        }

        if (resolvedPlan.UseKvCacheQuantization)
        {
            parts.Add($"kv={resolvedPlan.KvCacheType}+fa");
        }

        if (resolvedPlan.CpuMoe)
        {
            parts.Add("cpu-moe");
        }

        if (resolvedPlan.CpuThreads is { } threads)
        {
            parts.Add($"threads={threads.ToString(CultureInfo.InvariantCulture)}/{resolvedPlan.CpuThreadsBatch?.ToString(CultureInfo.InvariantCulture) ?? "-"}");
        }

        return parts.Count == 0 ? string.Empty : " [" + string.Join(", ", parts) + "]";
    }

    /// <summary>Whether the argument vector already sets a log verbosity, in which case the caller must not add one.</summary>
    internal static bool HasVerbosityArgument(IReadOnlyList<string> arguments)
    {
        return arguments.Any(static argument =>
            argument is "-v" or "--verbose" or "--log-verbose" or "-lv" or "--verbosity" or "--log-verbosity");
    }

    internal static LlamaServerChatLaunchTuning ResolveChatLaunchTuning(LlamaServerBenchmarkLaunchPolicy? benchmarkPolicy,
        LlamaServerSupervisorOptions liveOptions)
    {
        ArgumentNullException.ThrowIfNull(liveOptions);
        if (benchmarkPolicy is null)
        {
            return new LlamaServerChatLaunchTuning(liveOptions.ChatCacheReuse, liveOptions.ChatCacheRamMiB, liveOptions.Speculative);
        }

        if (!benchmarkPolicy.IsSupported)
        {
            throw new ArgumentException("The frozen benchmark launch policy is unsupported.", nameof(benchmarkPolicy));
        }

        return new LlamaServerChatLaunchTuning(benchmarkPolicy.ChatCacheReuse,
            benchmarkPolicy.ChatCacheRamMiB,
            SpeculativeDecodingSettings.Disabled);
    }
}

internal readonly record struct LlamaServerChatLaunchTuning(
    int ChatCacheReuse,
    int ChatCacheRamMiB,
    SpeculativeDecodingSettings Speculative);
