namespace XE_Local_AI_Engine.Client.Endpoints.Images.V1;

// ---------------------------------------------------------------------------
// Image-job request/response DTOs (IImageJobCoordinator)
// ---------------------------------------------------------------------------

/// <summary>
///     Body for <c>POST images/jobs</c>. Carries the text-to-image generation parameters; the coordinator persists the
///     prompt / negative-prompt encrypted at rest and never logs them. Enum-like values are absent — <see cref="Sampler" />
///     is a free-text method name the runtime validates.
/// </summary>
public sealed class CreateImageJobRequest
{
    /// <summary>Registry key of the installed image model to generate with.</summary>
    public required string ModelName { get; init; }

    /// <summary>The positive prompt. Persisted encrypted at rest; never logged.</summary>
    public required string Prompt { get; init; }

    /// <summary>Optional negative prompt. Persisted encrypted at rest; never logged.</summary>
    public string? NegativePrompt { get; init; }

    /// <summary>
    ///     Random seed as a string (precision-safe on the wire — a 64-bit seed serialized as a JSON number would round
    ///     above 2^53). <see langword="null" />/blank requests a runtime-chosen random seed (equivalent to <c>-1</c>);
    ///     any non-blank value must be a base-10 64-bit integer (validated by <see cref="Models.SeedValue" />).
    /// </summary>
    public string? Seed { get; init; }

    /// <summary>Output width in pixels.</summary>
    public int Width { get; init; } = 512;

    /// <summary>Output height in pixels.</summary>
    public int Height { get; init; } = 512;

    /// <summary>Number of diffusion steps.</summary>
    public int Steps { get; init; } = 20;

    /// <summary>Sampling method name; <see langword="null" /> uses the runtime default.</summary>
    public string? Sampler { get; init; }

    /// <summary>Classifier-free-guidance scale.</summary>
    public double CfgScale { get; init; } = 7.0;
}

/// <summary>Route-only request for <c>GET images/jobs/{jobId}</c> and <c>POST images/jobs/{jobId}/cancel</c>.</summary>
public sealed class ImageJobRouteRequest
{
    /// <summary>Route-bound job id.</summary>
    public Guid JobId { get; init; }
}

/// <summary>
///     Wire projection of a persisted image job. <see cref="Status" /> is the <c>ImageJobStatus</c> value's string name
///     (<c>Queued</c>/<c>Generating</c>/<c>Succeeded</c>/<c>Failed</c>/<c>Cancelled</c>). <see cref="Prompt" /> /
///     <see cref="NegativePrompt" /> are the decrypted plaintext — returned only to the authenticated operator, never
///     logged. All timestamps are unix-ms. No storage path is ever surfaced.
/// </summary>
public sealed class ImageJobResponse
{
    public required Guid Id { get; init; }

    public required string ModelName { get; init; }

    public required string Prompt { get; init; }

    public string? NegativePrompt { get; init; }

    /// <summary>The stored seed rendered as a string (precision-safe; see <see cref="CreateImageJobRequest.Seed" />).</summary>
    public required string Seed { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }

    public required int Steps { get; init; }

    public required string Sampler { get; init; }

    public required double CfgScale { get; init; }

    /// <summary>String name of the <c>ImageJobStatus</c> enum (decoupled from the persistence type).</summary>
    public required string Status { get; init; }

    public required long CreatedAtUtc { get; init; }

    public long? StartedAtUtc { get; init; }

    public long? CompletedAtUtc { get; init; }

    public long? DurationMs { get; init; }

    /// <summary>The produced image id (retrievable via <c>GET images/{imageId}</c>); null until succeeded.</summary>
    public Guid? ImageId { get; init; }

    /// <summary>Operator-safe error reason; non-null only when <see cref="Status" /> is <c>Failed</c>.</summary>
    public string? SanitizedError { get; init; }

    public long? CancellationRequestedAtUtc { get; init; }
}

/// <summary>Response envelope for <c>GET images/jobs</c>.</summary>
public sealed class ListImageJobsResponse
{
    public required IReadOnlyList<ImageJobResponse> Items { get; init; }
}

// ---------------------------------------------------------------------------
// Installed image-model DTOs (IImageModelRegistry)
// ---------------------------------------------------------------------------

/// <summary>
///     One present weight file of an installed model's file-set: its role and source file name plus size. The absolute
///     local path is intentionally omitted (never leak a filesystem path).
/// </summary>
public sealed class ImageModelPartResponse
{
    /// <summary>String name of the <c>ImageModelPartRole</c> (<c>Diffusion</c>/<c>Vae</c>/<c>ClipL</c>/<c>ClipG</c>/<c>T5</c>).</summary>
    public required string Role { get; init; }

    /// <summary>The downloaded source file name (leaf only, no path).</summary>
    public required string FileName { get; init; }

    public required long SizeBytes { get; init; }
}

/// <summary>
///     Wire projection of an installed image model (a file-set). <see cref="Family" /> / <see cref="Kind" /> are the
///     string names of the respective enums. <see cref="Parts" /> lists the present weight files (roles), so the UI can
///     show which parts a multi-file model has. No absolute path is surfaced.
/// </summary>
public sealed class ImageModelResponse
{
    public required string ModelName { get; init; }

    public required string RepoId { get; init; }

    /// <summary>String name of the <c>ImageModelFamily</c> enum (<c>Sd15</c>/<c>Sdxl</c>/<c>Sd3</c>/<c>Flux</c>/<c>Unknown</c>).</summary>
    public required string Family { get; init; }

    /// <summary>String name of the <c>ImageModelKind</c> enum (<c>Txt2Img</c>/<c>Edit</c>).</summary>
    public required string Kind { get; init; }

    /// <summary>Total size of every part in bytes.</summary>
    public required long SizeBytes { get; init; }

