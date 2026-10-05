namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.Mappers;

using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Inference;
using XE_Local_AI_Engine.Client.Services.ModelFit;
using XE_Local_AI_Engine.Client.Services.ModelFit.Gguf;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Extension methods translating the application-layer model-fit / advisor records into sanitized endpoint DTOs —
///     the sole point in the Client project that references those record member names.
/// </summary>
/// <remarks>
///     Every projection is sanitized: the recommendation view never carries raw output / stderr / diagnostics, the
///     hardware profile carries no machine identifiers, and running/version projections carry no internal paths.
/// </remarks>
internal static class ModelFitMapper
{
    public static GetLatestRecommendationsResponse ToResponse(this ModelFitLatestRecommendationsView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        return new GetLatestRecommendationsResponse
        {
            HasCache = true,
            SnapshotId = view.SnapshotId,
            Status = view.Status.ToString(),
            UseCase = view.UseCase,
            LastRefreshedAtUtc = view.CompletedAtUtc,
            Recommendations = [.. view.Recommendations.Select(static r => r.ToResponse())]
        };
    }

    /// <summary>The explicit cache-miss response: no cached recommendation snapshot exists for the key.</summary>
    public static GetLatestRecommendationsResponse EmptyCache()
    {
        return new GetLatestRecommendationsResponse
        {
            HasCache = false,
            SnapshotId = null,
            Status = null,
            UseCase = null,
            LastRefreshedAtUtc = null,
            Recommendations = []
        };
    }

    private static ModelFitRecommendationResponse ToResponse(this ModelFitRecommendationView view)
    {
        var record = view.Record;
        var diagnostics = view.Diagnostics;
        return new ModelFitRecommendationResponse
        {
            Rank = record.Rank,
            ModelName = record.ModelName,
            ProviderModelName = record.ProviderModelName,
            Score = record.Score,
            FitLevel = record.FitLevel,
            RunMode = record.RunMode,
            Quantization = record.Quantization,
            EstimatedTokensPerSecond = record.EstimatedTokensPerSecond,
            RequiredRamMb = record.RequiredRamMb,
            RequiredVramMb = record.RequiredVramMb,
            ContextTokens = record.ContextTokens,
            IsInstalled = record.IsInstalled,
            PullModelName = record.PullModelName,
            ReleaseDate = diagnostics.ReleaseDate,
            IsTrustedPublisher = diagnostics.IsTrustedPublisher,
            Section = diagnostics.Section,
            Tier = diagnostics.Tier,
            Tested = diagnostics.Tested,
            CatalogId = diagnostics.CatalogId,
            CatalogDisplayName = diagnostics.CatalogDisplayName,
            CatalogNotes = diagnostics.CatalogNotes,
            ExpertsOffloaded = diagnostics.ExpertsOffloaded,
            GpuGb = diagnostics.GpuGb,
            CpuGb = diagnostics.CpuGb,
            KvQuant = diagnostics.KvQuant,
            KvQuantEstimatedGb = diagnostics.KvQuantEstimatedGb,
            KvQuantHeadroomGb = diagnostics.KvQuantHeadroomGb,
            KvQuantFits = diagnostics.KvQuantFits,
            KvQuantRequiresFlashAttention = diagnostics.KvQuantRequiresFlashAttention,
            KvBytesPerToken = diagnostics.KvBytesPerToken,
            KvBytesPerTokenQuant = diagnostics.KvBytesPerTokenQuant,
            AttentionArch = diagnostics.AttentionArch
        };
    }

    // Sanitized aggregate projection; no hardware identifiers.
    public static HardwareProfileResponse ToResponse(this HardwareProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return new HardwareProfileResponse
        {
            TotalRamBytes = profile.TotalRamBytes,
            AvailableRamBytes = profile.AvailableRamBytes,
            VramBytes = profile.VramBytes,
            VramKnown = profile.VramKnown,
            GpuVendor = profile.GpuVendor.ToWireString(),
            GpuAccelAvailable = profile.GpuAccelAvailable,
            CpuCores = profile.CpuCores,
            FreeDiskBytes = profile.FreeDiskBytes
        };
    }

