namespace XE_Local_AI_Engine.Providers.HuggingFace.Implementation;

using System.Security.Cryptography;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.HuggingFace.Contracts;

/// <summary>
///     <see cref="IWhisperWeightFileStore" /> over the reused <see cref="HfDownloadClient" />. A Whisper weight is a
///     single file with a size and a digest the caller already knows, so this is the thinnest of the three stores
///     built on that client: no repository listing, no multi-file orchestration, no registry.
/// </summary>
internal sealed class HuggingFaceWhisperWeightStore(HfDownloadClient downloadClient) : IWhisperWeightFileStore
{
    private const string DefaultRevision = "main";

    private readonly HfDownloadClient _downloadClient = downloadClient ?? throw new ArgumentNullException(nameof(downloadClient));

    /// <inheritdoc />
    public async Task<string> EnsureFileAsync(WhisperWeightFileRequest request, IProgress<PullProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Ensure, not download. Every model download fetches the SHARED voice-activity-detection file as its first
        // part, so a node that already installed one model has that file on disk when the next download starts.
        // Without this branch the client re-fetched it, verified it, and then refused to publish it onto the existing
        // destination (HuggingFaceDownloadFailure.DestinationConflict, "The model download destination already
        // exists.") — which failed the whole download before the weights the operator actually asked for were ever
        // requested. The second model an operator installed could therefore never succeed.
        if (File.Exists(request.DestinationPath))
        {
            if (await MatchesExpectedContentAsync(request, ct).ConfigureAwait(false))
            {
                // The destination is already published, so anything still wearing the in-progress suffix beside it is
                // the residue of an attempt that died at the commit step — dead bytes, never resume state.
                DeleteTransferArtifacts(request.DestinationPath);
                progress?.Report(new PullProgress
                {
                    ModelName = request.ProgressLabel,
                    Status = "completed",
                    TotalBytes = request.ExpectedSizeBytes,
                    CompletedBytes = request.ExpectedSizeBytes
                });
                return request.DestinationPath;
            }

            // Present but not what the pinned catalogue describes: a truncated copy, an upstream re-upload, or a
            // foreign file under our name. Remove it and its residue so the download has a clean destination to
            // commit onto instead of failing on the same conflict guard forever.
            TryDelete(request.DestinationPath);
            DeleteTransferArtifacts(request.DestinationPath);
        }

        var result = await _downloadClient.DownloadAsync(request.RepoId,
            request.FileName,
            DefaultRevision,
            request.ProgressLabel,
            request.DestinationPath,
            request.ExpectedSizeBytes,
            request.ExpectedSha256,
            progress,
            ct).ConfigureAwait(false);

        // The download client verifies against the digest it was given, but it reports the digest it actually saw. A
        // mismatch here means the repository served different bytes under the same name — a re-upload, or worse — so
        // the file is removed rather than left on disk looking installed.
        if (result.Sha256 is { Length: > 0 } actual
            && !string.Equals(actual, request.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(request.DestinationPath);
            throw new HuggingFaceDownloadException(HuggingFaceDownloadFailure.HashMismatch,
                "The downloaded transcription model did not match its expected checksum.");
        }

        return result.LocalPath;
    }

    /// <summary>
    ///     Whether the file already at the destination is byte-for-byte the one the pinned catalogue describes. The
    ///     size check is the cheap gate; the digest is what actually decides, so a truncated file of the right length
    ///     cannot pass as installed.
    /// </summary>
    private static async Task<bool> MatchesExpectedContentAsync(WhisperWeightFileRequest request, CancellationToken ct)
    {
        try
        {
            if (new FileInfo(request.DestinationPath).Length != request.ExpectedSizeBytes)
            {
                return false;
            }

            await using var stream = File.OpenRead(request.DestinationPath);
            var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
            return string.Equals(actual, request.ExpectedSha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Unreadable is not the same as verified: fall through to the download, which reports its own failure
            // with a reason rather than this one guessing at the cause.
            return false;
        }
    }

    private static void DeleteTransferArtifacts(string destinationPath)
    {
        var partPath = destinationPath + HfDownloadClient.PartSuffix;
        TryDelete(partPath);
        TryDelete(partPath + HfDownloadClient.RangeSidecarSuffix);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best-effort: the caller is already being told the file is unusable.
        }
    }
}
