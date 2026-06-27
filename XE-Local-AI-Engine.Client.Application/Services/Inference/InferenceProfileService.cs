namespace XE_Local_AI_Engine.Client.Services.Inference;

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
    private readonly IInferenceBenchmarkHarness _harness;
    private readonly ILogger<InferenceProfileService> _logger;
    private readonly IMachineKeyProvider _machineKeyProvider;
    private readonly IInferenceProfileStore _profileStore;
    private readonly IInstalledRuntimeStore _runtimeStore;
    private readonly IModelFitSnapshotStore _snapshotStore;
    private readonly ILlamaServerProcessSupervisor _supervisor;
    private readonly IAvailableVramProbe _vramProbe;
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
        IAvailableVramProbe vramProbe,
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
        ArgumentNullException.ThrowIfNull(vramProbe);
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
        _vramProbe = vramProbe;
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

        var draft = await _supervisor.RunExclusiveProfilingAsync(modelName,
            role,
            ResolvedLaunchArguments.Explore(),
            enableMetrics: false,
            body: (context, _) => Task.FromResult(result: _fittedArgsParser.TryParseFittedArgs(context.StartupOutput)),
            ct).ConfigureAwait(false);

        if (draft is null)
        {
            _logger.LogWarning("Fit banner was unparseable for the explored model; persisting a conservative Explored profile from the GGUF context length.");
        }

        var input = BuildExploreInput(machineKey, modelName, role, backend, build, metadata, draft);
        var record = await _profileStore.CreateOrUpdateExploredAsync(input, ct).ConfigureAwait(false);
        return ExploreResult.Ok(ToView(record));
    }

    /// <inheritdoc />
    public async Task<BenchmarkResult> BenchmarkAsync(Guid profileId, CancellationToken ct)
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

        var spec = InferenceBenchmarkSpec.Golden(profile.Backend, profile.CtxSize);
        var role = (ModelRole)profile.Role;

        InferenceBenchmarkMetrics metrics;
        try
        {
            metrics = await _supervisor.RunExclusiveProfilingAsync(profile.ModelName,
                role,
                replay,
                enableMetrics: true,
                body: (context, innerCt) => _harness.RunAsync(context, spec, innerCt),
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
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

        // The freeze gate: a successful benchmark is the only justification.
        var benchmark = await _snapshotStore.GetLatestSuccessfulSummaryAsync(ModelFitOperation.Benchmark,
                useCase: null,
                providerName: ProviderName,
                modelName: profile.ModelName,
                ct)
            .ConfigureAwait(false);
        if (benchmark is null)
        {
            return ProfileActionResult.Fail($"Profile {profileId} has no successful benchmark; freeze is gated on a passing benchmark.");
        }

        // Re-probe free VRAM at freeze time as the invalidation baseline.
        var freeVramAtFreeze = await _vramProbe.TryGetFreeVramBytesAsync(profile.Backend, ct).ConfigureAwait(false);

        var frozen = await _profileStore.MarkFrozenAsync(profileId, benchmark.Id, freeVramAtFreeze, ct).ConfigureAwait(false);
        if (frozen is null)
        {
            // The store gate rejected the transition (not Explored at write time) — surface as a failed result, do not throw.
            return ProfileActionResult.Fail($"Profile {profileId} could not be frozen (it is no longer Explored).");
        }

        return ProfileActionResult.Ok(ToView(frozen));
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

    private static InferenceProfileInput BuildExploreInput(string machineKey,
        string modelName,
        ModelRole role,
        string backend,
        string build,
        GgufModelMetadata metadata,
        ResolvedLaunchArguments? draft)
    {
        var quant = string.IsNullOrWhiteSpace(metadata.QuantType) ? UnknownQuant : metadata.QuantType;
        var ctxSize = draft?.CtxSize ?? ClampToInt(metadata.ContextLength) ?? DefaultExploreCtxSize;

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
            ExpertCount: metadata.ExpertCount);
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
            DiagnosticsJson: null,
            PpTokensPerSecond: metrics.PpTokensPerSecond,
            CacheHitRate: metrics.CacheHitRate,
            ToolLoopMs: metrics.ToolLoopMs,
            VramLoadBytes: metrics.VramLoadBytes,
            VramAfterBytes: metrics.VramAfterBytes,
            LlamacppBuild: profile.LlamacppBuild,
            Quant: profile.Quant,
            CtxSize: profile.CtxSize,
            KvType: profile.KvTypeK,
            Backend: profile.Backend,
            MachineKey: profile.MachineKey,
            NGpuLayers: profile.NGpuLayers,
            TensorSplit: profile.TensorSplit,
            OverrideTensor: profile.OverrideTensor);
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
            record.FreeVramAtFreezeBytes,
            record.Status.ToString(),
            record.BenchmarkSnapshotId,
            record.CreatedAtUtc,
            record.UpdatedAtUtc);
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
