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

            // Single-slot serving, the locked design: one in-flight request per (model, role) process. Pinned on EVERY spawn, because the auto default reserves
            // four times the KV cache and starves the weight offload. See wiki 03, "LlamaServerProcessSupervisor — process lifecycle".
            "--parallel",
            "1",

            // Skip the empty-run warmup, which on a large model takes 45-110 s and overruns the readiness budget into a kill-and-respawn loop. The model serves
            // correctly without it: the readiness probe and the first real request warm it naturally.
            "--no-warmup"
        };

        // Context, placement, KV-cache/flash-attention and CPU threads, all emitted from ONE projection of this spawn's launch shape so the vector that reaches the
        // process and the shape a receipt records can never drift apart. Precedence lives in the launch policy that produced the plan; the matrix is in wiki 03.
        var projection = LlamaServerLaunchProjection.From(variant, resolved, plan, key.Role, chatCacheReuse, chatCacheRamMiB);
        AppendContextPlacementAndThreadArgs(args, projection);

        // LoRA adapter: the model file above is the BASE model this adapter was trained against, resolved by the caller, and the adapter is applied on top at load.
        // Role-agnostic on purpose — an adapter changes the weights, not the serving mode, so it belongs on whatever role the merged model would have served.
        if (!string.IsNullOrWhiteSpace(adapterFilePath))
        {
            args.Add("--lora");
            args.Add(adapterFilePath);
        }

        if (key.Role == ModelRole.Chat)
        {
            // Mandatory for tool/function calling — without it llama-server ignores the GGUF tool grammar.
            args.Add("--jinja");

            // Vision model: the projector is what makes llama-server accept image input, and without it an image in the request body is rejected. Present only for
            // a model whose companion was resolved locally (IGgufModelStore.ResolveProjectorFilePathAsync); a text-only model passes null and gets no flag.
            if (!string.IsNullOrWhiteSpace(projectorFilePath))
            {
                args.Add("--mmproj");
                args.Add(projectorFilePath);
            }

            // Prompt-cache prefix reuse, chat role only: a positive window enables the flag and 0 (the upstream default) omits it. A server-launch flag independent
            // of the OpenAI-compat request body, which exposes no cache-prompt field. Why it pays and why only chat: wiki 03, "Per-role launch flags".
            if (chatCacheReuse > 0)
            {
                args.Add("--cache-reuse");
                args.Add(chatCacheReuse.ToString(CultureInfo.InvariantCulture));
            }

            // Host-RAM prompt-cache budget, emitted EXPLICITLY on every chat spawn because the pinned build's implicit default is 8192 MiB and its limit
            // enforcement is known-ineffective on Linux under default overcommit (upstream #22629). 0 disables the cache.
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

            // One-shot forward passes have no prompt state worth caching, so disable the host prompt cache instead of inheriting the upstream default.
            args.Add("--cache-ram");
            args.Add("0");
        }
        else if (key.Role == ModelRole.Reranker)
        {
            // Reranker role, exposed only with --rerank (alias --reranking) plus rank pooling, verified against b9692 and re-confirmed against the pinned b10201
            // help output. MUTUALLY EXCLUSIVE with the embedding branch above, and carrying none of the chat-only flags. See wiki 03, "Per-role launch flags".
            args.Add("--rerank");
            args.Add("--pooling");
            args.Add("rank");
            AppendPooledForwardPassBatchArgs(args, projection);

            // One-shot scoring passes have no prompt state worth caching, so disable the host prompt cache instead of inheriting the upstream default.
            args.Add("--cache-ram");
            args.Add("0");
        }
        else
        {
            // Explicit guard: a ModelRole added later must not silently inherit the reranker flags, so fail loudly and keep the new role's launch args a deliberate
            // decision here rather than an accident of branch order.
            throw new ArgumentOutOfRangeException(nameof(key),
                key.Role,
                $"No llama-server launch arguments are defined for model role '{key.Role}'.");
        }

        var workingDirectory = Path.GetDirectoryName(Path.GetFullPath(executablePath)) ?? Environment.CurrentDirectory;
        return new LlamaServerLaunchSpec { ModelName = key.ModelName, Role = key.Role, ExecutablePath = executablePath, Arguments = args, Port = port, WorkingDirectory = workingDirectory };
    }

    /// <summary>
    ///     Emits the context, placement, KV-cache/flash-attention and CPU-thread args for a spawn from its
    ///     <see cref="LlamaServerLaunchProjection" />.
    /// </summary>
    /// <remarks>
    ///     The projection already encodes the variant and explore/replay matrix, so this is a straight rendering of it
    ///     rather than a second copy of the precedence rules. Two spellings of the same KV vector exist upstream and
    ///     both are kept, <see cref="LlamaServerLaunchProjection.AutoFit" /> telling them apart; CPU carries no
    ///     placement or KV args at all and is the only shape that carries thread counts. See wiki 03.
    /// </remarks>
    private static void AppendContextPlacementAndThreadArgs(List<string> args, LlamaServerLaunchProjection projection)
    {
        // Emitted on BOTH GPU modes: the gauges (KV bytes, slot state, cache-reused tokens) are the only in-app view of what a spawn actually did, and a
        // frozen-profile replay — the steady state on a machine tuned once — would otherwise be the one GPU path exposing none of them.
        if (projection.Metrics)
        {
            args.Add("--metrics");
        }

        if (projection.AutoFit)
        {
            // Let llama.cpp auto-fit choose and print placement: the explicit context is RESPECTED by it, and the KV/FA flags are not placement flags, so auto-fit
            // stays active. Verified against b9692; the pinned b10201 help output confirms auto-fit adjusts only UNSET arguments.
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

        // Every expert to system RAM, emitted only when the admitted allocation placed them there, so this is the flag that MAKES the reserved footprint true
        // rather than an optimization. The projection sets it on explore only, so it can never appear beside a frozen replay's tensor override.
        if (projection.CpuMoe)
        {
            args.Add("--cpu-moe");
        }

        // Matching-type rule and flash-attention invariant, enforced in ResolvedLaunchArguments.Replay and in the launch policy: the fused FA path needs equal
        // K/V types and flash attention on.
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
    ///     <strong>A correctness flag, not a tuning flag:</strong> a pooled forward pass is non-causal, so the whole
    ///     input must sit inside ONE physical micro-batch and llama-server REJECTS — it does not split — any single
    ///     input longer than <c>n_ubatch</c>. Safe by construction, because llama.cpp clamps both values down to the
    ///     effective context, and they compose with auto-fit (verified against the in-app source build, pin b10201).
    ///     Chat is deliberately excluded. The measured failure this prevents: wiki 03, "Per-role launch flags".
    /// </remarks>
    private static void AppendPooledForwardPassBatchArgs(List<string> args, LlamaServerLaunchProjection projection)
    {
        // The projection already mirrors whichever context this spawn emits (the policy's, or the frozen replay's own) and leaves both sizes null for a non-pooled
        // role. A pooled role must be able to embed anything that fits the context it advertises, so the projection pins both sizes to that context.
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
    /// </summary>
    /// <remarks>
    ///     Disabled or default emits nothing. A configured mode is validated first: an unknown mode, or an
    ///     external-draft mode with no draft path, is a deterministic misconfiguration surfaced as a NON-RETRYABLE
    ///     error rather than a server that dies cryptically on launch. The external drafter is never separately
    ///     ledgered, so a non-NVIDIA profile with no free-VRAM reading REJECTS that admission rather than undercount
    ///     it. What each branch emits: wiki 03, "Per-role launch flags and the pooled batch-size rule".
    /// </remarks>
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
