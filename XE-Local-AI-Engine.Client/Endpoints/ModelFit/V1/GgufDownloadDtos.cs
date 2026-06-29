namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

// ---------------------------------------------------------------------------
// GGUF browse request/response DTOs (Hugging Face discovery passthrough)
// ---------------------------------------------------------------------------

/// <summary>
///     Query-string request for <c>GET model-fit/gguf/browse</c>. <see cref="Query" /> is a free-text repo search term
///     (null returns the trending GGUF repos); <see cref="Limit" /> bounds the result count; <see cref="Sort" /> is one of
///     <c>trending|downloads|likes|lastModified</c> (defaults to trending). No raw command or image reference is accepted.
/// </summary>
public sealed class BrowseGgufRepositoriesRequest
{
    public string? Query { get; init; }

    public int? Limit { get; init; }

    /// <summary>Result ordering — <c>trending|downloads|likes|lastModified</c>; unknown/empty defaults to trending.</summary>
    public string? Sort { get; init; }
}

/// <summary>Sanitized summary of one discovered GGUF repo (no token, no internal URL).</summary>
public sealed class GgufRepositoryResponse
{
    public required string RepoId { get; init; }

    public required bool IsGated { get; init; }

    public required long Downloads { get; init; }

    public required int Likes { get; init; }

    /// <summary>Unix-ms instant the repo was last modified.</summary>
    public required long LastModifiedAtUtc { get; init; }

    public string? License { get; init; }

    public required bool HasUsableGguf { get; init; }

    /// <summary>
    ///     Soft publisher-trust signal: <c>true</c> when the repo's publisher is a known reputable GGUF packager /
    ///     first-party org. Never an exclusion gate — every repo is still returned; when <c>false</c> the UI shows a
    ///     "review before downloading" warning.
    /// </summary>
    public bool IsTrustedPublisher { get; init; }
}

/// <summary>Response envelope for <c>GET model-fit/gguf/browse</c>.</summary>
public sealed class BrowseGgufRepositoriesResponse
{
    public required IReadOnlyList<GgufRepositoryResponse> Items { get; init; }
}

/// <summary>
///     Query-string request for <c>GET model-fit/gguf/inspect</c>. <see cref="RepoId" /> is the <c>org/name</c> repo to
///     inspect for its selectable <c>.gguf</c> files (quants + sizes). No raw command or path is accepted.
/// </summary>
public sealed class InspectGgufRepositoryRequest
{
    public string? RepoId { get; init; }
}

/// <summary>
///     Sanitized per-file row from a repo inspection: the quant a downloader selects plus its size. <see cref="IsDynamic" />
///     flags an Unsloth "Dynamic" (<c>UD-</c>) quant so the picker can badge it. No token, no internal URL, no path.
/// </summary>
public sealed class GgufRepositoryFileResponse
{
    public required string FileName { get; init; }

    public required string Quant { get; init; }

    /// <summary>Whether the quant is an Unsloth Dynamic (<c>UD-</c>) quant (e.g. <c>UD-Q4_K_XL</c>).</summary>
    public required bool IsDynamic { get; init; }

    public required long SizeBytes { get; init; }

    /// <summary>
    ///     Hardware-free quality grade for this quant (<c>NearLossless</c> / <c>SweetSpot</c> / <c>Balanced</c> /
    ///     <c>Small</c> / <c>Minimal</c>) — the <c>GgufQuantTier</c> enum name. The picker renders it as a quality hint.
    /// </summary>
    public required string QualityTier { get; init; }

    /// <summary>
    ///     How this file's size compares to the host's currently-free GPU VRAM (<c>Fits</c> / <c>Tight</c> /
    ///     <c>WontFit</c> / <c>Unknown</c>) — the <c>GgufFitVerdict</c> enum name. <c>Unknown</c> when free VRAM could
    ///     not be probed (no GPU, CPU backend, or dev box without a real probe).
    /// </summary>
    public required string FitVerdict { get; init; }

