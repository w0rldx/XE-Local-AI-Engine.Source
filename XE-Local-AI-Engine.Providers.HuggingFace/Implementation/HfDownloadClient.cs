namespace XE_Local_AI_Engine.Providers.HuggingFace.Implementation;

using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.HuggingFace.Contracts;
using XE_Local_AI_Engine.Providers.HuggingFace.Options;

/// <summary>
///     The ranged, resumable, retryable HTTP download against <c>/{repo}/resolve/{rev}/{file}</c>: enforces the hard
///     pre-download disk guard, streams to a <c>.part</c> file, resumes via <c>Range</c>, verifies sha256 against the
///     LFS OID when exposed, atomically renames <c>.part</c> → final, and sets the optional <c>Bearer</c> token for
///     gated repos. Internal — tested via a stubbed handler + injected <see cref="IFreeSpaceProbe" />.
/// </summary>
/// <remarks>
///     Security: the HF token is a secret. It is attached only as an <c>Authorization: Bearer</c> header at request time
///     and is never logged, never placed in an exception/message, and never written to disk by this client.
/// </remarks>
internal sealed class HfDownloadClient
{
    // The file sha256 is surfaced ONLY via the X-Linked-Etag on Hugging Face's resolve response. The plain ETag is NOT a
    // trustworthy sha256: for Xet-backed repos (now HF's default storage) the post-redirect CDN ETag is a content-defined
    // chunking hash that is also 64-hex, so trusting it would guarantee a false HashMismatch. We therefore read
    // X-Linked-Etag from the pre-redirect (302) resolve response via a dedicated no-redirect client and never fall back
    // to ETag.
    private const string LinkedEtagHeader = "X-Linked-Etag";
    private const string RepoCommitHeader = "X-Repo-Commit";
    private const int CopyBufferSize = 128 * 1024;
    private readonly IFreeSpaceProbe _freeSpaceProbe;

    private readonly HttpClient _httpClient;
    private readonly ILogger<HfDownloadClient> _logger;
    private readonly HuggingFaceOptions _options;
    private readonly HttpClient _resolveHttpClient;
    private readonly IHfTokenStore _tokenStore;