    /// <summary>Projects the PHYSICAL hardware profile plus the runtime device audit into the wire DTO.</summary>
    /// <remarks>
    ///     The profile fields carry what hardware exists (a GPU may be physically present); the audit fields carry
    ///     runtime truth — whether the selected inference runtime actually uses it or has silently fallen back to the
    ///     CPU.
    /// </remarks>
    public static HardwareProfileResponse ToResponse(this HardwareProfile profile, RuntimeDeviceAuditState audit)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(audit);

        return new HardwareProfileResponse
        {
            TotalRamBytes = profile.TotalRamBytes,
            AvailableRamBytes = profile.AvailableRamBytes,
            VramBytes = profile.VramBytes,
            VramKnown = profile.VramKnown,
            GpuVendor = profile.GpuVendor.ToWireString(),
            GpuAccelAvailable = profile.GpuAccelAvailable,
            CpuCores = profile.CpuCores,
            FreeDiskBytes = profile.FreeDiskBytes,
            InferenceBackend = audit.InferenceBackend,
            GpuExpected = audit.GpuExpected,
            CpuFallback = audit.CpuFallback,
            CpuFallbackReason = audit.Reason,
            CpuFallbackRemediation = audit.Remediation,
            BackendUndeterminedReason = audit.BackendUndeterminedReason,
            GpuOffloadedLayers = audit.LayerPlacement?.OffloadedLayers,
            GpuTotalLayers = audit.LayerPlacement?.TotalLayers,
            GpuExpertsOffloaded = audit.LayerPlacement?.ExpertsOffloaded ?? false,
            GpuOffloadModelName = audit.LayerPlacement?.ModelName,
            GpuOffloadRole = audit.LayerPlacement?.Role.ToWireString()
        };
    }

    public static GgufRepositoryResponse ToResponse(this GgufRepoSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        return new GgufRepositoryResponse
        {
            RepoId = summary.RepoId,
            IsGated = summary.IsGated,
            Downloads = summary.Downloads,
            Likes = summary.Likes,
            LastModifiedAtUtc = summary.LastModified.ToUnixTimeMilliseconds(),
            License = summary.License,
            HasUsableGguf = summary.HasUsableGguf,
            IsTrustedPublisher = summary.IsTrustedPublisher
        };
    }

    public static InspectGgufRepositoryResponse ToResponse(this GgufRepoDetail detail,
        IReadOnlyList<GgufVariantAnnotation> annotations,
        GgufProjectorFile? projector)
    {
        ArgumentNullException.ThrowIfNull(detail);
        ArgumentNullException.ThrowIfNull(annotations);

        // Recommendation rows are keyed by file name (one annotation per inspected file). A file with no annotation
        // (defensive — should not happen) falls back to a hardware-free quality grade and an Unknown verdict.
        var annotationsByFile = annotations.ToDictionary(static annotation => annotation.FileName, StringComparer.Ordinal);

        return new InspectGgufRepositoryResponse
        {
            RepoId = detail.RepoId,
            // Base quants first, smallest-first so the picker leads with the lightest, then the speculative-decoding drafters. A drafter is a fraction of the real
            // weights' size, so a pure size sort would put every drafter at the TOP of the ladder instead of the lightest real quant.
            Files =
            [
                .. detail.Files
                         .OrderBy(static file => GgufDraftModel.IsDraftQuant(file.Quant))
                         .ThenBy(static file => file.SizeBytes)
                         .Select(file => file.ToFileResponse(annotationsByFile.GetValueOrDefault(file.FileName)))
            ],
            // Repo-level, not per file: the download attaches at most this one projector whichever quant is picked.
            HasProjector = projector is not null,
            ProjectorSizeBytes = projector?.SizeBytes
        };
    }

    private static GgufRepositoryFileResponse ToFileResponse(this GgufRepoFile file, GgufVariantAnnotation? annotation)
    {
        var isDraft = GgufDraftModel.IsDraftQuant(file.Quant);
        return new GgufRepositoryFileResponse
        {
            FileName = file.FileName,
            Quant = file.Quant,
            IsDynamic = GgufQuantParser.IsDynamic(file.Quant),
            IsDraft = isDraft,
            SizeBytes = file.SizeBytes,
            QualityTier = (annotation?.QualityTier ?? GgufQuantQuality.Classify(file.Quant)).ToString(),
            FitVerdict = (annotation?.FitVerdict ?? GgufFitVerdict.Unknown).ToString(),
            IsRecommended = annotation?.IsRecommended ?? false
        };
    }

    public static RunningModelResponse ToResponse(this LlamaServerProcessHealth health)
    {
        ArgumentNullException.ThrowIfNull(health);

        return new RunningModelResponse
        {
            ModelName = health.ModelName,
            Role = health.Role.ToWireString(),
            IsResponsive = health.IsResponsive,
            Detail = health.Detail,
            IsBusy = health.IsBusy,
            LastUsedUtc = health.LastUsedUtc,
            IsTransient = health.IsTransient,
            EffectiveContextTokens = health.EffectiveContextTokens,
            ExpertsOffloaded = health.ExpertsOffloaded,
            DetailCode = (health.HasExited, health.IsResponsive) switch
            {
                (true, _) => "exited",
                (false, true) => "responsive",
                _ => "unresponsive"
            }
        };
    }

    /// <summary>
    ///     Projects a resolved <see cref="LlamaBinary" /> to its wire DTO. <paramref name="recommendedTag" /> is the
    ///     effective recommended tag (the editable node setting), threaded in from the endpoint — the mapper no longer
    ///     reads the compiled-in <c>LlamaCppReleasePins.PinnedTag</c> constant for the recommended value.
    /// </summary>
    public static LlamaCppVersionResponse ToResponse(this LlamaCppRuntimeBinaryView binary, string recommendedTag)
    {
        ArgumentNullException.ThrowIfNull(binary);
        ArgumentException.ThrowIfNullOrWhiteSpace(recommendedTag);

        return new LlamaCppVersionResponse
        {
            Version = binary.Version,
            Variant = binary.Variant,
            IsPinnedFallback = binary.IsPinnedFallback,
            PinnedTag = recommendedTag
        };
    }

    /// <summary>
    ///     Projects the dynamic-runtime snapshot + installed-runtime record into the read-only runtime-status DTO.
    ///     <paramref name="recommendedTag" /> is the effective recommended tag (the snapshot's value, falling back to the
    ///     node setting when the snapshot has not been computed yet).
    /// </summary>
    public static LlamaCppRuntimeStatusResponse ToRuntimeStatusResponse(this LlamaCppUpdateSnapshot snapshot,
        InstalledRuntimeState? installed,
        string recommendedTag,
        int runningProcessCount,
        GpuVariant? overrideVariant)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(recommendedTag);

        // A managed source build is NOT on the prebuilt update channel: suppress the catalog-driven "update available"
        // and surface "rebuild available" instead when its tag differs from the engine's current pinned tag.
        var isSourceBuild = installed?.SourceBuildPath is { Length: > 0 };
        var rebuildAvailable = isSourceBuild
                               && installed is not null
                               && !string.Equals(installed.Tag, LlamaCppReleasePins.PinnedTag, StringComparison.Ordinal);

        return new LlamaCppRuntimeStatusResponse
        {
            Installed = installed?.ToInstalledRuntimeResponse(),
            RecommendedTag = recommendedTag,
            UpstreamLatestTag = snapshot.UpstreamLatestTag,
            UpdateAvailable = !isSourceBuild && snapshot.UpdateAvailable,
            IsOffline = snapshot.IsOffline,
            RunningProcessCount = runningProcessCount,
            IsSourceBuild = isSourceBuild,
            RebuildAvailable = rebuildAvailable,
            CheckedAtUtc = snapshot.CheckedAtUtc?.ToUnixTimeMilliseconds(),
            OverrideVariant = overrideVariant?.ToWireString()
        };
    }

    public static LlamaCppRuntimeStatusResponse ToRuntimeStatusResponse(this LlamaCppRuntimeStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        var isSourceBuild = status.Installed?.IsSourceBuild == true;
        return new LlamaCppRuntimeStatusResponse
        {
            Installed = status.Installed?.ToInstalledRuntimeResponse(),
            RecommendedTag = status.RecommendedTag,
            UpstreamLatestTag = status.UpstreamLatestTag,
            UpdateAvailable = !isSourceBuild && status.UpdateAvailable,
            IsOffline = status.IsOffline,
            RunningProcessCount = status.RunningProcessCount,
            IsSourceBuild = isSourceBuild,
            RebuildAvailable = isSourceBuild
                               && status.Installed is not null
                               && !string.Equals(status.Installed.Tag, LlamaCppReleasePins.PinnedTag, StringComparison.Ordinal),
            CheckedAtUtc = status.CheckedAtUtc?.ToUnixTimeMilliseconds(),
            OverrideVariant = status.OverrideVariant?.ToWireString()
        };
    }

    private static LlamaCppInstalledRuntimeResponse ToInstalledRuntimeResponse(this LlamaCppInstalledRuntimeView state) =>
        new()
        {
            Tag = state.Tag,
            Variant = state.Variant,
            Asset = state.Asset,
            InstalledAtUtc = state.InstalledAtUnixTimeMilliseconds,
            IsSourceBuild = state.IsSourceBuild,
            SourceRepository = state.SourceRepository,
            SourceCommit = state.SourceCommit,
            SourceSelection = state.SourceSelection is null ? null : (LlamaCppSourceSelectionDto)state.SourceSelection.Value,
            SourceRevisionMode = state.SourceRevisionMode is null ? null : (LlamaCppSourceRevisionModeDto)state.SourceRevisionMode.Value,
            SourceRequestedCommit = state.SourceRequestedCommit
        };

    private static LlamaCppInstalledRuntimeResponse ToInstalledRuntimeResponse(this InstalledRuntimeState state)
    {
        return new LlamaCppInstalledRuntimeResponse
        {
            Tag = state.Tag,
            Variant = state.Variant.ToWireString(),
            Asset = state.Asset,
            InstalledAtUtc = state.InstalledAtUtc.ToUnixTimeMilliseconds(),
            IsSourceBuild = state.SourceBuildPath is { Length: > 0 },
            SourceRepository = state.SourceRepository,
            SourceCommit = state.SourceCommit,
            SourceSelection = state.SourceSelection is null ? null : (LlamaCppSourceSelectionDto)(int)state.SourceSelection.Value,
            SourceRevisionMode = state.SourceRevisionMode is null ? null : (LlamaCppSourceRevisionModeDto)(int)state.SourceRevisionMode.Value,
            SourceRequestedCommit = state.SourceRequestedCommit
        };
    }

    public static LlamaCppSourceBuildRequest ToContract(this StartLlamaCppSourceBuildRequest request)
    {
        return new LlamaCppSourceBuildRequest
        {
            Backend = request.Backend.ToContract(),
            Source = (LlamaCppSourceSelection)(int)request.Source,
            Repository = request.Repository,
            Commit = request.Commit,
            AcknowledgeCustomSourceRisk = request.AcknowledgeCustomSourceRisk
        };
    }

    public static LlamaCppSourceBackend ToContract(this LlamaCppSourceBackendDto backend)
    {
        return (LlamaCppSourceBackend)(int)backend;
    }

    public static LlamaCppSourceBuildPrerequisitesResponse ToResponse(this LlamaCppSourceBuildPrerequisiteReport report,
        LlamaCppSourceBackend backend)
    {
        return new LlamaCppSourceBuildPrerequisitesResponse
        {
            Backend = (LlamaCppSourceBackendDto)(int)backend,
            CanBuild = report.CanBuild,
            Items =
            [
                .. report.Items.Select(static item => new LlamaCppSourceBuildPrerequisiteItemResponse
                {
                    Key = item.Key,
                    Satisfied = item.Satisfied,
                    Detail = item.Detail
                })
            ]
        };
    }

    public static LlamaCppSourceBuildStatusResponse ToResponse(this LlamaCppSourceBuildStatus status)
    {
        return new LlamaCppSourceBuildStatusResponse
        {
            Phase = status.Phase.ToString(),
            IsRunning = status.IsRunning,
            Terminal = status.Terminal,
            LogStartSequence = status.LogStartSequence,
            LogLines = status.LogLines,
            SanitizedError = status.SanitizedError,
            CurrentBuild = status.CurrentBuild?.ToResponse(),
            StartedAtUtc = status.StartedAtUtc?.ToUnixTimeMilliseconds(),
            CompletedAtUtc = status.CompletedAtUtc?.ToUnixTimeMilliseconds()
        };
    }

    private static LlamaCppSourceBuildDescriptorResponse ToResponse(this LlamaCppSourceBuildDescriptor descriptor)
    {
        return new LlamaCppSourceBuildDescriptorResponse
        {
            BuildId = descriptor.BuildId,
            Backend = descriptor.Variant switch
            {
                GpuVariant.Cpu => LlamaCppSourceBackendDto.Cpu,
                GpuVariant.Vulkan => LlamaCppSourceBackendDto.Vulkan,
                GpuVariant.Cuda => LlamaCppSourceBackendDto.Cuda,
                _ => throw new ArgumentOutOfRangeException(nameof(descriptor), descriptor.Variant, "Unknown source-build variant.")
            },
            Source = (LlamaCppSourceSelectionDto)(int)descriptor.Source,
            Repository = descriptor.Repository,
            RevisionMode = (LlamaCppSourceRevisionModeDto)(int)descriptor.RevisionMode,
            RequestedCommit = descriptor.RequestedCommit,
            ResolvedCommit = descriptor.ResolvedCommit
        };
    }

    /// <summary>Projects the registry's current acquisition snapshot to its wire DTO.</summary>
    /// <remarks>
    ///     A field-for-field copy rather than a projection: the hydrate response and the hub push must stay the same
    ///     shape so the client reconciles both through one <c>Sequence</c> comparison. The payload is already sanitized
    ///     by the registry.
    /// </remarks>
    public static RuntimeAcquisitionStatusResponse ToResponse(this LlamaCppRuntimeAcquisitionStatus statusEvent)
    {
        ArgumentNullException.ThrowIfNull(statusEvent);

        return new RuntimeAcquisitionStatusResponse
        {
            Sequence = statusEvent.Sequence,
            Phase = statusEvent.Phase,
            Variant = statusEvent.Variant,
            Tag = statusEvent.Tag,
            CompletedBytes = statusEvent.CompletedBytes,
            TotalBytes = statusEvent.TotalBytes,
            StepIndex = statusEvent.StepIndex,
            StepCount = statusEvent.StepCount,
            SanitizedError = statusEvent.SanitizedError
        };
    }

    /// <summary>
    ///     Projects an application-layer <see cref="InferenceProfileView" /> to its wire DTO. The view already omits the
    ///     local-only machine key; this projection only normalizes the numeric role to its lowercase wire token.
    /// </summary>
    public static InferenceProfileViewDto ToDto(this InferenceProfileView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        return new InferenceProfileViewDto
        {
            Id = view.Id,
            ModelName = view.ModelName,
            Role = ((ModelRole)view.Role).ToWireString(),
            Backend = view.Backend,
            LlamacppBuild = view.LlamacppBuild,
            Quant = view.Quant,
            CtxSize = view.CtxSize,
            NGpuLayers = view.NGpuLayers,
            TensorSplit = view.TensorSplit,
            OverrideTensor = view.OverrideTensor,
            KvTypeK = view.KvTypeK,
            KvTypeV = view.KvTypeV,
            FlashAttn = view.FlashAttn,
            NParams = view.NParams,
            IsMoe = view.IsMoe,
            ExpertCount = view.ExpertCount,
            LaunchPolicyFingerprintVersion = view.LaunchPolicyFingerprintVersion,
            LaunchPolicyFingerprint = view.LaunchPolicyFingerprint,
            GlobalFreeVramAtFreezeBytes = view.GlobalFreeVramAtFreezeBytes,
            ProcessBudgetVramAtFreezeBytes = view.ProcessBudgetVramAtFreezeBytes,
            Status = view.Status,
            BenchmarkSnapshotId = view.BenchmarkSnapshotId,
            CreatedAtUtc = view.CreatedAtUtc,
            UpdatedAtUtc = view.UpdatedAtUtc
        };
    }

    /// <summary>
    ///     Projects the measured <see cref="InferenceBenchmarkMetrics" /> to its wire DTO. The raw <c>/metrics</c> scrape
    ///     (<see cref="InferenceBenchmarkMetrics.RawJson" />) is deliberately dropped — it stays server-side so the
    ///     operator projection remains sanitized.
    /// </summary>
    public static InferenceBenchmarkMetricsDto ToDto(this InferenceBenchmarkMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);

        return new InferenceBenchmarkMetricsDto
        {
            Role = metrics.Role,
            TokensPerSecond = metrics.TokensPerSecond,
            PpTokensPerSecond = metrics.PpTokensPerSecond,
            TtftMs = metrics.TtftMs,
            TotalLatencyMs = metrics.TotalLatencyMs,
            CacheHitRate = metrics.CacheHitRate,
            ToolLoopMs = metrics.ToolLoopMs,
            ItemsPerSecond = metrics.ItemsPerSecond,
            InputTokensPerSecond = metrics.InputTokensPerSecond,
            P50LatencyMs = metrics.P50LatencyMs,
            P95LatencyMs = metrics.P95LatencyMs,
            BatchSize = metrics.BatchSize,
            OutputDimension = metrics.OutputDimension,
            ValuesFinite = metrics.ValuesFinite,
            DeterministicOutput = metrics.DeterministicOutput,
            VramLoadBytes = metrics.VramLoadBytes,
            VramAfterBytes = metrics.VramAfterBytes,
            GlobalFreeVramLoadBytes = metrics.GlobalFreeVramLoadBytes,
            GlobalFreeVramAfterBytes = metrics.GlobalFreeVramAfterBytes,
            ProcessBudgetVramLoadBytes = metrics.ProcessBudgetVramLoadBytes,
            ProcessBudgetVramAfterBytes = metrics.ProcessBudgetVramAfterBytes,
            MinimumGlobalFreeVramBytes = metrics.MinimumGlobalFreeVramBytes,
            MinimumProcessBudgetVramBytes = metrics.MinimumProcessBudgetVramBytes,
            PeakProcessRamBytes = metrics.PeakProcessRamBytes,
            ContextTokensHighWatermark = metrics.ContextTokensHighWatermark,
            ExternalPressureDetected = metrics.ExternalPressureDetected,
            Runs = metrics.Runs
        };
    }

    /// <summary>Parses a wire role string (<c>chat|embedding|reranker</c>) into <see cref="ModelRole" />; null/empty defaults to chat. Unknown → null.</summary>
    public static ModelRole? TryParseRole(string? role)
    {
        // Upper-invariant (CA1308: upper-casing round-trips safely) for case-insensitive matching of the wire tokens.
        return role?.Trim().ToUpperInvariant() switch
        {
            null or "" or "CHAT" => ModelRole.Chat,
            "EMBEDDING" => ModelRole.Embedding,
            "RERANKER" => ModelRole.Reranker,
            _ => null
        };
    }

    /// <summary>Parses a wire variant string (<c>cpu|cuda|vulkan</c>) into <see cref="GpuVariant" />; unknown/empty → null.</summary>
    public static GpuVariant? TryParseVariant(string? variant)
    {
        return variant?.Trim().ToUpperInvariant() switch
        {
            "CPU" => GpuVariant.Cpu,
            "CUDA" => GpuVariant.Cuda,
            "VULKAN" => GpuVariant.Vulkan,
            _ => null
        };
    }

    // Wire enum tokens are lowercase.
    private static string ToWireString(this GpuVendor vendor)
    {
        return vendor switch
        {
            GpuVendor.Nvidia => "nvidia",
            GpuVendor.Amd => "amd",
            GpuVendor.Intel => "intel",
            GpuVendor.None => "none",
            _ => "unknown"
        };
    }

    public static string ToWireString(this ModelRole role)
    {
        return role switch
        {
            ModelRole.Embedding => "embedding",
            ModelRole.Reranker => "reranker",
            _ => "chat"
        };
    }

    private static string ToWireString(this GpuVariant variant)
    {
        return variant switch
        {
            GpuVariant.Cuda => "cuda",
            GpuVariant.Vulkan => "vulkan",
            _ => "cpu"
        };
    }
}