    /// <summary>Whether this is THE recommended variant for the repo. Exactly one file in a non-empty list is flagged.</summary>
    public required bool IsRecommended { get; init; }
}

/// <summary>Response envelope for <c>GET model-fit/gguf/inspect</c>: the repo id plus its selectable GGUF files.</summary>
public sealed class InspectGgufRepositoryResponse
{
    public required string RepoId { get; init; }

    public required IReadOnlyList<GgufRepositoryFileResponse> Files { get; init; }
}

// ---------------------------------------------------------------------------
// Download request/response DTOs (GGUF store + cancel registry)
// ---------------------------------------------------------------------------

/// <summary>
///     Body for <c>POST model-fit/download</c>. Selects a GGUF file in a repo to download. <see cref="FileName" /> picks
///     the exact <c>.gguf</c> when supplied; otherwise <see cref="Quant" /> (defaulting to the store's configured default)
///     selects the matching quant. <see cref="Revision" /> optionally pins a commit/branch. No path/token is accepted.
/// </summary>
public sealed class StartGgufDownloadRequest
{
    public required string RepoId { get; init; }

    public string? FileName { get; init; }

    public string? Quant { get; init; }

    public string? Revision { get; init; }
}

/// <summary>
///     Accepted response for <c>POST model-fit/download</c>. The download runs in the background; the response returns the
///     canonical model-name identity to track/cancel it by, and whether an existing download for the same model was
///     rejoined.
/// </summary>
public sealed class StartGgufDownloadResponse
{
    public required string ModelName { get; init; }

    public required bool AlreadyInFlight { get; init; }
}

/// <summary>Body for <c>POST model-fit/download/cancel</c>. Identifies the in-flight download by its model name.</summary>
public sealed class CancelGgufDownloadRequest
{
    public required string ModelName { get; init; }
}

/// <summary>
///     Response for <c>POST model-fit/download/cancel</c>. <see cref="Cancelled" /> is <c>true</c> when a cancellable
///     in-flight download was found and signalled; <c>false</c> when none existed (already finished / never started).
/// </summary>
public sealed class CancelGgufDownloadResponse
{
    public required string ModelName { get; init; }

    public required bool Cancelled { get; init; }
}

// ---------------------------------------------------------------------------
// Download-status / progress-polling DTOs (IGgufDownloadCoordinator)
// ---------------------------------------------------------------------------

/// <summary>
///     Snapshot of one tracked GGUF download returned by <c>GET model-fit/gguf/downloads</c> and
///     <c>GET model-fit/gguf/downloads/{modelName}</c>. Carries only sanitized fields — no absolute path, URL, or token.
/// </summary>
public sealed class GgufDownloadStatusResponse
{
    public required string ModelName { get; init; }

    /// <summary>Phase string: <c>Running</c>, <c>Completed</c>, <c>Cancelled</c>, or <c>Failed</c>.</summary>
    public required string Phase { get; init; }

    /// <summary>Bytes received so far; <c>null</c> until the first progress event.</summary>
    public long? CompletedBytes { get; init; }

    /// <summary>Total content length in bytes; <c>null</c> when the server did not send Content-Length.</summary>
    public long? TotalBytes { get; init; }

    /// <summary>Operator-safe error reason; non-<c>null</c> only when <see cref="Phase" /> is <c>Failed</c>.</summary>
    public string? SanitizedError { get; init; }
}

/// <summary>Response envelope for <c>GET model-fit/gguf/downloads</c>.</summary>
public sealed class ListGgufDownloadsResponse
{
    public required IReadOnlyList<GgufDownloadStatusResponse> Items { get; init; }
}

/// <summary>Route-bound request for <c>GET model-fit/gguf/downloads/{modelName}</c>.</summary>
public sealed class GetGgufDownloadStatusRequest
{
    public string? ModelName { get; init; }
}
