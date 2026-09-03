namespace XE_Local_AI_Engine.Client.Services.Inference;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Orchestrates the Inference Optimizer explore → benchmark → freeze loop over the supervisor's exclusive profiling
///     entry point and the node-scoped profile/benchmark stores. SCOPED — it composes the scoped stores directly. Every
///     public method returns an outcome record (it never throws for an expected rejection such as a cloud model, a
///     missing profile, or a freeze that is not benchmark-justified).
/// </summary>
public sealed class InferenceProfileService : IInferenceProfileService
{
    private const string ProviderName = LlamaServerProviderConstants.ProviderName;
    private const string UnknownBuild = "unknown";
    private const string UnknownQuant = "unknown";
    private const int DefaultExploreCtxSize = 4096;

    private readonly IModelFitBenchmarkStore _benchmarkStore;
    private readonly IFittedArgsParser _fittedArgsParser;
    private readonly IGgufMetadataReader _ggufMetadataReader;
    private readonly IGgufModelStore _ggufModelStore;
    private readonly IHardwareProfiler _hardwareProfiler;
    private readonly IInferenceBenchmarkHarness _harness;
    private readonly IInferenceInvalidationEvaluator _invalidationEvaluator;
    private readonly InferenceBenchmarkVramAdmissionOptions _benchmarkVramAdmission;
    private readonly ILaunchPolicyFingerprintProvider _launchPolicyFingerprintProvider;
    private readonly ILogger<InferenceProfileService> _logger;
    private readonly IMachineKeyProvider _machineKeyProvider;
    private readonly IInferenceProfileStore _profileStore;
    private readonly IInstalledRuntimeStore _runtimeStore;
    private readonly IModelFitSnapshotStore _snapshotStore;
    private readonly ILlamaServerProcessSupervisor _supervisor;
    private readonly IProcessVramBudgetProbe _processVramBudgetProbe;
    private readonly IGpuVariantSelector _variantSelector;