    /// <summary>The present weight parts making up the model.</summary>
    public required IReadOnlyList<ImageModelPartResponse> Parts { get; init; }

    /// <summary>Unix-ms instant the file-set completed downloading.</summary>
    public required long DownloadedAtUtc { get; init; }
}

/// <summary>Response envelope for <c>GET images/models</c>.</summary>
public sealed class ListImageModelsResponse
{
    public required IReadOnlyList<ImageModelResponse> Items { get; init; }
}

// ---------------------------------------------------------------------------
// Image-model download DTOs (IImageModelStore.EnsureModelAsync — fire-and-forget)
// ---------------------------------------------------------------------------

/// <summary>
///     One requested weight file inside a model's file-set for <c>POST images/models/downloads</c>: which
///     <see cref="Role" /> it fills (<c>Diffusion</c>/<c>Vae</c>/<c>ClipL</c>/<c>ClipG</c>/<c>T5</c>) and the repo-relative
///     file name to download. <see cref="Sha256" /> optionally pins the file when the source exposes a digest.
/// </summary>
public sealed class ImageModelPartDownloadRequest
{
    public required string Role { get; init; }

    public required string FileName { get; init; }

    public string? Sha256 { get; init; }
}

/// <summary>
///     Body for <c>POST images/models/downloads</c>. Ensures a whole image-model file-set is present locally; the
///     download runs detached (fire-and-forget) and progress surfaces via <c>GET images/models</c> presence. A model is a
///     <b>set</b> of weight parts — one for SD1.5, several for FLUX/SD3 — so all parts download together. No path/token is
///     accepted or returned.
/// </summary>
public sealed class StartImageModelDownloadRequest
{
    /// <summary>Registry key — the canonical model name to register the completed set under.</summary>
    public required string ModelName { get; init; }

    /// <summary>Hugging Face repository id the parts are pulled from.</summary>
    public required string RepoId { get; init; }

    /// <summary>String name of the <c>ImageModelFamily</c> the model belongs to (drives which parts are expected).</summary>
    public required string Family { get; init; }

    /// <summary>String name of the <c>ImageModelKind</c>; null defaults to <c>Txt2Img</c>.</summary>
    public string? Kind { get; init; }

    /// <summary>Git revision (commit SHA or branch) to pin; null resolves the default branch.</summary>
    public string? Revision { get; init; }

    /// <summary>The file-set to download — at least one <c>Diffusion</c> part.</summary>
    public required IReadOnlyList<ImageModelPartDownloadRequest> Parts { get; init; }
}

/// <summary>
///     Accepted (202) response for <c>POST images/models/downloads</c>. The download runs in the background; the response
///     echoes the canonical model-name identity to poll the download status by.
/// </summary>
public sealed class StartImageModelDownloadResponse
{
    public required string ModelName { get; init; }

    /// <summary>Always <c>true</c> when the request was accepted and the detached download started.</summary>
    public required bool Accepted { get; init; }

    /// <summary>
    ///     <c>true</c> when a download for the same model name was already in flight and was rejoined instead of started
    ///     a second time (a double-submit is idempotent).
    /// </summary>
    public required bool AlreadyInFlight { get; init; }
}

/// <summary>
///     Snapshot of one tracked image-model download returned by <c>GET images/models/downloads</c>. Carries only
///     sanitized fields — no absolute path, URL, or token.
/// </summary>
public sealed class ImageModelDownloadStatusResponse
{
    public required string ModelName { get; init; }

    /// <summary>Phase string: <c>Running</c>, <c>Completed</c>, <c>Cancelled</c>, or <c>Failed</c>.</summary>
    public required string Phase { get; init; }

    /// <summary>Bytes received so far; <c>null</c> until the first progress event.</summary>
    public long? CompletedBytes { get; init; }

    /// <summary>Total content length in bytes; <c>null</c> when the source did not report one.</summary>
    public long? TotalBytes { get; init; }

    /// <summary>Operator-safe failure reason; non-<c>null</c> only when <see cref="Phase" /> is <c>Failed</c>.</summary>
    public string? SanitizedError { get; init; }

    /// <summary>
    ///     1-based index of the weight file currently transferring within the set; <c>null</c> until the first progress
    ///     event. An image model is a file <b>set</b>, so this is what lets the UI say "part 2 of 3".
    /// </summary>
    public int? PartIndex { get; init; }

    /// <summary>Number of weight files in the set; <c>null</c> until the first progress event.</summary>
    public int? PartCount { get; init; }
}

/// <summary>Response envelope for <c>GET images/models/downloads</c>.</summary>
public sealed class ListImageModelDownloadsResponse
{
    public required IReadOnlyList<ImageModelDownloadStatusResponse> Items { get; init; }
}

/// <summary>Request body for <c>POST images/models/downloads/cancel</c>.</summary>
public sealed class CancelImageModelDownloadRequest
{
    /// <summary>The model name whose in-flight download should be stopped.</summary>
    public required string ModelName { get; init; }
}

/// <summary>Response for <c>POST images/models/downloads/cancel</c>.</summary>
public sealed class CancelImageModelDownloadResponse
{
    /// <summary>Echo of the requested model name, so the caller can match the reply to the row it clicked.</summary>
    public required string ModelName { get; init; }

    /// <summary>
    ///     <c>true</c> when a running download was signalled to stop; <c>false</c> when nothing was in flight (the
    ///     download had already finished, or never started). Both are success — cancelling is idempotent.
    /// </summary>
    public required bool Cancelled { get; init; }
}

/// <summary>Route request for <c>DELETE images/models/{modelName}</c>.</summary>
public sealed class DeleteImageModelRequest
{
    /// <summary>The installed model to remove, from the route.</summary>
    public required string ModelName { get; init; }
}