    public HfDownloadClient(HttpClient httpClient,
        HttpClient resolveHttpClient,
        IHfTokenStore tokenStore,
        IFreeSpaceProbe freeSpaceProbe,
        HuggingFaceOptions options,
        ILogger<HfDownloadClient> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(resolveHttpClient);
        ArgumentNullException.ThrowIfNull(tokenStore);
        ArgumentNullException.ThrowIfNull(freeSpaceProbe);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClient = httpClient;
        _resolveHttpClient = resolveHttpClient;
        _tokenStore = tokenStore;
        _freeSpaceProbe = freeSpaceProbe;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    ///     Downloads <paramref name="fileName" /> from <paramref name="repoId" />@<paramref name="revision" /> to
    ///     <paramref name="destinationPath" />, resuming any existing <c>.part</c>, verifying the sha256 when the LFS OID
    ///     is exposed, and atomically renaming on success. Returns the resolved commit revision and verified sha256
    ///     (null when no OID was available).
    /// </summary>
    public async Task<HfDownloadResult> DownloadAsync(string repoId,
        string fileName,
        string revision,
        string modelName,
        string destinationPath,
        long expectedSizeBytes,
        IProgress<PullProgress>? progress,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoId);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(revision);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        var partPath = destinationPath + ".part";
        var directory = Path.GetDirectoryName(destinationPath)
                        ?? throw new InvalidOperationException("The model destination path has no directory.");
        Directory.CreateDirectory(directory);

        // Hard disk guard FIRST — refuse before opening any stream so no .part is written when space is short.
        var existingPartBytes = GetExistingPartLength(partPath);
        var remainingBytes = Math.Max(val1: 0, expectedSizeBytes - existingPartBytes);
        EnsureDiskSpace(directory, remainingBytes);

        var requestUri = BuildResolveUri(repoId, revision, fileName);

        // Probe the resolve endpoint ONCE (no-redirect) to capture the true file sha256 from X-Linked-Etag before the
        // CDN redirect hides it. Best-effort: a probe failure leaves expectedSha null (revision-pinned, unverified)
        // rather than blocking the download — the byte GET below still classifies real HTTP failures.
        var expectedSha = await ResolveLinkedShaAsync(requestUri, ct).ConfigureAwait(false);

        var attempt = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return await DownloadOnceAsync(requestUri, expectedSha, modelName, partPath, destinationPath, expectedSizeBytes, progress, ct)
                    .ConfigureAwait(false);
            }
            catch (HuggingFaceDownloadException exception) when (IsTransient(exception.Reason) && attempt < _options.MaxDownloadRetries)
            {
                attempt++;
                _logger.LogWarning("Transient Hugging Face download failure ({Reason}); retry {Attempt}/{Max}.",
                    exception.Reason,
                    attempt,
                    _options.MaxDownloadRetries);
                await Task.Delay(BackoffDelay(attempt), ct).ConfigureAwait(false);
            }
        }
    }

    private async Task<HfDownloadResult> DownloadOnceAsync(Uri requestUri,
        string? expectedSha,
        string modelName,
        string partPath,
        string destinationPath,
        long expectedSizeBytes,
        IProgress<PullProgress>? progress,
        CancellationToken ct)
    {
        var existingPartBytes = GetExistingPartLength(partPath);

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        await ApplyAuthorizationAsync(request, ct).ConfigureAwait(false);
        if (existingPartBytes > 0)
        {
            request.Headers.Range = new RangeHeaderValue(existingPartBytes, to: null);
        }

        HttpResponseMessage response;
        try
        {
            response = await _httpClient
                             .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                             .ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw new HuggingFaceDownloadException(HuggingFaceDownloadFailure.Network,
                "The model download failed due to a network error. Please try again.",
                exception);
        }
        catch (TaskCanceledException exception) when (!ct.IsCancellationRequested)
        {
            throw new HuggingFaceDownloadException(HuggingFaceDownloadFailure.Network,
                "The model download timed out. Please try again.",
                exception);
        }

        using (response)
        {
            // 416 means the .part is already at/over the real length (a prior full-but-unrenamed download, or the
            // upstream file shrank). Re-sending the same Range would 416 forever, so drop the stale .part and surface a
            // transient failure: the next retry sees no .part, sends no Range, and restarts cleanly from byte 0.
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                TryDeleteFile(partPath);
                throw new HuggingFaceDownloadException(HuggingFaceDownloadFailure.Network,
                    "The model download could not resume from the partial file. Retrying from the start.");
            }

            ClassifyStatus(response.StatusCode);

            // If the server ignored our Range and returned 200, restart the .part from scratch (truncate-append below).
            var appending = existingPartBytes > 0 && response.StatusCode == HttpStatusCode.PartialContent;
            // Prefer the sha256 captured from the pre-redirect resolve probe; fall back to a directly-served
            // X-Linked-Etag (e.g. a non-redirecting inline response). Never the plain ETag (see ReadLinkedSha256).
            expectedSha ??= ReadLinkedSha256(response);
            var resolvedRevision = ReadRepoCommit(response) ?? string.Empty;
            var totalBytes = ResolveTotalBytes(response, appending, existingPartBytes, expectedSizeBytes);

            await CopyToPartAsync(response, partPath, appending, modelName, totalBytes, progress, ct).ConfigureAwait(false);

            var verifiedSha = await VerifyHashAsync(partPath, expectedSha, ct).ConfigureAwait(false);

            // Atomic-on-complete: only now is the file a real model file.
            File.Move(partPath, destinationPath, overwrite: true);

            var finalSize = new FileInfo(destinationPath).Length;
            progress?.Report(new PullProgress
            {
                ModelName = modelName,
                Status = "completed",
                TotalBytes = finalSize,
                CompletedBytes = finalSize
            });

            return new HfDownloadResult(destinationPath, finalSize, verifiedSha, resolvedRevision);
        }
    }

    private static async Task CopyToPartAsync(HttpResponseMessage response,
        string partPath,
        bool appending,
        string modelName,
        long? totalBytes,
        IProgress<PullProgress>? progress,
        CancellationToken ct)
    {
        var mode = appending ? FileMode.Append : FileMode.Create;
        var completed = appending ? new FileInfo(partPath).Length : 0L;

        FileStream partStream;
        try
        {
            partStream = new FileStream(partPath, mode, FileAccess.Write, FileShare.None, CopyBufferSize, useAsync: true);
        }
        catch (IOException exception) when (IsDiskFull(exception))
        {
            throw DiskFull(exception);
        }

        var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using (partStream.ConfigureAwait(false))
        {
            await using (source.ConfigureAwait(false))
            {
                var buffer = new byte[CopyBufferSize];
                int read;
                try
                {
                    while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                    {
                        await partStream.WriteAsync(buffer.AsMemory(start: 0, read), ct).ConfigureAwait(false);
                        completed += read;
                        progress?.Report(new PullProgress
                        {
                            ModelName = modelName,
                            Status = "downloading",
                            TotalBytes = totalBytes,
                            CompletedBytes = completed
                        });
                    }

                    // Flush so the .part length on disk is accurate for a later resume.
                    await partStream.FlushAsync(ct).ConfigureAwait(false);
                }
                catch (IOException exception) when (IsDiskFull(exception))
                {
                    // Leave the .part intact for a future resume; never rename a short file to final.
                    throw DiskFull(exception);
                }
            }
        }
    }

    private static async Task<string?> VerifyHashAsync(string partPath, string? expectedSha, CancellationToken ct)
    {
        if (expectedSha is null)
        {
            // Null OID ⇒ revision-pin only (LFS OID was not exposed). Never claim integrity we did not verify.
            return null;
        }

        string actualSha;
        await using (var stream = new FileStream(partPath, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, useAsync: true))
        {
            var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
            actualSha = Convert.ToHexString(hash);
        }

        if (!string.Equals(actualSha, expectedSha, StringComparison.OrdinalIgnoreCase))
        {
            // Corrupt/truncated download — drop the .part so a retry re-downloads cleanly.
            TryDeleteFile(partPath);
            throw new HuggingFaceDownloadException(HuggingFaceDownloadFailure.HashMismatch,
                "The downloaded model file failed its integrity check and was discarded. Please try again.");
        }

        return actualSha;
    }

    private async Task ApplyAuthorizationAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var token = await _tokenStore.GetTokenAsync(ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(token))
        {
            // Token-bearing requests only; the value is never logged or surfaced.
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }

    private void EnsureDiskSpace(string directory, long requiredFileBytes)
    {
        if (requiredFileBytes <= 0)
        {
            return;
        }

        var required = requiredFileBytes + _options.DiskMarginBytes;
        var available = _freeSpaceProbe.GetAvailableFreeBytes(directory);
        if (available < required)
        {
            throw new InsufficientDiskSpaceException(required, available);
        }
    }

    private Uri BuildResolveUri(string repoId, string revision, string fileName)
    {
        var baseUrl = _options.DownloadBaseUrl.TrimEnd('/');
        var encodedFile = string.Join(separator: '/', fileName.Split('/').Select(Uri.EscapeDataString));
        return new Uri($"{baseUrl}/{repoId}/resolve/{Uri.EscapeDataString(revision)}/{encodedFile}");
    }

    private static long GetExistingPartLength(string partPath)
    {
        return File.Exists(partPath) ? new FileInfo(partPath).Length : 0L;
    }

    private static void ClassifyStatus(HttpStatusCode status)
    {
        switch (status)
        {
            case HttpStatusCode.OK:
            case HttpStatusCode.PartialContent:
                return;
            case HttpStatusCode.Unauthorized:
                // No token was accepted → the repo is gated and the caller must configure a token.
                throw new HuggingFaceDownloadException(HuggingFaceDownloadFailure.Gated,
                    "This model is gated and requires a Hugging Face access token. Configure a token and try again.");
            case HttpStatusCode.Forbidden:
                throw new HuggingFaceDownloadException(HuggingFaceDownloadFailure.Unauthorized,
                    "The configured Hugging Face token does not have access to this gated model.");
            case HttpStatusCode.NotFound:
                throw new HuggingFaceDownloadException(HuggingFaceDownloadFailure.NotFound,
                    "The requested model file, repository, or revision was not found.");
            default:
                throw new HuggingFaceDownloadException(HuggingFaceDownloadFailure.Network,
                    $"The model download failed with HTTP status {(int)status}. Please try again.");
        }
    }

    /// <summary>
    ///     Issues a no-redirect <c>HEAD</c> to the resolve URI and returns the file sha256 from <c>X-Linked-Etag</c> on the
    ///     <c>302</c> (or a non-redirecting <c>2xx</c>). Best-effort: any failure — network, an unexpected status, a missing
    ///     or non-sha header — yields <see langword="null" /> so the caller downloads revision-pinned-but-unverified rather
    ///     than failing; real HTTP errors are still surfaced by the byte GET. The HF token rides this same-origin probe for
    ///     gated repos but never reaches the CDN (no redirect is followed).
    /// </summary>
    private async Task<string?> ResolveLinkedShaAsync(Uri requestUri, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, requestUri);
            await ApplyAuthorizationAsync(request, ct).ConfigureAwait(false);
            using var response = await _resolveHttpClient
                                       .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                                       .ConfigureAwait(false);
            return ReadLinkedSha256(response);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            _logger.LogDebug(exception, "Could not probe the Hugging Face sha256 OID; the download will not be hash-verified.");
            return null;
        }
    }

    private static string? ReadLinkedSha256(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues(LinkedEtagHeader, out var linked))
        {
            return null;
        }

        var candidate = linked.FirstOrDefault()?.Trim('"');
        return IsSha256Hex(candidate) ? candidate!.ToUpperInvariant() : null;
    }

    private static string? ReadRepoCommit(HttpResponseMessage response)
    {
        return response.Headers.TryGetValues(RepoCommitHeader, out var values) ? values.FirstOrDefault() : null;
    }

    private static long? ResolveTotalBytes(HttpResponseMessage response, bool appending, long existingPartBytes, long expectedSizeBytes)
    {
        // For a 206 the Content-Range total is authoritative; otherwise fall back to Content-Length / the known size.
        var rangeTotal = response.Content.Headers.ContentRange?.Length;
        if (rangeTotal is > 0)
        {
            return rangeTotal;
        }

        var contentLength = response.Content.Headers.ContentLength;
        if (contentLength is > 0)
        {
            return appending ? existingPartBytes + contentLength : contentLength;
        }

        return expectedSizeBytes > 0 ? expectedSizeBytes : null;
    }

    private static bool IsSha256Hex(string? value)
    {
        return value is { Length: 64 } && value.All(Uri.IsHexDigit);
    }

    private static bool IsTransient(HuggingFaceDownloadFailure reason)
    {
        return reason == HuggingFaceDownloadFailure.Network;
    }

    private static bool IsDiskFull(IOException exception)
    {
        // ENOSPC (Linux/macOS) = 28; ERROR_DISK_FULL (Windows) = 0x70, ERROR_HANDLE_DISK_FULL = 0x27.
        var code = exception.HResult & 0xFFFF;
        return code is 28 or 0x70 or 0x27;
    }

    private static HuggingFaceDownloadException DiskFull(IOException exception)
    {
        return new HuggingFaceDownloadException(HuggingFaceDownloadFailure.DiskFull,
            "The volume ran out of space during the download. The partial file was kept so the download can resume.",
            exception);
    }

    private static TimeSpan BackoffDelay(int attempt)
    {
        return TimeSpan.FromMilliseconds(250 * Math.Min(attempt, val2: 8) * attempt);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup of a rejected partial file.
        }
    }
}

/// <summary>Outcome of a completed download: the final path, verified size, sha256 (when an OID was exposed), and revision.</summary>
internal sealed record HfDownloadResult(string LocalPath, long SizeBytes, string? Sha256, string ResolvedRevision);
