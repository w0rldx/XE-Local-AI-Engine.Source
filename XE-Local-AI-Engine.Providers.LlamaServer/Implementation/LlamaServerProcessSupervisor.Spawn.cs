namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;

/// <summary>
///     Spawn half of <see cref="LlamaServerProcessSupervisor" />: the restart-with-backoff loop, the single launch
///     attempt that resolves the binary, builds the launch plan and starts the child, the readiness wait, and the
///     load-telemetry and layer-placement bookkeeping recorded from a successful start.
/// </summary>
public sealed partial class LlamaServerProcessSupervisor
{
    /// <summary>
    ///     Spawns the process for <paramref name="key" />, retrying on a failed start up to the restart cap with a
    ///     linear backoff; exceeding the cap surfaces a sanitized <see cref="LlamaRuntimeException" />.
    /// </summary>
    private async Task<RunningProcess> SpawnWithRestartAsync(ProcessKey key,
        ProcessLaunchAdmission? admission,
        CancellationToken ct)
    {
        Exception? lastError = null;
        var readinessTimeoutRetries = 0;
        for (var attempt = 0; attempt < _options.MaxRestartAttempts; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(RestartBackoffStep * attempt, _timeProvider, ct).ConfigureAwait(false);
            }

            try
            {
                return await SpawnOnceAsync(key, admission, ct).ConfigureAwait(false);
            }
            catch (LlamaRuntimeException ex) when (ex.Data.Contains(NonRetryableMarker))
            {
                // Deterministic failures (cap reached, model not installed, no free port, crash-on-load) are policy
                // outcomes, not transient crashes — surface them as-is instead of burning retries on a guaranteed re-failure.
                _logger.LogError(ex, "llama-server start failed for model {ModelName} role {Role}: {Reason}",
                    key.ModelName, key.Role, ex.Message);
                throw;
            }
            catch (LlamaRuntimeException ex) when (ex.Data.Contains(ReadinessTimeoutMarker))
            {
                // A readiness TIMEOUT (process alive but slow to load) is not a transient crash: retrying it many times
                // just multiplies the kill/reload thrash (the audited ~6 min stall). Retry it at most
                // MaxReadinessTimeoutRetries times — independent of MaxRestartAttempts — then surface the classified
                // "did not become ready" failure.
                lastError = ex;
                readinessTimeoutRetries++;
                _logger.LogWarning(ex, "llama-server readiness timed out for model {ModelName} role {Role} (readiness-timeout attempt {ReadinessAttempt}; {MaxReadinessRetries} retry(ies) allowed).",
                    key.ModelName, key.Role, readinessTimeoutRetries, _options.MaxReadinessTimeoutRetries);

                if (readinessTimeoutRetries > _options.MaxReadinessTimeoutRetries)
                {
                    _logger.LogError(ex, "llama-server did not become ready for model {ModelName} role {Role} after {ReadinessAttempts} readiness attempt(s); not retrying further.",
                        key.ModelName, key.Role, readinessTimeoutRetries);
                    throw;
                }
            }
            catch (GpuModelLoadAdmissionTimeoutException ex)
            {
                // A bounded GPU-load admission wait elapsed (another model load did not become ready in time). It is not
                // a transient crash, so surface its sanitized message immediately rather than burning restart attempts
                // re-queuing behind a still-contended gate.
                _logger.LogError(ex, "llama-server spawn for model {ModelName} role {Role} could not acquire GPU-load admission in time.",
                    key.ModelName, key.Role);
                throw;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        // Retries exhausted: surface (and now log) the LAST underlying cause, which the sanitized wrapper otherwise hides.
        _logger.LogError(lastError, "llama-server failed to start for model {ModelName} role {Role} after {Attempts} attempt(s).",
            key.ModelName, key.Role, _options.MaxRestartAttempts);
        throw new LlamaRuntimeException("The local model runtime failed to start after several attempts. Check available memory and try again.",
            lastError ?? new InvalidOperationException("Spawn failed."));
    }

    /// <summary>
    ///     One normal spawn attempt: admit under the cap, allocate a port, launch, health-probe, register. The launch
    ///     args come from the profile resolver (frozen-profile replay or explore-mode auto-fit) for this
    ///     <c>(model, role, backend)</c>.
    /// </summary>
    private Task<RunningProcess> SpawnOnceAsync(ProcessKey key,
        ProcessLaunchAdmission? admission,
        CancellationToken ct)
    {
        // The resolver is awaited inside the core (after variant selection, before admission) exactly as before — a
        // slow profile read never stalls admission for other keys. No startup capture, no forced --metrics. The launch
        // policy (deterministic -c, GPU KV/FA, CPU threads) applies to this normal serving path.
        return SpawnCoreAsync(key,
            (variant, c) => _profileResolver.ResolveAsync(key.ModelName, key.Role, variant, c),
            startupCapture: null,
            fitParamsCapture: null,
            ensureMetrics: false,
            applyLaunchPolicy: true,
            admission,
            ct);
    }

    /// <summary>
    ///     Shared spawn core for both the resolver-driven normal path and the explicit-args operator profiling path:
    ///     resolve the model file + variant + binary, obtain the launch args via <paramref name="resolveArgs" /> BEFORE
    ///     taking the admission gate, then admit under the cap, allocate a port, launch, health-probe, and register.
    /// </summary>
    /// <remarks>
    ///     The <paramref name="resolveArgs" /> delegate is awaited at the same point the profile resolver used to be, so
    ///     admission ordering and the "a slow profile read never stalls admission" invariant are unchanged. When
    ///     <paramref name="startupCapture" />, <paramref name="fitParamsCapture" />, and
    ///     <paramref name="ensureMetrics" /> are their normal-path defaults (<see langword="null" />,
    ///     <see langword="null" />, <see langword="false" />), the built spec is identical to the legacy spawn.
    /// </remarks>
    private async Task<RunningProcess> SpawnCoreAsync(ProcessKey key,
        Func<GpuVariant, CancellationToken, Task<ResolvedLaunchArguments>> resolveArgs,
        Action<string>? startupCapture,
        Action<string>? fitParamsCapture,
        bool ensureMetrics,
        bool applyLaunchPolicy,
        ProcessLaunchAdmission? admission,
        CancellationToken ct,
        LlamaServerBenchmarkLaunchPolicy? benchmarkPolicy = null,
        bool profilingOwned = false)
    {
        var modelFilePath = await _modelStore.ResolveModelFilePathAsync(key.ModelName, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(modelFilePath))
        {
            throw NonRetryable("The requested model is not installed.");
        }

        // A LoRA-adapter model has no weights of its own: llama-server loads the installed BASE model and applies this
        // entry's file as --lora. Resolving it here (rather than inside the spec builder) keeps every size-derived
        // decision below — the readiness deadline in particular — accounting for the bytes actually loaded.
        string? adapterFilePath = null;
        var adapterSizeBytes = 0L;
        try
        {
            if (await _modelStore.ResolveAdapterLaunchAsync(key.ModelName, ct).ConfigureAwait(false) is { } adapterLaunch)
            {
                modelFilePath = adapterLaunch.BaseModelFilePath;
                adapterFilePath = adapterLaunch.AdapterFilePath;
                adapterSizeBytes = adapterLaunch.AdapterSizeBytes;
            }
        }
        catch (GgufAdapterBaseModelMissingException exception)
        {
            throw NonRetryable(exception.Message);
        }

        // A vision model's mmproj projector companion — passed to llama-server as --mmproj so it accepts image input.
        // Chat role only (embedding/reranker never take images); null for a text-only model, which gets no --mmproj.
        var projectorFilePath = key.Role == ModelRole.Chat
            ? await _modelStore.ResolveProjectorFilePathAsync(key.ModelName, ct).ConfigureAwait(false)
            : null;

        // The cold-start readiness deadline scales with the on-disk model size — a large model loads
        // proportionally slower, so a fixed constant would kill and retry it before it can finish (the audited hang). A
        // missing/unreadable size (0) falls back to the base timeout.
        var readinessTimeout = _options.ResolveReadinessTimeout(TryGetFileSizeBytes(modelFilePath) + adapterSizeBytes);

        // A chat-role EXTERNAL-DRAFT speculative mode needs its draft GGUF present before launch — a missing file would
        // otherwise start a server that dies cryptically. Deterministic misconfiguration → non-retryable (mirrors the
        // model-not-installed guard above). draft-mtp, ngram-*, and disabled modes never reach this: MTP drafts from
        // heads inside the main model, so there is no second GGUF to find (RequiresExternalDraftModel is false).
        // The operator selects a draft model by NAME (installed chat model); resolve it to its on-disk GGUF the same way
        // the target model is resolved above so the effective launch args carry a real path. An explicit path override
        // (SpeculativeDraftModelPath), when set, wins and skips resolution.
        var launchTuning = LlamaServerLaunchArgumentComposer.ResolveChatLaunchTuning(benchmarkPolicy, _options);
        var speculative = launchTuning.Speculative;
        if (key.Role == ModelRole.Chat && speculative.RequiresExternalDraftModel)
        {
            var draftModelPath = speculative.DraftModelPath;
            if (string.IsNullOrWhiteSpace(draftModelPath) && !string.IsNullOrWhiteSpace(_options.SpeculativeDraftModelName))
            {
                draftModelPath = await _modelStore.ResolveModelFilePathAsync(_options.SpeculativeDraftModelName, ct).ConfigureAwait(false);
                speculative = speculative with
                {
                    DraftModelPath = draftModelPath
                };
            }

            if (string.IsNullOrWhiteSpace(draftModelPath) || !File.Exists(draftModelPath))
            {
                throw NonRetryable("Speculative decoding is set to a draft model, but the configured draft model file was not found. Check the draft model or disable speculative decoding.");
            }
        }
        else if (key.Role == ModelRole.Chat
                 && speculative.ModeClass is SpeculativeModeClass.MainModelHeads
                 && (!string.IsNullOrWhiteSpace(speculative.DraftModelPath) || !string.IsNullOrWhiteSpace(_options.SpeculativeDraftModelName)))
        {
            // Ignored, not rejected: settings saved before this contract was corrected were REQUIRED to name a draft
            // model for draft-mtp, so rejecting would turn every such install into a non-retryable launch failure on
            // upgrade. Clearing the path keeps the launch spec honest — nothing downstream can emit a stale draft flag.
            speculative = speculative with
            {
                DraftModelPath = null
            };

            _logger.LogInformation("Speculative mode {Mode} drafts from the main model's own MTP heads; the configured draft model is ignored.",
                speculative.NormalizedMode);
        }

        var variant = await _variantSelector.SelectVariantAsync(ct).ConfigureAwait(false);
        if (admission is not null)
        {
            if (variant != admission.Variant)
            {
                throw NonRetryable("The admitted local model launch no longer matches the selected runtime.");
            }

            var facts = await _modelStore.ResolveModelFootprintFactsAsync(key.ModelName, ct).ConfigureAwait(false);
            var contentIdentity = facts?.ContentIdentity ?? $"{key.ModelName}:{facts?.FileSizeBytes ?? TryGetFileSizeBytes(modelFilePath)}";
            if (!string.Equals(contentIdentity, admission.Allocation.ContentIdentity, StringComparison.Ordinal))
            {
                throw NonRetryable("The admitted local model launch no longer matches the installed model.");
            }
        }

        var binary = await _binaryManager.EnsureBinaryAsync(variant, ct).ConfigureAwait(false);

        // Everything below keys off `variant`: the VRAM admission gate, the placement sniffer, the launch policy and,
        // through LlamaServerLaunchProjection, every GPU argument (it gates -ngl/--fit/-ctk on `variant != Cpu`). A
        // serve can hand back a build of a DIFFERENT variant than was asked for — the managed source-build record is
        // authoritative and wins when the selector's cached signal has not been seeded yet — so follow the binary that
        // is actually being launched. Selecting Cpu and serving a CUDA build would otherwise spawn it with no offload
        // at all. The admission identity check above deliberately stays on the SELECTED variant: that is the variant
        // the admission was granted against.
        variant = binary.Variant;
        var capabilityManifest = await _capabilityManifestProbe.GetManifestAsync(binary, ct).ConfigureAwait(false);
        if (!capabilityManifest.ProbeSucceeded)
        {
            throw NonRetryable("The selected llama.cpp runtime could not report its supported server options. Reinstall or rebuild the runtime and try again.");
        }

        // Resolve the launch args (frozen-profile replay or explore-mode auto-fit, or operator-supplied profiling args)
        // for this (model, role, backend) BEFORE taking the admission gate, so a slow profile read never stalls
        // admission for other keys.
        // The ticket describes the variant it was granted against, which the override above can have moved off. Once it
        // has, NOTHING the ticket carries applies to this spawn: its arguments were resolved for another backend, and
        // its allocation was sized for one — a CPU admission carries no GPU bytes, so spending it on a GPU load would
        // put that load outside VRAM capacity accounting and let concurrent spawns oversubscribe the device. Drop it
        // and take the unadmitted path, which resolves both against the variant actually being launched.
        var admitted = admission?.Variant == variant ? admission : null;
        var resolved = admitted?.ResolvedArguments ?? await resolveArgs(variant, ct).ConfigureAwait(false);

        // Per-model developer/advanced override: extra llama-server flags the operator typed. Resolved here (alongside
        // the profile args, BEFORE the admission gate) so a slow store read never stalls admission for other keys, and
        // ONLY on the normal serving path — a benchmark/profiling spawn (applyLaunchPolicy false) must stay a pure
        // measurement, so the operator's experimentation flags never perturb it. The app-managed flags are already
        // stripped by the resolver: reachability (-m/--model/--host/--port) AND the memory-fit placement family
        // (-c/-ngl/-ts/-ot/-ctk/-ctv/-fa/--parallel/-b/-ub), whose values the allocation + policy above already decided
        // and recorded in the ledger — so what remains is sampling/decoding tuning only. Those are appended after the
        // built spec below, where a later scalar flag overrides the bundled tuning default (llama.cpp is last-wins).
        // Never throws.
        var extraLaunchArgs = applyLaunchPolicy
            ? await _extraArgumentsResolver.ResolveAsync(key.ModelName, key.Role, ct).ConfigureAwait(false)
            : [];

        // Serialize the spawn-through-readiness window of GPU-backed loads process-wide (shared with the image
        // supervisor) so two --fit loads never read the same free-VRAM snapshot at once and oversubscribe the device.
        // CPU loads bypass — they do not contend for VRAM. The gate is acquired here (after variant selection + arg
        // resolution, immediately before the admission cap decision that may evict an idle process to free VRAM) so the
        // freed VRAM is seen only by THIS load's --fit; the ticket releases on ready OR any failure via the using scope,
        // and the next waiter then proceeds with a fresh free-VRAM read (the re-evaluation). Because this core runs
        // under the detached-spawn/shutdown token (not the first caller's), a caller cancelling its wait never leaves
        // the gate held. The ticket deliberately spans BOTH launch-plan attempts below (optimized + safe retry) — the
        // retry is part of the same load window, and another load interleaving between the attempts would re-race the
        // free-VRAM read the gate exists to serialize.
        using var admissionTicket = variant == GpuVariant.Cpu
            ? null
            : await _loadAdmission.AcquireAsync(ct).ConfigureAwait(false);

        // The central launch policy fills in the deterministic context (-c), the GPU KV-cache
        // quantization + flash-attention optimization, and the CPU thread policy the audited launch defaults omitted.
        // Replay profiling bypasses it so the supplied frozen args ARE the experiment. Explore profiling applies the
        // production policy because the helper and server must observe the same concrete context/KV/FA vector as normal
        // serving; otherwise the fit helper (behavior observed on b9692) can report unchanged `-c 0 -ngl -1` defaults
        // that are not replayable placement.
        var planSet = await BuildLaunchPlanCandidatesAsync(key,
                variant,
                resolved,
                applyLaunchPolicy,
                admitted?.Allocation,
                ct)
            .ConfigureAwait(false);
        var planCandidates = planSet.Candidates;

        Exception? optimizedFailure = null;
        for (var attempt = 0; attempt < planCandidates.Count; attempt++)
        {
            var candidate = planCandidates[attempt];
            var isSafeRetry = candidate.AttemptKind == LlamaServerLoadAttemptKind.SafeRetry;
            var port = await _reaper.AdmitAndAllocatePortAsync(ct).ConfigureAwait(false);

            ILlamaServerProcessHandle? handle = null;
            long? readinessStartedTimestamp = null;
            var readinessRecorded = false;
            var automaticCapture = applyLaunchPolicy ? new LlamaServerBoundedStartupCapture() : null;

            // Latches llama.cpp's layer-placement banner out of the streamed startup output. It is deliberately NOT
            // read off automaticCapture: that buffer is bounded, and at the verbosity the banner requires it is
            // printed around line 155 — outside any small window.
            var placementSniffer = variant == GpuVariant.Cpu ? null : new LlamaServerLayerPlacementSniffer();

            // Flipped once this child is serving, to demote its (raised-verbosity) request chatter to Debug. It stays
            // false for the whole load, and forever on a spawn that never reaches readiness, so the placement banner
            // and every failure message are still logged at Information.
            var servingWindow = new LlamaServerDiagnosticVerbosityWindow();
            try
            {
                var spec = LlamaServerLaunchArgumentComposer.BuildLaunchSpec(key, binary.ServerExecutablePath, modelFilePath, port, variant, candidate.Resolved,
                    launchTuning.ChatCacheReuse,
                    speculative,
                    candidate.Plan,
                    launchTuning.ChatCacheRamMiB,
                    projectorFilePath,
                    adapterFilePath);

                // Append the operator's per-model extra flags LAST so they win over the bundled tuning defaults
                // (llama.cpp is last-wins for scalar flags). Placed BEFORE the diagnostic --metrics / -lv fill-ins below
                // so those checks see an operator-supplied --metrics/-lv and do not duplicate it.
                if (extraLaunchArgs.Count > 0)
                {
                    spec = spec with
                    {
                        Arguments = [.. spec.Arguments, .. extraLaunchArgs]
                    };
                }

                // Benchmark spawns need /metrics on ANY variant. Both GPU modes emit it themselves, so this now only
                // fills in the CPU case — and guards against a future spec shape that omits it.
                if (ensureMetrics && !spec.Arguments.Contains("--metrics", StringComparer.Ordinal))
                {
                    spec = spec with
                    {
                        Arguments = [.. spec.Arguments, "--metrics"]
                    };
                }

                // Raise log verbosity just enough to make llama.cpp print how many layers actually landed on the GPU.
                // At the server default the whole startup is 11 lines and says nothing about placement, so a model
                // whose weights spilled into system RAM is indistinguishable from one that fully fit.
                //
                // EVERY GPU spawn pays this, deliberately. Placement under auto-fit is decided against the FREE VRAM at
                // load time, so the same model can be fully resident when loaded alone and partly resident when loaded
                // beside two others — measuring once and reusing the answer would report a number that is no longer
                // true. Each spawn is a fresh process, so each gets a fresh reading. The sink cost is paid back by
                // demoting this child's request chatter to Debug once it is serving (see servingWindow).
                //
                // The operator-profiling EXPLORE path is skipped because it already raises verbosity to maximum below.
                // A benchmark spawn is the exception among profiling spawns: it takes a fit-params capture like every
                // other profiling spawn, but it never reaches the explore `-v` branch (its args are a replay), so
                // without this it was the one measurement that could not say where its own layers landed — exactly the
                // spawn whose placement a later reader most needs. It pays the same servingWindow demotion.
                if ((fitParamsCapture is null || benchmarkPolicy is not null)
                    && placementSniffer is not null
                    && variant != GpuVariant.Cpu
                    && !LlamaServerLaunchArgumentComposer.HasVerbosityArgument(spec.Arguments))
                {
                    spec = spec with
                    {
                        Arguments = [.. spec.Arguments, "-lv", PlacementProbeLogVerbosity],
                        ShouldDemoteForwardedLines = servingWindow.IsServing
                    };
                }

                // Operator profiling spawns capture both pipes; the normal path leaves the sink null (spec unchanged).
                if (startupCapture is not null || automaticCapture is not null || placementSniffer is not null)
                {
                    // The sink is wired for the process's LIFETIME, but the two automatic buffers behind it are
                    // startup-only: the placement banner is read at readiness and the failure-classifier window is only
                    // ever read from this attempt's catch block, which readiness has ruled out. Detaching them at
                    // readiness turns every serving-time forwarded line from a Lock + string copy into one volatile
                    // read. The operator profiling sink is NOT detached — that output was explicitly requested.
                    var isServing = servingWindow.IsServing;
                    spec = spec with
                    {
                        StartupCapture = line =>
                        {
                            startupCapture?.Invoke(line);
                            if (isServing())
                            {
                                return;
                            }

                            automaticCapture?.Add(line);
                            placementSniffer?.Add(line);
                        }
                    };
                }

                if (fitParamsCapture is not null
                    && candidate.Resolved.ExploreMode
                    && variant != GpuVariant.Cpu
                    && !spec.Arguments.Contains("-v", StringComparer.Ordinal)
                    && !spec.Arguments.Contains("--verbose", StringComparer.Ordinal))
                {
                    // The fit helper (observed on b9692) leaves -ngl at its automatic sentinel when the initial
                    // placement already fits. Verbose
                    // load_tensors output is the authoritative proof that automatic placement meant every layer was
                    // offloaded; the fit parser uses that proof to normalize replay to explicit all-layers (-2).
                    spec = spec with
                    {
                        Arguments = [.. spec.Arguments, "-v"]
                    };
                }

                var capabilityDecision = LlamaServerCapabilityGate.Apply(spec, capabilityManifest, ensureMetrics);
                if (!capabilityDecision.IsCompatible)
                {
                    throw CapabilityIncompatible(capabilityDecision.SanitizedError!, capabilityDecision.CanTrySafeFallback);
                }

                spec = capabilityDecision.Spec;
                if (capabilityDecision.OmittedOptions.Count > 0)
                {
                    _logger.LogWarning("The selected llama-server runtime lacks optional capabilities {Options}; those launch optimizations were omitted.",
                        string.Join(", ", capabilityDecision.OmittedOptions));
                }

                IReadOnlyList<string>? fittedArgsForSuccessfulAttempt = null;
                if (fitParamsCapture is not null && candidate.Resolved.ExploreMode && variant != GpuVariant.Cpu)
                {
                    var fitResult = await _fitParamsRunner.RunAsync(spec, ct).ConfigureAwait(false);
                    if (fitResult.Status == LlamaFitParamsRunStatus.Succeeded)
                    {
                        fittedArgsForSuccessfulAttempt = fitResult.StandardOutput;
                    }
                    else if (fitResult.Status == LlamaFitParamsRunStatus.MissingCapability)
                    {
                        _logger.LogWarning(
                            "The resolved llama.cpp runtime does not expose the sibling llama-fit-params capability; the live explore spawn will remain auto-fit, but no placement profile will be drafted.");
                    }
                    else
                    {
                        _logger.LogWarning("llama-fit-params acquisition failed ({Reason}); the live explore spawn will remain auto-fit, but no placement profile will be drafted.",
                            fitResult.FailureReason ?? "unknown failure");
                    }
                }

                readinessStartedTimestamp = _timeProvider.GetTimestamp();
                handle = _launcher.Launch(spec);
                _logger.LogInformation("llama-server spawned for model {ModelName} role {Role} (pid {ProcessId}, port {Port}){LaunchPlan}.",
                    key.ModelName, key.Role, handle.ProcessId, port, LlamaServerLaunchArgumentComposer.DescribeLaunchPlan(candidate.Plan));

                await WaitForReadyOrExitAsync(handle, spec.BaseAddress, readinessTimeout, ct).ConfigureAwait(false);
                var readinessDuration = _timeProvider.GetElapsedTime(readinessStartedTimestamp.Value);
                _logger.LogInformation("llama-server ready for model {ModelName} role {Role} (pid {ProcessId}) after {ElapsedMs:F0} ms (readiness budget {BudgetSeconds:F0}s).",
                    key.ModelName, key.Role, handle.ProcessId, readinessDuration.TotalMilliseconds, readinessTimeout.TotalSeconds);

                var placement = RecordObservedLayerPlacement(key, variant, placementSniffer);

                // Assembled here (the RunningProcess below carries it) but NOT published yet: post-readiness
                // bookkeeping still runs, and anything that throws there tree-kills the child. A Ready observation
                // raised before that point would tell the host — and its last-load VRAM cache — that a process which
                // never served had loaded. It is published just before the successful return instead, so
                // readinessRecorded stays false until then and the catch path records the failed outcome exactly once.
                var loadObservation = BuildLoadObservation(key,
                    variant,
                    capabilityManifest.Version ?? binary.Version,
                    capabilityManifest.ExecutableSha256,
                    readinessDuration,
                    LlamaServerReadinessOutcome.Ready,
                    placement.Outcome,
                    candidate.AttemptKind,
                    speculative,
                    admitted);

                // The load window is over and the banner has been read. From here the child's raised-verbosity output is
                // per-request chatter nobody asked to persist: drop it to Debug AND detach the automatic startup
                // capture (same latch, see the StartupCapture wiring above). Deliberately after
                // RecordObservedLayerPlacement, and never reached on a spawn that failed to become ready — both
                // buffers must stay live for the whole load window.
                servingWindow.MarkServing();

                // Publish helper output only for the candidate that actually reached readiness. If the optimized
                // production policy failed and the safe plan retried, output from the failed candidate must not be frozen.
                if (fittedArgsForSuccessfulAttempt is not null)
                {
                    foreach (var line in fittedArgsForSuccessfulAttempt)
                    {
                        fitParamsCapture!(line);
                    }
                }

                // Read the effective per-slot context the server actually loaded (best-effort) so both app-side
                // budgeters and the UI meter size against the REAL window rather than the requested/advertised one.
                var effectiveContext = await TryReadEffectiveContextAsync(spec.BaseAddress, ct).ConfigureAwait(false);

                // A benchmark spawn — and only a benchmark spawn — records what it actually launched, once the process is
                // genuinely serving. Assembly is non-throwing by construction (every unreadable fact becomes null), so
                // a receipt can never turn a healthy measurement into a failed run.
                //
                // The projection is read back out of the FINAL argv rather than recomputed from (variant, resolved,
                // plan, role, tuning): the capability gate above can drop an optional flag the intended projection
                // still claims, and the operator's extra arguments were appended after it. An unparseable vector falls
                // back to the intended shape — a describable launch is worth more than no receipt at all.
                var launchReceipt = benchmarkPolicy is null
                    ? null
                    : await BuildBenchmarkLaunchReceiptAsync(variant,
                        capabilityManifest.Version ?? binary.Version,
                        capabilityManifest.ExecutableSha256,
                        LlamaServerLaunchProjection.TryFromArguments(spec.Arguments)
                        ?? LlamaServerLaunchProjection.From(variant,
                            candidate.Resolved,
                            candidate.Plan,
                            key.Role,
                            launchTuning.ChatCacheReuse,
                            launchTuning.ChatCacheRamMiB),
                        new LlamaServerLaunchAuxAssets(!string.IsNullOrWhiteSpace(adapterFilePath),
                            !string.IsNullOrWhiteSpace(projectorFilePath),
                            !string.IsNullOrWhiteSpace(speculative.DraftModelPath)),
                        placement,
                        effectiveContext,
                        benchmarkPolicy,
                        handle.ProcessId,
                        capabilityDecision.OmittedOptions).ConfigureAwait(false);

                var endpoint = new LlamaServerEndpoint { ModelName = key.ModelName, Role = key.Role, BaseAddress = spec.BaseAddress };
                var running = new RunningProcess(handle, endpoint, port, _timeProvider.GetUtcNow())
                {
                    EffectiveContextTokens = effectiveContext,
                    SuccessfulLaunchArguments = fitParamsCapture is null ? [] : [.. spec.Arguments],
                    LoadObservation = loadObservation,
                    LaunchReceipt = launchReceipt,
                    IsProfilingOwned = profilingOwned
                };
                _processes[key] = running;

                if (isSafeRetry)
                {
                    // The safe config reached readiness where the optimized (KV-quant + flash-attention) config could
                    // not — so the optimized config is the culprit for THIS backend (not a broken model, which would
                    // fail the safe config too). Record it so subsequent spawns skip the known-bad optimized config.
                    // WithoutKvCacheQuantization() leaves the plan's KvCacheType intact, so the verdict is keyed on
                    // the node's CURRENT selection. On the replay branch that is not read off the frozen profile: it
                    // coincides with the profile's frozen type only because the node's selected KV-cache type is part
                    // of a profile's launch-policy fingerprint, so a profile frozen under a different type is already
                    // stale and re-explores rather than replaying — a mismatching pair never reaches this line.
                    if (candidate.Plan is { CpuMoe: true })
                    {
                        // An expert-offload spawn is the most VRAM-marginal launch on the box, so a one-shot success
                        // without KV quantization proves nothing about KV: the primary may have failed on placement or
                        // transient pressure. Recording it would disable the optimized config for EVERY model on this
                        // backend from one model's failure, so it is logged as inconclusive instead.
                        _logger.LogInformation(
                            "Safe-retry readiness for expert-offload model {ModelName} role {Role} is inconclusive about the optimized KV config; nothing recorded for backend {Variant}.",
                            key.ModelName,
                            key.Role,
                            variant);
                    }
                    else if (candidate.Plan is { } safeRetryPlan)
                    {
                        // Deliberately after the _processes registration above, and safe there because the policy
                        // absorbs a verdict it cannot persist (see ILlamaServerLaunchPolicy). The endpoint is already
                        // reachable by a concurrent EnsureRunningAsync, so an exception out of this line would
                        // tree-kill a process another caller holds — to save a cache entry the next spawn re-records.
                        await _launchPolicy.RecordOptimizedConfigFailedAsync(variant, safeRetryPlan.KvCacheType, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        // Unreachable today: every SafeRetry candidate is built from a plan. An empty key would be
                        // unmatchable by every read, silently discarding the verdict, so say so instead.
                        _logger.LogWarning("Safe-retry readiness for model {ModelName} role {Role} carried no launch plan; the optimized-config failure was not recorded.",
                            key.ModelName,
                            key.Role);
                    }
                }

                // Every post-readiness step survived, so this spawn really did produce a serving process: raise the
                // Ready observation now and latch it, which is what keeps the catch paths from recording a second one.
                PublishLoadObservation(loadObservation);
                readinessRecorded = true;
                return running;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                RecordIncompleteReadinessAttempt(LlamaServerReadinessOutcome.Cancelled);
                handle?.TreeKill();
                handle?.Dispose();
                await _reaper.ReleaseReservedPortAsync(port).ConfigureAwait(false);
                throw;
            }
            catch (Exception ex)
            {
                RecordIncompleteReadinessAttempt(LlamaServerReadinessOutcome.Failed);
                // Launch/readiness failed: tree-kill the half-started child and free its reserved port (under the
                // admission gate, since the reserved-port set backs the cap count) before deciding whether to fall back.
                handle?.TreeKill();
                handle?.Dispose();
                await _reaper.ReleaseReservedPortAsync(port).ConfigureAwait(false);

                // A capability rejection is known before process launch, so retrying an optimized/safe candidate cannot
                // change the outcome. Other non-retryable errors can still be caused by the optimized child exiting
                // during load; preserve the paid-for one-shot safe KV/FA fallback for that case.
                if (ex.Data.Contains(CapabilityIncompatibleMarker))
                {
                    if (ex.Data.Contains(CapabilitySafeFallbackMarker)
                        && !isSafeRetry
                        && attempt + 1 < planCandidates.Count)
                    {
                        optimizedFailure = ex;
                        _logger.LogWarning(
                            "The selected llama-server runtime does not support the optimized KV-cache/Flash Attention vector for model {ModelName} role {Role}; using the explicit safe candidate.",
                            key.ModelName,
                            key.Role);
                        continue;
                    }

                    throw;
                }

                // The OPTIMIZED attempt failed and a safe candidate remains: remember the error and retry ONCE with the
                // safe (KV/FA off) config. Any other failure (the safe attempt, or a spawn with no fallback candidate)
                // propagates exactly as before — including the readiness-timeout marker the restart loop keys on.
                if (!isSafeRetry && attempt + 1 < planCandidates.Count)
                {
                    optimizedFailure = ex;
                    _logger.LogWarning(ex,
                        "llama-server optimized launch (KV-cache quant + flash attention) failed for model {ModelName} role {Role} on backend {Variant}; retrying once with the safe config.",
                        key.ModelName, key.Role, variant);
                    continue;
                }

                if (automaticCapture is not null
                    && LlamaStartupFailureClassifier.Classify(automaticCapture.Snapshot()) == LlamaStartupFailureKind.OutOfMemory
                    && planSet.Allocation is not null
                    && _allocationResolver.TryDownTierAfterOutOfMemory(planSet.Allocation, out var downTiered))
                {
                    planSet = planSet with
                    {
                        Allocation = downTiered
                    };
                    var downTierPlan = await _launchPolicy.ResolveAsync(key.Role, variant, candidate.Resolved, downTiered, ct).ConfigureAwait(false);
                    planCandidates.Add(candidate with
                    {
                        Plan = candidate.Plan is { UseKvCacheQuantization: false }
                            ? downTierPlan.WithoutKvCacheQuantization()
                            : downTierPlan
                    });
                    _logger.LogWarning("llama-server automatic context allocation encountered a classified startup OOM; retrying at context tier {ContextTokens}.",
                        downTiered.ProcessContextTokens);
                    continue;
                }

                throw;
            }

            void RecordIncompleteReadinessAttempt(LlamaServerReadinessOutcome outcome)
            {
                if (readinessRecorded || readinessStartedTimestamp is not { } started)
                {
                    return;
                }

                _ = RecordLoadTelemetry(key,
                    variant,
                    capabilityManifest.Version ?? binary.Version,
                    capabilityManifest.ExecutableSha256,
                    _timeProvider.GetElapsedTime(started),
                    outcome,
                    variant == GpuVariant.Cpu ? LlamaServerPlacementOutcome.Cpu : LlamaServerPlacementOutcome.Unknown,
                    candidate.AttemptKind,
                    speculative,
                    admitted);
                readinessRecorded = true;
            }
        }

        // Unreachable: the loop returns on success or throws on the final candidate; the fallback keeps the analyzer happy.
        throw optimizedFailure ?? new InvalidOperationException("llama-server spawn produced no launch attempt.");
    }

    private Task<LlamaServerLaunchPlanSet> BuildLaunchPlanCandidatesAsync(ProcessKey key,
        GpuVariant variant,
        ResolvedLaunchArguments resolved,
        bool applyLaunchPolicy,
        ProcessContextAllocation? admittedAllocation,
        CancellationToken ct)
    {
        var builder = new LlamaServerLaunchCandidateBuilder(_allocationResolver, _launchPolicy);
        return builder.BuildAsync(key, variant, resolved, applyLaunchPolicy, admittedAllocation, NonRetryable, ct);
    }

    /// <summary>
    ///     Publishes a sniffed layer-placement observation once the process is genuinely serving. Recording only after
    ///     readiness keeps a candidate that printed a banner and then failed to start out of the operator-facing report.
    ///     A partial or zero offload is logged as a warning: the model serves, but a share of its layers — or all of
    ///     them — run from system RAM. The raw counts travel with the class so a reader sees 38/49 rather than only
    ///     "partial".
    /// </summary>
    private LlamaServerLaunchPlacement RecordObservedLayerPlacement(ProcessKey key,
        GpuVariant variant,
        LlamaServerLayerPlacementSniffer? sniffer)
    {
        if (sniffer is null || !sniffer.TryGetObservation(out var offloaded, out var total))
        {
            return new LlamaServerLaunchPlacement(variant == GpuVariant.Cpu ? LlamaServerPlacementOutcome.Cpu : LlamaServerPlacementOutcome.Unknown,
                OffloadedLayers: null,
                TotalLayers: null);
        }

        _layerPlacementReport.Record(key.Role, variant, key.ModelName, offloaded, total);

        // 0/N is its own outcome, not the extreme end of a partial offload: a GPU build serving entirely from system RAM
        // is a different fact about a measurement than one that placed most of its layers.
        if (offloaded <= 0)
        {
            _logger.LogWarning("llama-server placed NONE of model {ModelName} role {Role}'s {Total} layers on the GPU; the whole model runs from system RAM, which is substantially slower.",
                key.ModelName, key.Role, total);
            return new LlamaServerLaunchPlacement(LlamaServerPlacementOutcome.None, offloaded, total);
        }

        if (offloaded < total)
        {
            _logger.LogWarning("llama-server placed {Offloaded}/{Total} of model {ModelName} role {Role} layers on the GPU; the remainder runs from system RAM, which is substantially slower.",
                offloaded, total, key.ModelName, key.Role);
            return new LlamaServerLaunchPlacement(LlamaServerPlacementOutcome.Partial, offloaded, total);
        }

        _logger.LogInformation("llama-server placed all {Total} layers of model {ModelName} role {Role} on the GPU.",
            total, key.ModelName, key.Role);
        return new LlamaServerLaunchPlacement(LlamaServerPlacementOutcome.Full, offloaded, total);
    }

    /// <summary>
    ///     Assembles the benchmark launch receipt. Non-throwing by construction: the only fact that can fail to be read
    ///     is the running image digest, and <see cref="TryComputeRunningImageSha256Async" /> reports that failure as
    ///     <see langword="null" /> rather than as an exception, so a receipt never costs a run its measurement.
    /// </summary>
    internal static async Task<LlamaServerLaunchReceipt> BuildBenchmarkLaunchReceiptAsync(GpuVariant variant,
        string? executableVersion,
        string? manifestSha256,
        LlamaServerLaunchProjection launchProjection,
        LlamaServerLaunchAuxAssets auxAssets,
        LlamaServerLaunchPlacement placement,
        int? effectiveContextTokens,
        LlamaServerBenchmarkLaunchPolicy benchmarkLaunchPolicy,
        int processId,
        IReadOnlyList<string>? omittedOptions = null)
    {
        return new LlamaServerLaunchReceipt
        {
            ReceiptVersion = LlamaServerLaunchReceipt.CurrentVersion,
            Variant = variant,
            Os = DescribeOperatingSystem(),
            ExecutableVersion = executableVersion,
            ExecutableSha256 = await TryComputeRunningImageSha256Async(processId).ConfigureAwait(false),
            ManifestSha256 = manifestSha256,
            LaunchProjection = launchProjection,
            AuxAssets = auxAssets,
            Placement = placement,
            EffectiveContextTokens = effectiveContextTokens,
            BenchmarkLaunchPolicy = benchmarkLaunchPolicy,
            OmittedOptions = omittedOptions ?? []
        };
    }

    /// <summary>
    ///     Hashes the image the LIVE process is running, rather than the executable path the launch resolved — those two
    ///     disagree exactly when it matters, because a runtime can be replaced on disk between launch and readiness.
    ///     Returns <see langword="null" /> whenever the running image cannot be read; an unreadable digest is a fact
    ///     worth recording as absent, never a reason to fail a benchmark.
    /// </summary>
    internal static async Task<string?> TryComputeRunningImageSha256Async(int processId)
    {
        if (processId <= 0)
        {
            return null;
        }

        try
        {
            string? imagePath;
            if (OperatingSystem.IsLinux())
            {
                // /proc/<pid>/exe resolves through the kernel to the mapped image even when the file was replaced or
                // unlinked after launch, which is the whole point of reading it instead of the resolved path.
                imagePath = $"/proc/{processId.ToString(CultureInfo.InvariantCulture)}/exe";
            }
            else
            {
                using var process = Process.GetProcessById(processId);
                imagePath = process.MainModule?.FileName;
            }

            if (string.IsNullOrWhiteSpace(imagePath))
            {
                return null;
            }

            // CancellationToken.None: a bounded local-file hash the caller cannot usefully abandon, and cancelling it
            // would turn recorded evidence into a failure. Matches BenchmarkFidelityExecutor.TryHashFileAsync, and
            // means the catch below never has to absorb a cancellation it did not ask for.
            await using var stream = File.OpenRead(imagePath);
            return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, CancellationToken.None).ConfigureAwait(false));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The host OS as a stable, bounded token — enough to tell a Metal host from a CUDA one, and nothing more.</summary>
    private static string DescribeOperatingSystem()
    {
        if (OperatingSystem.IsWindows())
        {
            return "windows";
        }

        if (OperatingSystem.IsMacOS())
        {
            return "macos";
        }

        return OperatingSystem.IsLinux() ? "linux" : "unknown";
    }

    private LlamaServerLoadObservation RecordLoadTelemetry(ProcessKey key,
        GpuVariant variant,
        string runtimeVersion,
        string? runtimeSha256,
        TimeSpan readinessDuration,
        LlamaServerReadinessOutcome outcome,
        LlamaServerPlacementOutcome placement,
        LlamaServerLoadAttemptKind attemptKind,
        SpeculativeDecodingSettings speculative,
        ProcessLaunchAdmission? admitted)
    {
        var observation = BuildLoadObservation(key, variant, runtimeVersion, runtimeSha256, readinessDuration, outcome, placement, attemptKind, speculative, admitted);
        PublishLoadObservation(observation);
        return observation;
    }

    /// <summary>
    ///     Assembles the observation without raising it. Split from <see cref="RecordLoadTelemetry" /> so the Ready
    ///     path can fill <c>RunningProcess.LoadObservation</c> while the PUBLISH waits until the post-readiness
    ///     bookkeeping has actually succeeded — a Ready load must never be announced for a process that gets killed.
    /// </summary>
    private static LlamaServerLoadObservation BuildLoadObservation(ProcessKey key,
        GpuVariant variant,
        string runtimeVersion,
        string? runtimeSha256,
        TimeSpan readinessDuration,
        LlamaServerReadinessOutcome outcome,
        LlamaServerPlacementOutcome placement,
        LlamaServerLoadAttemptKind attemptKind,
        SpeculativeDecodingSettings speculative,
        ProcessLaunchAdmission? admitted)
    {
        var speculativeModeClass = key.Role == ModelRole.Chat
            ? speculative.ModeClass ?? SpeculativeModeClass.Disabled
            : SpeculativeModeClass.Disabled;

        // Both byte figures are whatever the admission ALREADY knew — the free-VRAM reading the capacity gate took
        // under its decision gate, and the GPU bytes it reserved. Nothing is probed here: a load must not pay for a
        // second nvidia-smi call, and a figure measured after the weights landed would answer a different question.
        // An unadmitted spawn (direct, profiling, test) or one whose variant moved off the admission reports neither.
        var observation = new LlamaServerLoadObservation
        {
            Role = key.Role,
            Variant = variant,
            RuntimeVersion = runtimeVersion,
            RuntimeSha256 = runtimeSha256,
            ReadinessDurationMs = Math.Max(0d, readinessDuration.TotalMilliseconds),
            Outcome = outcome,
            Placement = placement,
            AttemptKind = attemptKind,
            SpeculativeModeClass = speculativeModeClass,
            ModelName = key.ModelName,
            GlobalFreeVramBytesAtLoad = admitted?.GlobalFreeVramBytesAtAdmission,
            AdmittedVramBytes = admitted?.Allocation.Footprint.GpuBytes
        };
        return observation;
    }

    private void PublishLoadObservation(LlamaServerLoadObservation observation)
    {
        try
        {
            _loadTelemetry.RecordLoad(observation);
        }
        catch (Exception exception)
        {
            // Telemetry is report-only. A broken exporter must never change launch/fallback/admission behavior.
            _logger.LogDebug(exception, "llama-server load telemetry observer failed.");
        }
    }

    /// <summary>Best-effort read of the running server's effective context window from /props; null when unavailable.</summary>
    private async Task<int?> TryReadEffectiveContextAsync(Uri baseAddress, CancellationToken ct)
    {
        try
        {
            return await _healthProbe.TryReadEffectiveContextTokensAsync(baseAddress, ct).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Never let a /props hiccup discard a model that reached readiness; effective context simply stays unknown.
            _logger.LogDebug(exception, "Reading the effective context window from /props failed; effective context is unknown.");
            return null;
        }
    }

    /// <summary>
    ///     Waits for a freshly launched process to pass its readiness probe, racing that wait against the process
    ///     exiting. A child that dies during load (an incompatible model, or a context that will not fit in the
    ///     available memory) is detected the instant it exits and surfaced as a NON-RETRYABLE failure — instead of
    ///     polling <c>/health</c> against a dead endpoint for the full readiness budget and then retrying. A
    ///     crash-on-load is deterministic, so retrying it only multiplies the stall by <c>MaxRestartAttempts</c>.
    /// </summary>
    private async Task WaitForReadyOrExitAsync(ILlamaServerProcessHandle handle, Uri baseAddress, TimeSpan readinessTimeout, CancellationToken ct)
    {
        // Cancel the losing side the instant the other wins, so neither the /health poll nor the exit-watcher is left
        // running after the race is decided.
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var readyTask = _healthProbe.WaitForReadyAsync(baseAddress, readinessTimeout, linkedCts.Token);
        var exitTask = WatchForExitAsync(handle, linkedCts.Token);

        var winner = await Task.WhenAny(readyTask, exitTask).ConfigureAwait(false);

        if (winner == exitTask && handle.HasExited)
        {
            // The child exited before it ever became ready: a deterministic load failure. Stop the abandoned /health
            // poll and surface a sanitized, non-retryable error (no file paths) so the caller fails fast.
            await linkedCts.CancelAsync().ConfigureAwait(false);
            await SwallowCancellationAsync(readyTask).ConfigureAwait(false);
            throw NonRetryable("The local model runtime exited while loading the model. The model may be incompatible with this runtime or too large for the available memory.");
        }

        // Readiness settled first: stop the exit-watcher and honor the existing outcome — a genuine timeout (process
        // still alive but slow) stays a retryable "did not become ready in time".
        await linkedCts.CancelAsync().ConfigureAwait(false);
        await SwallowCancellationAsync(exitTask).ConfigureAwait(false);

        if (!await readyTask.ConfigureAwait(false))
        {
            _logger.LogWarning("llama-server (pid {ProcessId}) did not become ready within {TimeoutSeconds:F0}s.",
                handle.ProcessId, readinessTimeout.TotalSeconds);
            throw ReadinessTimedOut("The local model runtime did not become ready in time.");
        }
    }

    /// <summary>Polls the process's exit flag until it exits or the wait is cancelled (readiness won the race).</summary>
    private async Task WatchForExitAsync(ILlamaServerProcessHandle handle, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (handle.HasExited)
            {
                return;
            }

            await Task.Delay(ProcessExitPollInterval, _timeProvider, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Awaits the losing side of the readiness race, absorbing the cancellation it throws when abandoned.</summary>
    private static async Task SwallowCancellationAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected: this task was cancelled because the other side of the race won.
        }
    }
}
