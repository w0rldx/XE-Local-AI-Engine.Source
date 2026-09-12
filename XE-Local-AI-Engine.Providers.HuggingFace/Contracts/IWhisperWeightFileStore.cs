namespace XE_Local_AI_Engine.Providers.HuggingFace.Contracts;

using XE_Local_AI_Engine.Providers.Abstractions.Contracts;

/// <summary>One Whisper weight file to fetch: where it lives upstream, where it goes, and what it must hash to.</summary>
/// <remarks>
///     The caller supplies the expected size and digest because they come from the transcription provider's own
///     pinned catalogue, not from a repository listing. Nothing here trusts what the repository reports about itself.
/// </remarks>
public sealed record WhisperWeightFileRequest
{
    /// <summary>Hugging Face repository the file lives in.</summary>
    public required string RepoId { get; init; }

    /// <summary>File name inside the repository.</summary>
    public required string FileName { get; init; }

    /// <summary>Absolute destination path.</summary>
    public required string DestinationPath { get; init; }

    /// <summary>Exact expected size in bytes.</summary>
    public required long ExpectedSizeBytes { get; init; }

    /// <summary>Expected lowercase hex SHA256.</summary>
    public required string ExpectedSha256 { get; init; }

    /// <summary>The label progress is reported under, so a two-part download can say which part is moving.</summary>
    public required string ProgressLabel { get; init; }
}

/// <summary>
///     Fetches a single Whisper weight file through the same download client the GGUF and image lanes use, so the
///     transcription provider never sees the Hugging Face client itself — and no new project edge is created for it.
/// </summary>
public interface IWhisperWeightFileStore
{
    /// <summary>
    ///     Downloads the file, or reuses an already-verified one at the destination, and returns its absolute path.
    /// </summary>
    /// <exception cref="XE_Local_AI_Engine.Providers.Abstractions.Gguf.HuggingFaceDownloadException">
    ///     The file could not be fetched, or its digest did not match. The message is sanitized.
    /// </exception>
    Task<string> EnsureFileAsync(WhisperWeightFileRequest request, IProgress<PullProgress>? progress, CancellationToken ct);
}