    public InferenceProfileService(ILlamaServerProcessSupervisor supervisor,
        IInferenceProfileStore profileStore,
        IModelFitSnapshotStore snapshotStore,
        IModelFitBenchmarkStore benchmarkStore,
        IInferenceBenchmarkHarness harness,
        IFittedArgsParser fittedArgsParser,
        IGgufModelStore ggufModelStore,
        IGgufMetadataReader ggufMetadataReader,
        IMachineKeyProvider machineKeyProvider,
        IGpuVariantSelector variantSelector,
        IInstalledRuntimeStore runtimeStore,
        IHardwareProfiler hardwareProfiler,
        IProcessVramBudgetProbe processVramBudgetProbe,
        ILaunchPolicyFingerprintProvider launchPolicyFingerprintProvider,
        IInferenceInvalidationEvaluator invalidationEvaluator,
        IOptions<InferenceBenchmarkVramAdmissionOptions> benchmarkVramAdmission,
        ILogger<InferenceProfileService> logger)
    {
        ArgumentNullException.ThrowIfNull(supervisor);
        ArgumentNullException.ThrowIfNull(profileStore);
        ArgumentNullException.ThrowIfNull(snapshotStore);
        ArgumentNullException.ThrowIfNull(benchmarkStore);
        ArgumentNullException.ThrowIfNull(harness);
        ArgumentNullException.ThrowIfNull(fittedArgsParser);
        ArgumentNullException.ThrowIfNull(ggufModelStore);
        ArgumentNullException.ThrowIfNull(ggufMetadataReader);
        ArgumentNullException.ThrowIfNull(machineKeyProvider);
        ArgumentNullException.ThrowIfNull(variantSelector);
        ArgumentNullException.ThrowIfNull(runtimeStore);
        ArgumentNullException.ThrowIfNull(hardwareProfiler);
        ArgumentNullException.ThrowIfNull(processVramBudgetProbe);
        ArgumentNullException.ThrowIfNull(launchPolicyFingerprintProvider);
        ArgumentNullException.ThrowIfNull(invalidationEvaluator);
        ArgumentNullException.ThrowIfNull(benchmarkVramAdmission);
        ArgumentNullException.ThrowIfNull(logger);

        _supervisor = supervisor;
        _profileStore = profileStore;
        _snapshotStore = snapshotStore;
        _benchmarkStore = benchmarkStore;
        _harness = harness;
        _fittedArgsParser = fittedArgsParser;
        _ggufModelStore = ggufModelStore;
        _ggufMetadataReader = ggufMetadataReader;
        _machineKeyProvider = machineKeyProvider;
        _variantSelector = variantSelector;
        _runtimeStore = runtimeStore;
        _hardwareProfiler = hardwareProfiler;
        _processVramBudgetProbe = processVramBudgetProbe;
        _launchPolicyFingerprintProvider = launchPolicyFingerprintProvider;
        _invalidationEvaluator = invalidationEvaluator;
        _benchmarkVramAdmission = benchmarkVramAdmission.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ExploreResult> ExploreAsync(string modelName, ModelRole role, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        var filePath = await _ggufModelStore.ResolveModelFilePathAsync(modelName, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(filePath))
        {
            _logger.LogInformation("Rejected explore for a non-local model (Inference Optimizer profiles node-local GGUF models only).");
            return ExploreResult.Fail($"Model '{modelName}' is not a local GGUF; the Inference Optimizer profiles node-local models only.");
        }

        var machineKey = await _machineKeyProvider.GetMachineKeyAsync(ct).ConfigureAwait(false);
        var variant = await _variantSelector.SelectVariantAsync(ct).ConfigureAwait(false);
        var backend = InferenceBackends.FromVariant(variant);
        var installed = await _runtimeStore.ReadAsync(ct).ConfigureAwait(false);
        var build = installed?.Tag is { } tag && !string.IsNullOrWhiteSpace(tag) ? tag : UnknownBuild;
        var metadata = await _ggufMetadataReader.ReadMetadataAsync(filePath, ct).ConfigureAwait(false);

        ResolvedLaunchArguments? draft;
        try
        {
            draft = await _supervisor.RunExclusiveProfilingAsync(modelName,
                role,
                ResolvedLaunchArguments.Explore(),
                enableMetrics: false,
                body: (context, _) => Task.FromResult(result: _fittedArgsParser.TryParseFittedArgs(context.FitParamsOutput,
                    context.StartupOutput,
                    context.SuccessfulLaunchArguments)),
                ct).ConfigureAwait(false);
        }
        catch (LlamaServerProfilingRefusedException exception)
        {
            // A warm role is serving: nothing was spawned and nothing was evicted, so this is a skip, not a failure.
            _logger.LogInformation("Explore skipped for a model in use: {Reason}", exception.Message);
            return ExploreResult.SkippedInUse(exception.Message);
        }

        if (draft is null)
        {
            if (variant != GpuVariant.Cpu)
            {
                const string failure =
                    "llama-fit-params did not produce a concrete replayable context and GPU placement. The live explore spawn remained auto-fit; no partial profile was saved.";
                _logger.LogWarning("{Failure}", failure);
                return ExploreResult.Fail(failure);
            }

            _logger.LogWarning("Machine-readable llama-fit-params output was unavailable for the CPU explore; persisting the GGUF context because CPU profiles do not replay GPU placement.");
        }

        var input = await BuildExploreInputAsync(machineKey,
                modelName,
                role,
                backend,
                build,
                filePath,
                metadata,
                draft,
                ct)
            .ConfigureAwait(false);
        var record = await _profileStore.CreateOrUpdateExploredAsync(input, ct).ConfigureAwait(false);
        return ExploreResult.Ok(ToView(record));
    }

    /// <inheritdoc />
    public Task<BenchmarkResult> BenchmarkAsync(Guid profileId, CancellationToken ct)
    {
        return BenchmarkAsync(profileId, allowPreSpawnVramPressure: false, ct);
    }

    public async Task<BenchmarkResult> BenchmarkAsync(Guid profileId,
        bool allowPreSpawnVramPressure,
        CancellationToken ct)
    {
        var profile = await FindProfileByIdAsync(profileId, ct).ConfigureAwait(false);
        if (profile is null)
        {
            return BenchmarkResult.Fail($"No inference profile with id {profileId}.");
        }

        var filePath = await _ggufModelStore.ResolveModelFilePathAsync(profile.ModelName, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return BenchmarkResult.Fail($"Model '{profile.ModelName}' is no longer a local GGUF; benchmark is node-local only.");
        }

        if (!HasCompletePlacement(profile))
        {
            return BenchmarkResult.Fail($"Profile {profileId} has no complete machine-readable GPU placement; re-explore after llama-fit-params is available.");
        }

        if (!await IsReplayableUnderCurrentSemanticsAsync(profile, filePath, ct).ConfigureAwait(false))
        {
            _ = await _profileStore.MarkStaleAsync(profile.Id, ct).ConfigureAwait(false);
            return BenchmarkResult.Fail($"Profile {profileId} was created under different launch semantics or model/runtime revision; re-explore before benchmarking.");
        }

        ResolvedLaunchArguments replay;
        try
        {
            replay = BuildReplay(profile);
        }
        catch (ArgumentException exception)
        {
            return BenchmarkResult.Fail($"Profile {profileId} holds invalid replay arguments: {exception.Message}");
        }

        var startedAtUtc = NowUnixMs();
        var snapshot = await _snapshotStore.CreateRunningAsync(new ModelFitSnapshotInput(ApprovedImageId: profile.ModelName,
                Operation: ModelFitOperation.Benchmark,
                UseCase: null,
                ProviderName: ProviderName,
                ModelName: profile.ModelName,
                Status: ModelFitRunStatus.Running,
                StartedAtUtc: startedAtUtc),
            ct).ConfigureAwait(false);

        var spec = InferenceBenchmarkSpec.Golden(profile.Backend, profile.CtxSize, _benchmarkVramAdmission) with
        {
            RejectPreSpawnVramPressure = !allowPreSpawnVramPressure
        };
        var role = (ModelRole)profile.Role;

        InferenceBenchmarkMetrics metrics;
        try
        {
            metrics = await _supervisor.RunExclusiveProfilingAsync(profile.ModelName,
                role,
                replay,
                enableMetrics: true,
                body: (context, innerCt) => _harness.RunAsync(context, spec, innerCt),
                ct,
                captureVramBeforeSpawn: innerCt => CapturePreSpawnVramAsync(profile.Backend, innerCt)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (LlamaServerProfilingRefusedException exception)
        {
            // A warm role is serving: nothing was spawned and nothing was evicted. The snapshot opened above is closed
            // as cancelled (no run happened) and the caller is told this was skipped, not that the benchmark failed.
            _logger.LogInformation("Benchmark skipped for profile {ProfileId}: {Reason}", profileId, exception.Message);
            await _snapshotStore.MarkTerminalAsync(snapshot.Id,
                                    ModelFitRunStatus.Cancelled,
                                    exitCode: null,
                                    durationMs: NowUnixMs() - startedAtUtc,
                                    rawJson: null,
                                    stderrExcerpt: exception.Message,
                                    diagnosticsJson: null,
                                    completedAtUtc: NowUnixMs(),
                                    ct)
                                .ConfigureAwait(false);
            return BenchmarkResult.SkippedInUse(exception.Message, snapshot.Id);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Benchmark spawn failed for profile {ProfileId}.", profileId);
            await _snapshotStore.MarkTerminalAsync(snapshot.Id,
                                    ModelFitRunStatus.Failed,
                                    exitCode: null,
                                    durationMs: NowUnixMs() - startedAtUtc,
                                    rawJson: null,
                                    stderrExcerpt: $"Benchmark spawn error: {exception.GetType().Name}.",
                                    diagnosticsJson: null,
                                    completedAtUtc: NowUnixMs(),
                                    ct)
                                .ConfigureAwait(false);
            return BenchmarkResult.Fail($"Benchmark spawn failed: {exception.GetType().Name}.", snapshot.Id);
        }

        var completedAtUtc = NowUnixMs();
        var benchmarkRow = MapBenchmarkInput(profile, metrics);
        await _benchmarkStore.ReplaceForSnapshotAsync(snapshot.Id, [benchmarkRow], ct).ConfigureAwait(false);

        var terminalStatus = metrics.Success ? ModelFitRunStatus.Succeeded : ModelFitRunStatus.Failed;
        await _snapshotStore.MarkTerminalAsync(snapshot.Id,
                                terminalStatus,
                                exitCode: metrics.Success ? 0 : 1,
                                durationMs: completedAtUtc - startedAtUtc,
                                rawJson: metrics.RawJson,
                                stderrExcerpt: metrics.Success ? null : metrics.FailureReason,
                                diagnosticsJson: null,
                                completedAtUtc: completedAtUtc,
                                ct)
                            .ConfigureAwait(false);

        return new BenchmarkResult(metrics.Success,
            metrics.Success ? null : metrics.FailureReason,
            snapshot.Id,
            metrics,
            ToView(profile));
    }

    /// <inheritdoc />
    public async Task<ProfileActionResult> FreezeAsync(Guid profileId, CancellationToken ct)
    {
        var profile = await FindProfileByIdAsync(profileId, ct).ConfigureAwait(false);
        if (profile is null)
        {
            return ProfileActionResult.Fail($"No inference profile with id {profileId}.");
        }

        if (profile.Status != InferenceProfileStatus.Explored)
        {
            return ProfileActionResult.Fail($"Profile {profileId} is {profile.Status}; only an Explored profile can be frozen.");
        }

        if (!HasCompletePlacement(profile))
        {
            return ProfileActionResult.Fail($"Profile {profileId} has no complete machine-readable GPU placement; re-explore after llama-fit-params is available.");
        }

        var filePath = await _ggufModelStore.ResolveModelFilePathAsync(profile.ModelName, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(filePath)
            || !await IsReplayableUnderCurrentSemanticsAsync(profile, filePath, ct).ConfigureAwait(false))
        {
            _ = await _profileStore.MarkStaleAsync(profile.Id, ct).ConfigureAwait(false);
            return ProfileActionResult.Fail($"Profile {profileId} no longer matches the active launch semantics or model/runtime revision; re-explore before freezing.");
        }

        // The freeze gate binds to the EXACT profile revision: only a successful benchmark taken for THIS profile whose
        // recorded launch args still match the profile's current args justifies a freeze. Re-exploring a profile
        // (changing quant/ctx/gpu-layers/kv-types/flash-attn/…) rewrites its args and clears its benchmark
        // justification, so a benchmark captured before the change must NOT freeze the new configuration — the user is
        // told to re-benchmark. NOTE: this stricter gate applies to FUTURE freezes only; already-frozen profiles are
        // deliberately left untouched (no retroactive invalidation).
        var benchmark = await _benchmarkStore.GetLatestSuccessfulForProfileAsync(profile.Id, ct).ConfigureAwait(false);
        if (benchmark is null)
        {
            return ProfileActionResult.Fail($"Profile {profileId} has no successful benchmark for its current configuration; re-benchmark before freezing.");
        }

        if (!BenchmarkMatchesProfile(benchmark, profile))
        {
            return ProfileActionResult.Fail($"Profile {profileId} was re-explored after its last benchmark (launch arguments changed); re-benchmark before freezing.");
        }

        // Store global-free VRAM as the invalidation baseline wherever NVIDIA/NVML provides it. llama.cpp's
        // --list-devices figure is a process-local residency budget under WDDM and can ignore external pressure, so it is
        // recorded independently for diagnostics and must never substitute for missing global-free evidence. CPU profiles
        // deliberately keep no VRAM baseline; unrelated GPU pressure must never invalidate a CPU placement.
        var hardware = await _hardwareProfiler.GetProfileAsync(forceRefresh: true, ct).ConfigureAwait(false);
        long? globalFreeVramAtFreeze = null;
        long? processBudgetVramAtFreeze = null;
        if (!string.Equals(profile.Backend, InferenceBackends.Cpu, StringComparison.OrdinalIgnoreCase))
        {
            globalFreeVramAtFreeze = hardware.AvailableVramBytes;
            processBudgetVramAtFreeze =
                await _processVramBudgetProbe.TryGetProcessBudgetBytesAsync(profile.Backend, ct).ConfigureAwait(false);
        }

        var frozen = await _profileStore.MarkFrozenAsync(profileId,
            benchmark.SnapshotId,
            globalFreeVramAtFreeze,
            processBudgetVramAtFreeze,
            ct).ConfigureAwait(false);
        if (frozen is null)
        {
            // The store gate rejected the transition (not Explored at write time) — surface as a failed result, do not throw.
            return ProfileActionResult.Fail($"Profile {profileId} could not be frozen (it is no longer Explored).");
        }

        return ProfileActionResult.Ok(ToView(frozen));
    }

    private async Task<LlamaServerProfilingVramSnapshot> CapturePreSpawnVramAsync(string backend, CancellationToken ct)
    {
        if (string.Equals(backend, InferenceBackends.Cpu, StringComparison.OrdinalIgnoreCase))
        {
            return new LlamaServerProfilingVramSnapshot(GlobalFreeBytes: null, ProcessBudgetBytes: null);
        }

        var hardware = await _hardwareProfiler.GetProfileAsync(forceRefresh: true, ct).ConfigureAwait(false);
        var processBudget = await _processVramBudgetProbe.TryGetProcessBudgetBytesAsync(backend, ct).ConfigureAwait(false);
        return new LlamaServerProfilingVramSnapshot(hardware.AvailableVramBytes, processBudget);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<InferenceProfileView>> ListProfilesAsync(CancellationToken ct)
    {
        var records = await _profileStore.ListAsync(ct).ConfigureAwait(false);
        return records.Select(ToView).ToList();
    }

    /// <inheritdoc />
    public async Task<ProfileActionResult> InvalidateAsync(Guid profileId, CancellationToken ct)
    {
        var updated = await _profileStore.MarkStaleAsync(profileId, ct).ConfigureAwait(false);
        if (updated is null)
        {
            return ProfileActionResult.Fail($"No inference profile with id {profileId}.");
        }

        return ProfileActionResult.Ok(ToView(updated));
    }

    private async Task<InferenceProfileInput> BuildExploreInputAsync(string machineKey,
        string modelName,
        ModelRole role,
        string backend,
        string build,
        string modelFilePath,
        GgufModelMetadata metadata,
        ResolvedLaunchArguments? draft,
        CancellationToken ct)
    {
        var quant = string.IsNullOrWhiteSpace(metadata.QuantType) ? UnknownQuant : metadata.QuantType;
        var ctxSize = draft?.CtxSize ?? ClampToInt(metadata.ContextLength) ?? DefaultExploreCtxSize;
        var fingerprint = await _launchPolicyFingerprintProvider.CaptureAsync(new InferenceProfileFingerprintInput(modelName,
                (int)role,
                backend,
                modelFilePath,
                ctxSize,
                draft?.NGpuLayers,
                draft?.TensorSplit,
                draft?.OverrideTensor,
                draft?.KvTypeK,
                draft?.KvTypeV,
                draft?.FlashAttn ?? false),
            ct).ConfigureAwait(false);

        return new InferenceProfileInput(MachineKey: machineKey,
            ModelName: modelName,
            Role: (int)role,
            Backend: backend,
            LlamacppBuild: build,
            Quant: quant,
            CtxSize: ctxSize,
            NGpuLayers: draft?.NGpuLayers,
            TensorSplit: draft?.TensorSplit,
            OverrideTensor: draft?.OverrideTensor,
            KvTypeK: draft?.KvTypeK,
            KvTypeV: draft?.KvTypeV,
            FlashAttn: draft?.FlashAttn ?? false,
            NParams: metadata.ParamCount,
            IsMoe: metadata.IsMoe,
            ExpertCount: metadata.ExpertCount,
            LaunchPolicyFingerprintVersion: fingerprint.Version,
            LaunchPolicyFingerprint: fingerprint.Value);
    }

    private static ResolvedLaunchArguments BuildReplay(InferenceProfileRecord profile)
    {
        return ResolvedLaunchArguments.Replay(profile.CtxSize,
            profile.NGpuLayers,
            profile.TensorSplit,
            profile.OverrideTensor,
            profile.KvTypeK,
            profile.KvTypeV,
            profile.FlashAttn);
    }

    private static ModelFitBenchmarkInput MapBenchmarkInput(InferenceProfileRecord profile, InferenceBenchmarkMetrics metrics)
    {
        return new ModelFitBenchmarkInput(ModelName: profile.ModelName,
            ProviderName: ProviderName,
            TokensPerSecond: metrics.TokensPerSecond,
            TtftMs: metrics.TtftMs,
            TotalLatencyMs: metrics.TotalLatencyMs,
            Runs: metrics.Runs,
            RawJson: metrics.RawJson,
            DiagnosticsJson: metrics.DiagnosticsJson,
            PpTokensPerSecond: metrics.PpTokensPerSecond,
            CacheHitRate: metrics.CacheHitRate,
            ToolLoopMs: metrics.ToolLoopMs,
            VramLoadBytes: metrics.VramLoadBytes,
            VramAfterBytes: metrics.VramAfterBytes,
            GlobalFreeVramLoadBytes: metrics.GlobalFreeVramLoadBytes,
            GlobalFreeVramAfterBytes: metrics.GlobalFreeVramAfterBytes,
            ProcessBudgetVramLoadBytes: metrics.ProcessBudgetVramLoadBytes,
            ProcessBudgetVramAfterBytes: metrics.ProcessBudgetVramAfterBytes,
            MinimumGlobalFreeVramBytes: metrics.MinimumGlobalFreeVramBytes,
            MinimumProcessBudgetVramBytes: metrics.MinimumProcessBudgetVramBytes,
            PeakProcessRamBytes: metrics.PeakProcessRamBytes,
            ExternalPressureDetected: metrics.ExternalPressureDetected,
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

    // A benchmark justifies a freeze only when every launch-affecting arg it recorded still matches the profile's
    // current args. The profile-scoped store read already guarantees the row belongs to this ProfileId; this guards the
    // re-explore case, where the profile kept its id and benchmark row but had its args overwritten in place.
    private static bool BenchmarkMatchesProfile(ModelFitBenchmarkRecord benchmark, InferenceProfileRecord profile)
    {
        return benchmark.LlamacppBuild == profile.LlamacppBuild
               && benchmark.Quant == profile.Quant
               && benchmark.CtxSize == profile.CtxSize
               && benchmark.KvType == profile.KvTypeK
               && benchmark.KvTypeV == profile.KvTypeV
               && (benchmark.FlashAttn ?? false) == profile.FlashAttn
               && benchmark.NGpuLayers == profile.NGpuLayers
               && benchmark.TensorSplit == profile.TensorSplit
               && benchmark.OverrideTensor == profile.OverrideTensor
               && benchmark.Backend == profile.Backend
               && benchmark.MachineKey == profile.MachineKey
               && benchmark.LaunchPolicyFingerprintVersion == profile.LaunchPolicyFingerprintVersion
               && string.Equals(benchmark.LaunchPolicyFingerprint, profile.LaunchPolicyFingerprint, StringComparison.Ordinal);
    }

    // A GPU profile is replayable only with a concrete -ngl. Expert placement needs no separate check here: a spawn
    // that kept its experts in system RAM is frozen with the equivalent -ot (the fit parser refuses to draft one
    // otherwise), so -ot is the placement and it already travels through the fingerprint, BenchmarkMatchesProfile and
    // BuildReplay. There is no CpuMoe column to consult and no pre-slice row can carry the decision unrecorded --
    // --cpu-moe was never emitted before this slice.
    private static bool HasCompletePlacement(InferenceProfileRecord profile)
    {
        return string.Equals(profile.Backend, InferenceBackends.Cpu, StringComparison.OrdinalIgnoreCase)
               || profile.NGpuLayers is not null;
    }

    // The single "is this row still replayable under today's semantics" gate, used by BOTH profile-owned replay
    // decisions (benchmark and freeze). Two axes: the versioned fingerprint identity, and the placement axis — a row
    // that would launch an expert-offload model as fully resident is not replayable however well its hash matches, and
    // the serving path reaches the SAME check through IInferenceInvalidationEvaluator.IsStaleAsync. Both callers mark
    // the profile Stale on false, which is the D13 re-explore.
    private async Task<bool> IsReplayableUnderCurrentSemanticsAsync(InferenceProfileRecord profile, string modelFilePath, CancellationToken ct)
    {
        if (profile.LaunchPolicyFingerprintVersion is null || string.IsNullOrWhiteSpace(profile.LaunchPolicyFingerprint))
        {
            return false;
        }

        if (!await _launchPolicyFingerprintProvider.MatchesAsync(profile, modelFilePath, ct).ConfigureAwait(false))
        {
            return false;
        }

        return !await _invalidationEvaluator.ContradictsCurrentPlacementAsync(profile, ct).ConfigureAwait(false);
    }

    private static InferenceProfileView ToView(InferenceProfileRecord record)
    {
        return new InferenceProfileView(record.Id,
            record.ModelName,
            record.Role,
            record.Backend,
            record.LlamacppBuild,
            record.Quant,
            record.CtxSize,
            record.NGpuLayers,
            record.TensorSplit,
            record.OverrideTensor,
            record.KvTypeK,
            record.KvTypeV,
            record.FlashAttn,
            record.NParams,
            record.IsMoe,
            record.ExpertCount,
            record.Status.ToString(),
            record.BenchmarkSnapshotId,
            record.CreatedAtUtc,
            record.UpdatedAtUtc,
            record.LaunchPolicyFingerprintVersion,
            record.LaunchPolicyFingerprint,
            record.GlobalFreeVramAtFreezeBytes,
            record.ProcessBudgetVramAtFreezeBytes);
    }

    private static int? ClampToInt(long? value)
    {
        return value switch
        {
            null => null,
            <= 0 => null,
            > int.MaxValue => int.MaxValue,
            var positive => (int)positive
        };
    }

    private static long NowUnixMs()
    {
        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    // The profile stores key by (machine, model, role, backend), not id; the node holds a handful of profiles, so an
    // id lookup reads the full list (no Persistence-store change) and filters in memory.
    private async Task<InferenceProfileRecord?> FindProfileByIdAsync(Guid profileId, CancellationToken ct)
    {
        var all = await _profileStore.ListAsync(ct).ConfigureAwait(false);
        return all.FirstOrDefault(profile => profile.Id == profileId);
    }
}
