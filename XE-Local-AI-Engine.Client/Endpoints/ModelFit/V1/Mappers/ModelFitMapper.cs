namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.Mappers;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.ModelFit;
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
            ReleaseDate = ExtractReleaseDate(record.DiagnosticsJson)
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
            HasUsableGguf = summary.HasUsableGguf
        };
    }

    // -----------------------------------------------------------------------
    // GGUF repo inspection (per-file quants) → response
    // -----------------------------------------------------------------------

    public static InspectGgufRepositoryResponse ToResponse(this GgufRepoDetail detail)
    {
        ArgumentNullException.ThrowIfNull(detail);

        return new InspectGgufRepositoryResponse
        {
            RepoId = detail.RepoId,
            // Smallest-first so the picker leads with the lightest quant; the UI can re-sort.
            Files = [.. detail.Files.OrderBy(static file => file.SizeBytes).Select(static file => file.ToFileResponse())]
        };
    }

    private static GgufRepositoryFileResponse ToFileResponse(this GgufRepoFile file)
    {
        return new GgufRepositoryFileResponse
        {
            FileName = file.FileName,
            Quant = file.Quant,
            IsDynamic = GgufQuantParser.IsDynamic(file.Quant),
            SizeBytes = file.SizeBytes
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

        return new LlamaCppRuntimeStatusResponse
        {
            Installed = installed?.ToInstalledRuntimeResponse(),
            RecommendedTag = recommendedTag,
            UpstreamLatestTag = snapshot.UpstreamLatestTag,
            UpdateAvailable = snapshot.UpdateAvailable,
            IsOffline = snapshot.IsOffline,
            RunningProcessCount = runningProcessCount
        };
    }

    private static LlamaCppInstalledRuntimeResponse ToInstalledRuntimeResponse(this InstalledRuntimeState state)
    {
        return new LlamaCppInstalledRuntimeResponse
        {
            Tag = state.Tag,
            Variant = state.Variant.ToWireString(),
            Asset = state.Asset,
            InstalledAtUtc = state.InstalledAtUtc.ToUnixTimeMilliseconds()
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
}
