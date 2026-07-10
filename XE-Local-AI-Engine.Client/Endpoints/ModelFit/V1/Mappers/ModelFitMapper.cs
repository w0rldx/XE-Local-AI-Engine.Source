namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.Mappers;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Inference;
using XE_Local_AI_Engine.Client.Services.ModelFit;
using XE_Local_AI_Engine.Client.Services.ModelFit.Gguf;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Extension methods that translate the application-layer model-fit / advisor records into sanitized endpoint DTOs.
///     This is the sole point in the Client project that references those record member names. Every projection is
///     sanitized: the recommendation view never carries raw output / stderr / diagnostics; the hardware profile carries
///     no machine identifiers; running/version projections carry no internal paths.
/// </summary>
internal static class ModelFitMapper
{
    // -----------------------------------------------------------------------
    // Latest recommendations view → response
    // -----------------------------------------------------------------------

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

    private static ModelFitRecommendationResponse ToResponse(this ModelFitRecommendationRecord record)
    {
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
            ReleaseDate = ExtractReleaseDate(record.DiagnosticsJson),
            // Soft publisher-trust signal extracted from the persisted diagnostics blob. The advisor emits
            // is_trusted_publisher per model; when the blob predates that emit we derive it from the model name so
            // pre-existing snapshots still flag trust until the next refresh.
            IsTrustedPublisher = ExtractIsTrustedPublisher(record.DiagnosticsJson, record.ModelName),
            // Catalog-lane fields (locked decision D1/D2/D3), extracted from the same diagnostics blob. A pre-existing
            // snapshot row (predating the catalog lane) has none of these keys and defaults to the "explore" section.
            Section = ExtractString(record.DiagnosticsJson, "section") ?? "explore",
            Tier = ExtractString(record.DiagnosticsJson, "tier"),
            CatalogId = ExtractString(record.DiagnosticsJson, "catalog_id"),
            CatalogDisplayName = ExtractString(record.DiagnosticsJson, "catalog_display_name"),
            CatalogNotes = ExtractString(record.DiagnosticsJson, "catalog_notes"),
            ExpertsOffloaded = ExtractBool(record.DiagnosticsJson, "expert_offload") ?? false,
            GpuGb = ExtractDouble(record.DiagnosticsJson, "gpu_gb"),
            CpuGb = ExtractDouble(record.DiagnosticsJson, "cpu_gb"),
            // Advisory-only quantized-KV estimate (catalog lane; absent for explore rows / incomplete metadata /
            // pre-advisory snapshots). Extracted from the same diagnostics blob; never drives fit or ranking.
            KvQuant = ExtractString(record.DiagnosticsJson, "kv_quant"),
            KvQuantEstimatedGb = ExtractDouble(record.DiagnosticsJson, "kv_quant_estimated_gb"),
            KvQuantHeadroomGb = ExtractDouble(record.DiagnosticsJson, "kv_quant_headroom_gb"),
            KvQuantFits = ExtractBool(record.DiagnosticsJson, "kv_quant_fits"),
            KvQuantRequiresFlashAttention = ExtractBool(record.DiagnosticsJson, "kv_quant_requires_flash_attention")
        };
    }

    // -----------------------------------------------------------------------
    // Hardware profile → response (sanitized: aggregates only, no identifiers)
    // -----------------------------------------------------------------------

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

    // -----------------------------------------------------------------------
    // GGUF repo summary → response
    // -----------------------------------------------------------------------

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

    // -----------------------------------------------------------------------
    // GGUF repo inspection (per-file quants) → response
    // -----------------------------------------------------------------------

    public static InspectGgufRepositoryResponse ToResponse(this GgufRepoDetail detail, IReadOnlyList<GgufVariantAnnotation> annotations)
    {
        ArgumentNullException.ThrowIfNull(detail);
        ArgumentNullException.ThrowIfNull(annotations);

        // Recommendation rows are keyed by file name (one annotation per inspected file). A file with no annotation
        // (defensive — should not happen) falls back to a hardware-free quality grade and an Unknown verdict.
        var annotationsByFile = annotations.ToDictionary(static annotation => annotation.FileName, StringComparer.Ordinal);

        return new InspectGgufRepositoryResponse
        {
            RepoId = detail.RepoId,
            // Smallest-first so the picker leads with the lightest quant; the UI can re-sort.
            Files = [.. detail.Files.OrderBy(static file => file.SizeBytes).Select(file => file.ToFileResponse(annotationsByFile.GetValueOrDefault(file.FileName)))]
        };
    }

    private static GgufRepositoryFileResponse ToFileResponse(this GgufRepoFile file, GgufVariantAnnotation? annotation)
    {
        return new GgufRepositoryFileResponse
        {
            FileName = file.FileName,
            Quant = file.Quant,
            IsDynamic = GgufQuantParser.IsDynamic(file.Quant),
            SizeBytes = file.SizeBytes,
            QualityTier = (annotation?.QualityTier ?? GgufQuantQuality.Classify(file.Quant)).ToString(),
            FitVerdict = (annotation?.FitVerdict ?? GgufFitVerdict.Unknown).ToString(),
            IsRecommended = annotation?.IsRecommended ?? false
        };
    }

    // -----------------------------------------------------------------------
    // Running process health → response
    // -----------------------------------------------------------------------

    public static RunningModelResponse ToResponse(this LlamaServerProcessHealth health)
    {
        ArgumentNullException.ThrowIfNull(health);

        return new RunningModelResponse
        {
            ModelName = health.ModelName,
            Role = health.Role.ToWireString(),
            IsResponsive = health.IsResponsive,
            Detail = health.Detail
        };
    }

    // -----------------------------------------------------------------------
    // llama.cpp binary → response
    // -----------------------------------------------------------------------

    /// <summary>
    ///     Projects a resolved <see cref="LlamaBinary" /> to its wire DTO. <paramref name="recommendedTag" /> is the
    ///     effective recommended tag (the editable node setting), threaded in from the endpoint — the mapper no longer
    ///     reads the compiled-in <c>LlamaCppReleasePins.PinnedTag</c> constant for the recommended value.
    /// </summary>
    public static LlamaCppVersionResponse ToResponse(this LlamaBinary binary, string recommendedTag)
    {
        ArgumentNullException.ThrowIfNull(binary);
        ArgumentException.ThrowIfNullOrWhiteSpace(recommendedTag);

        return new LlamaCppVersionResponse
        {
            Version = binary.Version,
            Variant = binary.Variant.ToWireString(),
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
        int runningProcessCount)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(recommendedTag);

        // A managed source build is NOT on the prebuilt update channel: suppress the catalog-driven "update available"
        // and surface "rebuild available" instead when its tag differs from the engine's current pinned tag. [archMED-2/4]
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
            RebuildAvailable = rebuildAvailable
        };
    }

    private static LlamaCppInstalledRuntimeResponse ToInstalledRuntimeResponse(this InstalledRuntimeState state)
    {
        return new LlamaCppInstalledRuntimeResponse
        {
            Tag = state.Tag,
            Variant = state.Variant.ToWireString(),
            Asset = state.Asset,
            InstalledAtUtc = state.InstalledAtUtc.ToUnixTimeMilliseconds(),
            IsSourceBuild = state.SourceBuildPath is { Length: > 0 }
        };
    }

    // -----------------------------------------------------------------------
    // In-app CUDA build → responses
    // -----------------------------------------------------------------------

    /// <summary>Projects the prerequisite report to its wire DTO.</summary>
    public static CudaBuildPrerequisitesResponse ToResponse(this CudaBuildPrerequisiteReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        return new CudaBuildPrerequisitesResponse
        {
            CanBuild = report.CanBuild,
            Items =
            [
                .. report.Items.Select(static item => new CudaBuildPrerequisiteItemResponse
                {
                    Key = item.Key,
                    Satisfied = item.Satisfied,
                    Detail = item.Detail
                })
            ]
        };
    }

    /// <summary>Projects the build status to its wire DTO.</summary>
    public static CudaBuildStatusResponse ToResponse(this CudaBuildStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        return new CudaBuildStatusResponse
        {
            Phase = status.Phase.ToString(),
            IsRunning = status.IsRunning,
            Terminal = status.Terminal,
            LogLines = status.LogLines,
            SanitizedError = status.SanitizedError,
            Tag = status.Tag
        };
    }

    // -----------------------------------------------------------------------
    // Inference Optimizer profile view → response (sanitized: machine key already omitted by the view)
    // -----------------------------------------------------------------------

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
            FreeVramAtFreezeBytes = view.FreeVramAtFreezeBytes,
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
            TokensPerSecond = metrics.TokensPerSecond,
            PpTokensPerSecond = metrics.PpTokensPerSecond,
            TtftMs = metrics.TtftMs,
            TotalLatencyMs = metrics.TotalLatencyMs,
            CacheHitRate = metrics.CacheHitRate,
            ToolLoopMs = metrics.ToolLoopMs,
            VramLoadBytes = metrics.VramLoadBytes,
            VramAfterBytes = metrics.VramAfterBytes,
            Runs = metrics.Runs
        };
    }

    // -----------------------------------------------------------------------
    // Wire-string → enum parsing (case-insensitive; null/unknown handled by the caller)
    // -----------------------------------------------------------------------

    /// <summary>Parses a wire role string (<c>chat|embedding</c>) into <see cref="ModelRole" />; null/empty defaults to chat. Unknown → null.</summary>
    public static ModelRole? TryParseRole(string? role)
    {
        // Upper-invariant (CA1308: upper-casing round-trips safely) for case-insensitive matching of the wire tokens.
        return role?.Trim().ToUpperInvariant() switch
        {
            null or "" or "CHAT" => ModelRole.Chat,
            "EMBEDDING" => ModelRole.Embedding,
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

    // -----------------------------------------------------------------------
    // Lowercase wire-enum projections (kept in one place; matches the lowercase wire-enum convention)
    // -----------------------------------------------------------------------

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

    /// <summary>
    ///     Pulls ONLY the <c>release_date</c> string out of the persisted diagnostics blob (the rest stays server-side, so
    ///     the row projection remains sanitized). Tolerant: a null/blank/malformed blob, a non-object root, or a missing /
    ///     non-string <c>release_date</c> all yield <c>null</c>.
    /// </summary>
    private static string? ExtractReleaseDate(string? diagnosticsJson)
    {
        if (string.IsNullOrWhiteSpace(diagnosticsJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(diagnosticsJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("release_date", out var releaseDate)
                   && releaseDate.ValueKind == JsonValueKind.String
                ? releaseDate.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    ///     Pulls the <c>is_trusted_publisher</c> boolean out of the persisted diagnostics blob. When the blob carries an
    ///     explicit <c>true</c>/<c>false</c> that value wins. When the property is ABSENT (a row persisted before the
    ///     advisor emitted the signal) or the blob is null/malformed, the trust is derived from the model name so
    ///     pre-existing snapshots are not all silently flagged untrusted until the next refresh regenerates the blob.
    /// </summary>
    private static bool ExtractIsTrustedPublisher(string? diagnosticsJson, string modelName)
    {
        if (!string.IsNullOrWhiteSpace(diagnosticsJson))
        {
            try
            {
                using var document = JsonDocument.Parse(diagnosticsJson);
                if (document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("is_trusted_publisher", out var trusted)
                    && trusted.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    return trusted.ValueKind == JsonValueKind.True;
                }
            }
            catch (JsonException)
            {
                // Fall through to the name-derived signal below.
            }
        }

        return GgufPublisherTrust.IsTrustedPublisher(modelName);
    }

    /// <summary>Pulls a single string property out of the persisted diagnostics blob; tolerant of a null/malformed blob or a missing/non-string property.</summary>
    private static string? ExtractString(string? diagnosticsJson, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(diagnosticsJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(diagnosticsJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty(propertyName, out var value)
                   && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Pulls a single boolean property out of the persisted diagnostics blob; tolerant of a null/malformed blob or a missing/non-boolean property.</summary>
    private static bool? ExtractBool(string? diagnosticsJson, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(diagnosticsJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(diagnosticsJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty(propertyName, out var value)
                   && value.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? value.ValueKind == JsonValueKind.True
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Pulls a single numeric property out of the persisted diagnostics blob; tolerant of a null/malformed blob or a missing/non-numeric property.</summary>
    private static double? ExtractDouble(string? diagnosticsJson, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(diagnosticsJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(diagnosticsJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty(propertyName, out var value)
                   && value.ValueKind == JsonValueKind.Number
                   && value.TryGetDouble(out var number)
                ? number
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
