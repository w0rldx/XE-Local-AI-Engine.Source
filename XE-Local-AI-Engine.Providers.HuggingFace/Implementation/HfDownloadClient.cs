namespace XE_Local_AI_Engine.Providers.HuggingFace.Implementation;

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.HuggingFace.Contracts;
using XE_Local_AI_Engine.Providers.HuggingFace.Options;
using XE_Local_AI_Engine.Providers.HuggingFace.Telemetry;

/// <summary>
///     The ranged, resumable, retryable HTTP download against <c>/{repo}/resolve/{rev}/{file}</c>: enforces the hard
///     pre-download disk guard, streams to a <c>.part</c> file, resumes via <c>Range</c>, verifies sha256 against the
///     LFS OID when exposed, atomically renames <c>.part</c> → final, and sets the optional <c>Bearer</c> token for
///     gated repos. Internal — tested via a stubbed handler + injected <see cref="IFreeSpaceProbe" />.
///     <para>
///         <b>Parallel range mode.</b> A file of at least <see cref="HuggingFaceOptions.ParallelDownloadMinimumBytes" />
///         is split across <see cref="HuggingFaceOptions.DownloadConnections" /> simultaneous <c>Range</c> streams that
///         all write into the SAME pre-sized <c>.part</c> at their own offsets, because Hugging Face's CDN is
///         per-connection throughput limited and a 30 GB weight file over one socket leaves most of the link idle. Every
///         reliability property of the single-stream path is kept per chunk (read-idle deadline, transient
///         classification, cancellation) and resume is per range via a sidecar of byte cursors. The mode is entered only
///         after a one-byte probe proves the origin actually honours <c>Range</c>; anything else — an unknown size, a
///         length disagreement, a <c>200</c> — falls back to the single stream automatically.
///     </para>
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

    // Upper bound for HuggingFaceOptions.DownloadConnections — see that property for why 16 is the ceiling.
    private const int MaxDownloadConnections = 16;

    // How much a chunk may fetch before its resume cursor is written again. Small enough that an interruption costs at
    // most this much re-downloading, large enough that the sidecar rewrite is noise next to the bytes moved.
    private const long ResumeCursorFlushBytes = 8L * 1024 * 1024;

    // Resume cursors — and the commit that wrote them — kept beside the .part by BOTH download paths. The ".part" tail
    // is deliberate: it makes the file match the "*.part" glob that GgufAcquisitionArtifactStartupReaper already
    // sweeps, so an abandoned download leaves nothing the existing startup cleanup misses.
    private const string RangeSidecarSuffix = ".ranges.part";
    private readonly IHfDownloadMetrics _downloadMetrics;
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
        ILogger<HfDownloadClient> logger,
        IHfDownloadMetrics? downloadMetrics = null)
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
        _downloadMetrics = downloadMetrics ?? NullHfDownloadMetrics.Instance;
    }

    /// <summary>
    ///     Downloads <paramref name="fileName" /> from <paramref name="repoId" />@<paramref name="revision" /> to
    ///     <paramref name="destinationPath" />, resuming any existing <c>.part</c>, verifying the sha256 when the LFS OID
    ///     is exposed, and atomically renaming on success. Returns the resolved commit revision and verified sha256
    ///     (null when the content could not be verified). <paramref name="expectedSha256" /> is a caller-supplied
    ///     discovery digest (HF API <c>lfs.sha256</c>): it is used to integrity-check the stream ONLY as a fallback when
    ///     the resolve endpoint did not expose the LFS OID, so the returned sha256 always reflects content that was
    ///     actually verified — never an unverified digest echoed back.
    /// </summary>
    public async Task<HfDownloadResult> DownloadAsync(string repoId,
        string fileName,
        string revision,
        string modelName,
        string destinationPath,
        long expectedSizeBytes,
        string? expectedSha256,
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
        var existingPartBytes = GetCompletedPartBytes(partPath);
        var remainingBytes = Math.Max(val1: 0, expectedSizeBytes - existingPartBytes);
        EnsureDiskSpace(directory, remainingBytes);

        var requestUri = BuildResolveUri(repoId, revision, fileName);

        // Probe the resolve endpoint ONCE (no-redirect) to capture the true file sha256 from X-Linked-Etag before the
        // CDN redirect hides it. Best-effort: a probe failure leaves expectedSha null (unverified) rather than blocking
        // the download — the byte GET below still classifies real HTTP failures.
        var expectedSha = await ResolveLinkedShaAsync(requestUri, ct).ConfigureAwait(false);

        // The caller's discovery digest is the last-resort verification source when the resolve endpoint exposes no OID.
        // Reject a malformed value (not 64-hex) rather than fail every download against garbage.
        var fallbackSha = IsSha256Hex(expectedSha256) ? expectedSha256!.ToUpperInvariant() : null;

        var attempt = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return await DownloadOnceAsync(requestUri,
                                     commit => BuildResolveUri(repoId, commit, fileName),
                                     expectedSha,
                                     fallbackSha,
                                     modelName,
                                     partPath,
                                     destinationPath,
                                     expectedSizeBytes,
                                     progress,
                                     ct)
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
        Func<string, Uri> resolveAtCommit,
        string? expectedSha,
        string? fallbackSha,
        string modelName,
        string partPath,
        string destinationPath,
        long expectedSizeBytes,
        IProgress<PullProgress>? progress,
        CancellationToken ct)
    {
        var connections = ResolveConnections(expectedSizeBytes);
        if (connections > 1)
        {
            var ranged = await DownloadRangesAsync(requestUri, resolveAtCommit, modelName, partPath, expectedSizeBytes, connections, progress, ct).ConfigureAwait(false);
            if (ranged is not null)
            {
                return await CommitAsync(partPath, destinationPath, modelName, expectedSha ?? ranged.LinkedSha ?? fallbackSha, ranged.Revision, progress, ct)
                    .ConfigureAwait(false);
            }
        }

        // Single stream: either the file was not eligible for parallel mode or the origin turned out not to honour
        // Range. Only bytes a RECORDED commit vouches for may be resumed — appending to a prefix a different commit
        // wrote splices two versions of the file into one that never existed upstream, and there is usually no sha256
        // to catch it. A .part with no record (one written before this client recorded revisions, or one whose ref has
        // since moved) is refetched from byte 0 instead: a bounded one-time cost, paid once per abandoned file.
        var resume = ReadSingleStreamResume(partPath, expectedSizeBytes);
        var existingPartBytes = resume?.Bytes ?? 0L;
        // Pin to the recorded commit where it looks like one, exactly as the parallel path does, so the resumed bytes
        // are asked for at the version that wrote the prefix rather than at whatever the mutable ref points to now.
        var pinned = resume is not null && IsCommitId(resume.Revision);

        using var request = new HttpRequestMessage(HttpMethod.Get, pinned ? resolveAtCommit(resume!.Revision) : requestUri);
        await ApplyAuthorizationAsync(request, ct).ConfigureAwait(false);
        if (existingPartBytes > 0)
        {
            request.Headers.Range = new RangeHeaderValue(existingPartBytes, to: null);
        }

        var response = await SendAsync(request, ct).ConfigureAwait(false);

        using (response)
        {
            // 416 means the .part is already at/over the real length (a prior full-but-unrenamed download, or the
            // upstream file shrank). Re-sending the same Range would 416 forever, so drop the stale .part and surface a
            // transient failure: the next retry sees no .part, sends no Range, and restarts cleanly from byte 0.
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                DiscardPartial(partPath);
                throw new HuggingFaceDownloadException(HuggingFaceDownloadFailure.Network,
                    "The model download could not resume from the partial file. Retrying from the start.");
            }

            // Against a pinned commit a 404 means the PIN is gone, not the file: the recorded commit is no longer
            // resolvable. Drop the partial so the retry asks the caller's ref again instead of failing the download.
            if (pinned && response.StatusCode == HttpStatusCode.NotFound)
            {
                DiscardPartial(partPath);
                throw new HuggingFaceDownloadException(HuggingFaceDownloadFailure.Network,
                    "The model download could not resume from the partial file. Retrying from the start.");
            }

            ClassifyStatus(response.StatusCode);

            // If the server ignored our Range and returned 200, restart the .part from scratch (truncate-append below).
            var appending = existingPartBytes > 0 && response.StatusCode == HttpStatusCode.PartialContent;
            // Prefer the sha256 captured from the pre-redirect resolve probe; fall back to a directly-served
            // X-Linked-Etag (e.g. a non-redirecting inline response), then to the caller's discovery digest. Never the
            // plain ETag (see ReadLinkedSha256).
            expectedSha ??= ReadLinkedSha256(response) ?? fallbackSha;
            var resolvedRevision = ReadRepoCommit(response) ?? string.Empty;

            // Backstop for a pin that could not be applied (or did not hold): the body about to be appended belongs to
            // a different version of the file than the prefix on disk. An origin that names no commit at all compares
            // equal to a record that named none either — the same residual the parallel path accepts, because there is
            // nothing left to detect a move with.
            if (appending && !string.Equals(RangeResumeState.Stamp(resolvedRevision), resume!.Revision, StringComparison.Ordinal))
            {
                DiscardPartial(partPath);
                throw new HuggingFaceDownloadException(HuggingFaceDownloadFailure.Network,
                    "The model changed on the server while it was downloading. Please try again.");
            }

            var totalBytes = ResolveTotalBytes(response, appending, existingPartBytes, expectedSizeBytes);
            var resumeState = RangeResumeState.CreateSingle(partPath + RangeSidecarSuffix,
                expectedSizeBytes,
                resolvedRevision,
                appending ? existingPartBytes : 0L);

            try
            {
                await CopyToPartAsync(response, partPath, appending, modelName, totalBytes, TimeSpan.FromSeconds(_options.DownloadReadIdleTimeoutSeconds), _downloadMetrics, resumeState, progress, ct)
                    .ConfigureAwait(false);
            }
            finally
            {
                // The stream above is disposed (and therefore flushed) by now, so the .part length IS this run's
                // cursor. Recording it on ANY exit is what lets the next attempt trust the prefix it finds.
                resumeState.Persist(index: 0, GetExistingPartLength(partPath));
            }

            return await CommitAsync(partPath, destinationPath, modelName, expectedSha, resolvedRevision, progress, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Verifies the completed <c>.part</c> and publishes it: the sha256 check (when any digest is known), the
    ///     case-insensitive destination guard, and the atomic rename that makes the bytes a real model file. Shared by
    ///     the single-stream and parallel-range paths so integrity and commit semantics cannot drift apart.
    /// </summary>
    private static async Task<HfDownloadResult> CommitAsync(string partPath,
        string destinationPath,
        string modelName,
        string? expectedSha,
        string resolvedRevision,
        IProgress<PullProgress>? progress,
        CancellationToken ct)
    {
        var verifiedSha = await VerifyHashAsync(partPath, expectedSha, ct).ConfigureAwait(false);

        // Atomic-on-complete: only now is the file a real model file.
        if (HasCaseInsensitiveCollision(destinationPath))
        {
            throw new HuggingFaceDownloadException(HuggingFaceDownloadFailure.DestinationConflict,
                "The model download destination already exists.");
        }

        try
        {
            File.Move(partPath, destinationPath, overwrite: false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new HuggingFaceDownloadException(HuggingFaceDownloadFailure.DestinationConflict,
                "The model download could not be committed safely.",
                exception);
        }

        // The .part is now the final file, so any parallel resume cursors describe a file that no longer exists.
        TryDeleteFile(partPath + RangeSidecarSuffix);

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

    /// <summary>
    ///     How many range streams this file gets. Clamped at the point of use — the same convention the other
    ///     concurrency knob in these options follows — so an out-of-range configured value degrades instead of throwing
    ///     mid-download. Returns 1 (the untouched single-stream path) whenever the size is unknown, below the
    ///     worth-it threshold, or the operator asked for one connection.
    /// </summary>
    private int ResolveConnections(long expectedSizeBytes)
    {
        var connections = Math.Clamp(_options.DownloadConnections, min: 1, MaxDownloadConnections);
        return connections == 1 || expectedSizeBytes < Math.Max(val1: 1, _options.ParallelDownloadMinimumBytes)
            ? 1
            : connections;
    }

    /// <summary>
    ///     Fetches the whole file over <paramref name="connections" /> simultaneous <c>Range</c> streams into one
    ///     pre-sized <c>.part</c>, resuming each range from its recorded cursor. Returns the probe result (revision +
    ///     any LFS OID) on success, or <see langword="null" /> when the origin does not honour <c>Range</c> — in which
    ///     case any sparse <c>.part</c> has been discarded and the caller must use the single-stream path.
    ///     <para>
    ///         Every chunk is fetched from the COMMIT the probe resolved, not from the caller's ref: <c>main</c> is a
    ///         mutable branch, and a branch that advances mid-download would otherwise hand different chunks bytes from
    ///         different commits — a file that never existed upstream, committed as genuine whenever no sha256 is known.
    ///     </para>
    /// </summary>
    private async Task<RangeProbe?> DownloadRangesAsync(Uri requestUri,
        Func<string, Uri> resolveAtCommit,
        string modelName,
        string partPath,
        long expectedSizeBytes,
        int connections,
        IProgress<PullProgress>? progress,
        CancellationToken ct)
    {
        var probe = await ProbeRangeSupportAsync(requestUri, expectedSizeBytes, ct).ConfigureAwait(false);
        if (probe is null)
        {
            DiscardRangedPartial(partPath, expectedSizeBytes);
            return null;
        }

        // Pin only to something that looks like a commit id: an unexpected header value must degrade to the caller's
        // ref (and the per-chunk commit check below) rather than turn a working download into a 404 on a bogus ref.
        var pinned = IsCommitId(probe.Revision);
        var chunkUri = pinned ? resolveAtCommit(probe.Revision) : requestUri;

        var total = probe.TotalBytes;
        var chunkSize = Math.Max(val1: 1, (total + connections - 1) / connections);
        var chunkCount = (int)Math.Min(connections, (total + chunkSize - 1) / chunkSize);
        var state = RangeResumeState.Create(partPath + RangeSidecarSuffix, total, chunkCount, chunkSize, GetExistingPartLength(partPath), probe.Revision);

        // ONE handle shared by every chunk. RandomAccess writes are positional and keep no user-mode buffer, so
        // non-overlapping chunks never contend and a cursor written after a completed write can never claim more bytes
        // than the file actually holds.
        using (var handle = File.OpenHandle(partPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, FileOptions.Asynchronous))
        {
            RandomAccess.SetLength(handle, total);

            var context = new ChunkContext(chunkUri, handle, state, modelName, total, chunkSize, progress, probe.Revision);
            using var failureCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            // Chunks capture their own failure rather than faulting, so one dead connection cannot leave a sibling's
            // exception unobserved and the real cause is still the one that surfaces.
            var failures = await Task.WhenAll(Enumerable.Range(start: 0, chunkCount)
                                                        .Select(index => DownloadChunkAsync(context, index, failureCts)))
                                     .ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();
            // Siblings aborted by the first failure report cancellation — noise. Surface what actually broke, so the
            // caller's retry loop sees the transient classification it needs.
            var failure = Array.Find(failures, candidate => candidate is not null and not OperationCanceledException)
                          ?? Array.Find(failures, candidate => candidate is not null);
            if (failure is not null)
            {
                ExceptionDispatchInfo.Capture(failure).Throw();
            }
        }

        // The cursors are deliberately NOT deleted here: they stay until the commit renames the .part away, so a
        // download that finishes its bytes but fails to commit resumes as "already complete" instead of refetching.
        return probe;
    }

    /// <summary>
    ///     Asks for a single byte to learn whether the origin that will actually serve the payload honours <c>Range</c>
    ///     and what it believes the file's length is. Hugging Face redirects to a CDN, so only a real ranged <c>GET</c>
    ///     can answer this — the no-redirect resolve <c>HEAD</c> describes a different server. Returns
    ///     <see langword="null" /> when ranges are ignored or the advertised length disagrees with the size the disk
    ///     guard was sized against; genuine HTTP failures still throw through <see cref="ClassifyStatus" />.
    /// </summary>
    private async Task<RangeProbe?> ProbeRangeSupportAsync(Uri requestUri, long expectedSizeBytes, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        await ApplyAuthorizationAsync(request, ct).ConfigureAwait(false);
        request.Headers.Range = new RangeHeaderValue(from: 0, to: 0);
        using var response = await SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.PartialContent)
        {
            ClassifyStatus(response.StatusCode);
            return null;
        }

        var total = response.Content.Headers.ContentRange?.Length;
        return total == expectedSizeBytes && total > 0
            ? new RangeProbe(total.Value, ReadRepoCommit(response) ?? string.Empty, ReadLinkedSha256(response))
            : null;
    }

    /// <summary>
    ///     Fetches one chunk's outstanding bytes. Returns the exception that stopped it (having cancelled its siblings)
    ///     instead of faulting, so the caller can pick the meaningful failure out of all of them.
    /// </summary>
    private async Task<Exception?> DownloadChunkAsync(ChunkContext context, int index, CancellationTokenSource failureCts)
    {
        var token = failureCts.Token;
        try
        {
            var start = index * context.ChunkSize;
            var end = Math.Min(context.TotalBytes, start + context.ChunkSize);
            var position = start + context.State.CursorOf(index);
            if (position >= end)
            {
                // Finished by an earlier attempt — the entire point of the resume cursors.
                return null;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, context.RequestUri);
            await ApplyAuthorizationAsync(request, token).ConfigureAwait(false);
            request.Headers.Range = new RangeHeaderValue(position, end - 1);
            using var response = await SendAsync(request, token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.PartialContent)
            {
                // Throws for the real HTTP failures. A 2xx that is not 206 means the origin stopped honouring Range
                // after the probe said it would; streaming a whole-file body into a chunk offset would silently corrupt
                // the .part, so treat it as transient and let the retry re-probe.
                ClassifyStatus(response.StatusCode);
                throw new HuggingFaceDownloadException(HuggingFaceDownloadFailure.Network,
                    "The model download server stopped serving byte ranges. Please try again.");
            }

            EnsureChunkDescribesRequest(response, context, position, end);

            var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            await using (source.ConfigureAwait(false))
            {
                await CopyChunkAsync(context, index, source, start, position, end, token).ConfigureAwait(false);
            }

            return null;
        }
        catch (Exception exception) when (exception is HuggingFaceDownloadException
                                              or OperationCanceledException
                                              or IOException
                                              or InsufficientDiskSpaceException)
        {
            // This attempt is over; stop the siblings so the outer retry restarts from the recorded cursors rather than
            // racing a half-cancelled set of streams.
            await failureCts.CancelAsync().ConfigureAwait(false);
            return exception;
        }
    }

    /// <summary>
    ///     Refuses a <c>206</c> that does not describe EXACTLY the bytes that were asked for, at the commit the probe
    ///     resolved. The body is about to be written at the offset we requested, not at the offset the response claims,
    ///     so a shifted range, a different file length, or bytes from a commit the branch has since moved to would be
    ///     assembled into a file that never existed upstream — and committed as genuine whenever no sha256 is known.
    ///     Checked BEFORE the copy, so a rejected response leaves the <c>.part</c> exactly as it was and the transient
    ///     classification lets the retry re-probe, re-pin, and resume from the recorded cursors.
    /// </summary>
    private static void EnsureChunkDescribesRequest(HttpResponseMessage response, ChunkContext context, long position, long end)
    {
        var range = response.Content.Headers.ContentRange;
        if (range?.From != position || range.To != end - 1 || range.Length != context.TotalBytes)
        {
            throw new HuggingFaceDownloadException(HuggingFaceDownloadFailure.Network,
                "The model download server returned the wrong byte range. Please try again.");
        }

        // A chunk that names a different commit means the pin did not hold (or there was nothing to pin to): its bytes
        // belong to a different version of the file than its siblings'.
        var commit = ReadRepoCommit(response);
        if (context.Revision.Length > 0 && !string.IsNullOrEmpty(commit) && !string.Equals(commit, context.Revision, StringComparison.Ordinal))
        {
            throw new HuggingFaceDownloadException(HuggingFaceDownloadFailure.Network,
                "The model changed on the server while it was downloading. Please try again.");
        }
    }

    private async Task CopyChunkAsync(ChunkContext context,
        int index,
        Stream source,
        long start,
        long position,
        long end,
        CancellationToken ct)
    {
        var readIdleTimeout = TimeSpan.FromSeconds(_options.DownloadReadIdleTimeoutSeconds);
        using var idleCts = readIdleTimeout > TimeSpan.Zero
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : null;
        var buffer = new byte[CopyBufferSize];
        var offset = position;
        var persistedAt = position;
        try
        {
            while (offset < end)
            {
                // Never read past this chunk's last byte: a server that over-serves the range must not be allowed to
                // write into the next chunk's territory.
                var wanted = (int)Math.Min(buffer.Length, end - offset);
                var read = await ReadBoundedAsync(source, buffer.AsMemory(start: 0, wanted), idleCts, readIdleTimeout, _downloadMetrics, ct).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                await RandomAccess.WriteAsync(context.Handle, buffer.AsMemory(start: 0, read), offset, ct).ConfigureAwait(false);
                offset += read;

                context.State.Advance(read, context.ModelName, context.Progress);

                if (offset - persistedAt >= ResumeCursorFlushBytes)
                {
                    context.State.Persist(index, offset - start);
                    persistedAt = offset;
                }
            }
        }
        catch (IOException exception) when (IsDiskFull(exception))
        {
            // Leave the .part and its cursors intact for a future resume; never rename a short file to final.
            throw DiskFull(exception);
        }
        finally
        {
            // Record what landed on ANY exit — success, stall, cancel — so the next attempt asks only for the rest.
            context.State.Persist(index, offset - start);
        }

        if (offset < end)
        {
            throw new HuggingFaceDownloadException(HuggingFaceDownloadFailure.Network,
                "The model download ended before the requested byte range was complete. Please try again.");
        }
    }

    /// <summary>Sends a body request with headers-only completion, mapping transport faults to transient failures.</summary>
    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            return await _httpClient
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
    }

    /// <summary>
    ///     Drops a <c>.part</c> left by an earlier parallel attempt. It is pre-sized and sparse, so its LENGTH is not a
    ///     byte count: letting the single-stream path resume from it would skip everything the holes cover. A partial
    ///     whose record says one contiguous run from byte 0 is kept — that is the single-stream path's own file, and it
    ///     is exactly what the fallback below is about to resume.
    /// </summary>
    private static void DiscardRangedPartial(string partPath, long expectedSizeBytes)
    {
        if (!File.Exists(partPath + RangeSidecarSuffix) || ReadSingleStreamResume(partPath, expectedSizeBytes) is not null)
        {
            return;
        }

        DiscardPartial(partPath);
    }

    /// <summary>Drops a partial and the record beside it, so the next attempt starts from byte 0 with nothing to trust.</summary>
    private static void DiscardPartial(string partPath)
    {
        TryDeleteFile(partPath + RangeSidecarSuffix);
        TryDeleteFile(partPath);
    }

    /// <summary>
    ///     How much of the <c>.part</c> the single-stream path may resume, and which commit wrote it. Non-null only for
    ///     a record describing ONE contiguous run whose length still matches what is actually on disk; anything else —
    ///     no partial, no record, a torn one, a record for a different file length, or a pre-sized parallel partial —
    ///     is <see langword="null" />, meaning refetch from byte 0.
    /// </summary>
    private static SingleStreamResume? ReadSingleStreamResume(string partPath, long expectedSizeBytes)
    {
        var partBytes = GetExistingPartLength(partPath);
        if (partBytes <= 0)
        {
            return null;
        }

        return RangeResumeState.TryReadRecord(partPath + RangeSidecarSuffix) is { Cursors.Length: 1 } record
               && record.Total == expectedSizeBytes
               && record.Cursors[0] == partBytes
            ? new SingleStreamResume(partBytes, record.Revision)
            : null;
    }

    /// <summary>
    ///     Bytes genuinely present in the partial file, for the pre-download disk guard. A parallel <c>.part</c> is
    ///     pre-sized to the full length, so only its resume cursors say how much was actually fetched. (The guard runs
    ///     before any commit is known, so a partial that later turns out to be from a moved ref is counted here and
    ///     refetched afterwards — it over-states free space by at most the partial, which the disk margin absorbs.)
    /// </summary>
    private static long GetCompletedPartBytes(string partPath)
    {
        if (!File.Exists(partPath))
        {
            // Cursors outliving their .part describe bytes that are gone; the whole file still has to be fetched.
            return 0L;
        }

        var sidecarPath = partPath + RangeSidecarSuffix;
        if (!File.Exists(sidecarPath))
        {
            return GetExistingPartLength(partPath);
        }

        // A sidecar that will not parse buys nothing: the resume path refetches every range, so the guard must size for
        // the whole file rather than for a pre-sized .part full of holes.
        return RangeResumeState.TryReadRecord(sidecarPath)?.Cursors.Sum() ?? 0L;
    }

    private static bool HasCaseInsensitiveCollision(string destinationPath)
    {
        var directory = Path.GetDirectoryName(destinationPath);
        if (directory is null || !Directory.Exists(directory))
        {
            return false;
        }

        var fileName = Path.GetFileName(destinationPath);
        return Directory.EnumerateFileSystemEntries(directory)
                        .Select(Path.GetFileName)
                        .Any(existing => string.Equals(existing, fileName, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task CopyToPartAsync(HttpResponseMessage response,
        string partPath,
        bool appending,
        string modelName,
        long? totalBytes,
        TimeSpan readIdleTimeout,
        IHfDownloadMetrics downloadMetrics,
        RangeResumeState resumeState,
        IProgress<PullProgress>? progress,
        CancellationToken ct)
    {
        var mode = appending ? FileMode.Append : FileMode.Create;
        var completed = appending ? new FileInfo(partPath).Length : 0L;
        var persistedAt = completed;

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
                using var idleCts = readIdleTimeout > TimeSpan.Zero
                    ? CancellationTokenSource.CreateLinkedTokenSource(ct)
                    : null;
                var buffer = new byte[CopyBufferSize];
                try
                {
                    while (true)
                    {
                        var read = await ReadBoundedAsync(source, buffer, idleCts, readIdleTimeout, downloadMetrics, ct).ConfigureAwait(false);
                        if (read <= 0)
                        {
                            break;
                        }

                        await partStream.WriteAsync(buffer.AsMemory(start: 0, read), ct).ConfigureAwait(false);
                        completed += read;
                        progress?.Report(new PullProgress
                        {
                            ModelName = modelName,
                            Status = "downloading",
                            TotalBytes = totalBytes,
                            CompletedBytes = completed
                        });

                        if (completed - persistedAt >= ResumeCursorFlushBytes)
                        {
                            // Flush BEFORE the cursor: a cursor that outran the buffered bytes would claim a prefix the
                            // file does not hold, and the resume check compares it against the .part's length.
                            await partStream.FlushAsync(ct).ConfigureAwait(false);
                            resumeState.Persist(index: 0, completed);
                            persistedAt = completed;
                        }
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

    /// <summary>
    ///     Reads one buffer's worth of body under a read-idle deadline. <c>ResponseHeadersRead</c> means the HttpClient
    ///     timeout covered only the headers, so without this a CDN that stalls mid-body hangs the copy forever. ONE
    ///     linked CTS is re-armed per read (<c>CancelAfter</c> reschedules its timer) — cheap on the happy path, where it
    ///     never fires; a genuine stall cancels the read, which becomes a TRANSIENT network failure so the caller's
    ///     retry/resume path (<c>MaxDownloadRetries</c> + <c>Range</c> resume) re-attempts from the recorded offset. A
    ///     non-positive timeout disables the bound. A rare spurious fire at the idle boundary is self-healing: it costs
    ///     one resume.
    /// </summary>
    private static async ValueTask<int> ReadBoundedAsync(Stream source,
        Memory<byte> buffer,
        CancellationTokenSource? idleCts,
        TimeSpan readIdleTimeout,
        IHfDownloadMetrics downloadMetrics,
        CancellationToken ct)
    {
        if (idleCts is null)
        {
            return await source.ReadAsync(buffer, ct).ConfigureAwait(false);
        }

        idleCts.CancelAfter(readIdleTimeout);
        try
        {
            return await source.ReadAsync(buffer, idleCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (idleCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            downloadMetrics.RecordReadIdleTimeout();
            throw new HuggingFaceDownloadException(HuggingFaceDownloadFailure.Network,
                "The model download stalled with no data received and was retried.");
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
            // Lowercase hex is the repo-wide canonical form (GgufMemberFingerprint rejects anything else).
            // Uppercase here is what left legacy registry entries mismatching their freshly computed digest.
            actualSha = Convert.ToHexStringLower(hash);
        }

        if (!string.Equals(actualSha, expectedSha, StringComparison.OrdinalIgnoreCase))
        {
            // Corrupt/truncated download — drop the .part, and its resume cursors with it, so a retry starts clean.
            DiscardPartial(partPath);
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

    /// <summary>Whether a value can be used as an immutable ref in a resolve URL — a git object id, not a branch.</summary>
    private static bool IsCommitId(string? value)
    {
        return value is { Length: >= 7 and <= 64 } && value.All(Uri.IsHexDigit);
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
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Best-effort cleanup of a rejected partial file.
        }
    }

    /// <summary>What the one-byte range probe learned: the authoritative length, the pinned revision, and any LFS OID.</summary>
    private sealed record RangeProbe(long TotalBytes, string Revision, string? LinkedSha);

    /// <summary>A single-stream partial the record beside it vouches for: its contiguous length and the writing commit.</summary>
    private sealed record SingleStreamResume(long Bytes, string Revision);

    /// <summary>Everything a chunk needs that is identical for every chunk of one parallel download.</summary>
    private sealed record ChunkContext(Uri RequestUri,
        SafeFileHandle Handle,
        RangeResumeState State,
        string ModelName,
        long TotalBytes,
        long ChunkSize,
        IProgress<PullProgress>? Progress,
        string Revision);

    /// <summary>
    ///     Per-chunk resume cursors plus the aggregate byte count for one parallel download.
    ///     <para>
    ///         <b>Resume scheme.</b> The chunk layout is derived purely from (total length, connection count), so the
    ///         only state worth keeping is how many bytes each chunk has fetched, and which version of the file they
    ///         came from. The single-stream path keeps the SAME record with a single cursor — one contiguous run from
    ///         byte 0 — so both paths answer "which commit wrote these bytes" the same way. That lives in a one-line
    ///         sidecar next to the <c>.part</c>:
    ///         <c>"2 &lt;total&gt; &lt;revision&gt; &lt;cursor0&gt; &lt;cursor1&gt; …"</c>. A cursor is
    ///         written only AFTER the corresponding positional write returned, and <see cref="RandomAccess" /> keeps no
    ///         user-mode buffer, so a cursor can never claim more bytes than the file holds. The line is rewritten in
    ///         place rather than atomically: a crash mid-write leaves an unparseable line, which
    ///         <see cref="TryReadRecord" /> discards — the partial is lost, but a torn cursor is never trusted. A
    ///         mismatched total, chunk count, or revision is discarded the same way, so changing
    ///         <see cref="HuggingFaceOptions.DownloadConnections" /> mid-download is safe and a mutable ref that moved
    ///         between attempts refetches rather than splicing two commits together.
    ///     </para>
    /// </summary>
    private sealed class RangeResumeState
    {
        // Bumped when the line's shape changed (a revision field was added). An older line fails the version check and
        // is discarded as untrusted, which costs one re-download of an abandoned partial.
        private const string FormatVersion = "2";

        // Stands in for "the origin named no commit": the fields are space separated, so no field may be empty.
        private const string UnknownRevision = "-";
        private readonly long[] _cursors;
        private readonly Lock _gate = new();
        private readonly string _revision;
        private readonly string _sidecarPath;
        private readonly long _totalBytes;
        private long _completedBytes;

        private RangeResumeState(string sidecarPath, long totalBytes, string revision, long[] cursors)
        {
            _sidecarPath = sidecarPath;
            _totalBytes = totalBytes;
            _revision = revision;
            _cursors = cursors;
            _completedBytes = cursors.Sum();
        }

        /// <summary>
        ///     Loads the cursors for this file. <paramref name="existingPartBytes" /> is the length of an existing
        ///     <c>.part</c>, used to check that the sidecar still describes the file that is actually on disk.
        /// </summary>
        public static RangeResumeState Create(string sidecarPath,
            long totalBytes,
            int chunkCount,
            long chunkSize,
            long existingPartBytes,
            string revision)
        {
            var stamped = Stamp(revision);
            return new RangeResumeState(sidecarPath,
                totalBytes,
                stamped,
                ResolveCursors(sidecarPath, totalBytes, chunkCount, chunkSize, existingPartBytes, stamped));
        }

        /// <summary>
        ///     The single-stream path's state: one cursor, because that path writes one contiguous run from byte 0. It
        ///     shares the sidecar rather than inventing a second record, so the parallel path can read what wrote a
        ///     prefix it is thinking of adopting — and so both paths clean up after themselves the same way.
        /// </summary>
        public static RangeResumeState CreateSingle(string sidecarPath, long totalBytes, string revision, long cursor)
        {
            return new RangeResumeState(sidecarPath, totalBytes, Stamp(revision), [cursor]);
        }

        /// <summary>The on-disk form of a revision: the fields are space separated, so "the origin named none" needs a token.</summary>
        public static string Stamp(string? revision)
        {
            return string.IsNullOrEmpty(revision) ? UnknownRevision : revision;
        }

        /// <summary>
        ///     Decides how much of the <c>.part</c> the next attempt may keep. Nothing on disk distinguishes a sparse
        ///     pre-sized parallel partial from a contiguous single-stream one, and nothing distinguishes bytes from this
        ///     commit from bytes from the one the ref moved off — so the record beside the <c>.part</c> is the ONLY
        ///     thing that may grant a head start, and only when it still describes the file that is actually there.
        /// </summary>
        private static long[] ResolveCursors(string sidecarPath,
            long totalBytes,
            int chunkCount,
            long chunkSize,
            long existingPartBytes,
            string revision)
        {
            // No record, a torn one, a record for a different file length, or one written by a commit this ref has
            // since moved off: refetch every range. That includes a .part from before this client recorded revisions —
            // deliberately, since there is no way to learn what wrote it, and the cost is one re-download.
            if (TryReadRecord(sidecarPath) is not { } record
                || record.Total != totalBytes
                || !string.Equals(record.Revision, revision, StringComparison.Ordinal))
            {
                return new long[chunkCount];
            }

            // A ranged partial: usable only while the .part is still the pre-sized file those cursors described.
            if (record.Cursors.Length == chunkCount && existingPartBytes == totalBytes && IsUsable(record.Cursors, totalBytes, chunkCount, chunkSize))
            {
                return record.Cursors;
            }

            // A single-stream partial written by the SAME commit: one contiguous run from byte 0, so it seeds every
            // chunk it reaches. Its cursor must still match the file's length, or the run it describes is not there.
            return record.Cursors.Length == 1 && existingPartBytes == record.Cursors[0] && existingPartBytes < totalBytes
                ? SeedFromContiguousPrefix(totalBytes, chunkCount, chunkSize, existingPartBytes)
                : new long[chunkCount];
        }

        private static long[] SeedFromContiguousPrefix(long totalBytes, int chunkCount, long chunkSize, long contiguousPartBytes)
        {
            var cursors = new long[chunkCount];
            for (var index = 0; index < chunkCount; index++)
            {
                var start = index * chunkSize;
                cursors[index] = Math.Clamp(contiguousPartBytes - start, min: 0, Math.Min(totalBytes, start + chunkSize) - start);
            }

            return cursors;
        }

        public long CursorOf(int index)
        {
            lock (_gate)
            {
                return _cursors[index];
            }
        }

        /// <summary>
        ///     Adds <paramref name="count" /> to the aggregate and publishes it. The increment and the delivery happen
        ///     under one lock ON PURPOSE: chunks advance concurrently, and merely making the running total atomic would
        ///     still let a smaller figure reach the consumer after a larger one. Serialising both is what keeps reported
        ///     progress monotonic. Contention is a lock per buffer read, which is nothing next to the bytes moved.
        /// </summary>
        public void Advance(int count, string modelName, IProgress<PullProgress>? progress)
        {
            lock (_gate)
            {
                _completedBytes += count;
                progress?.Report(new PullProgress
                {
                    ModelName = modelName,
                    Status = "downloading",
                    TotalBytes = _totalBytes,
                    CompletedBytes = _completedBytes
                });
            }
        }

        public void Persist(int index, long cursor)
        {
            lock (_gate)
            {
                _cursors[index] = cursor;
                var line = string.Join(' ',
                    [
                        FormatVersion, _totalBytes.ToString(CultureInfo.InvariantCulture), _revision,
                        .. _cursors.Select(value => value.ToString(CultureInfo.InvariantCulture))
                    ]);
                try
                {
                    File.WriteAllText(_sidecarPath, line);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // A cursor that cannot be written costs at most a re-download of this range; it is never a reason to
                    // fail a download that is otherwise progressing.
                }
            }
        }

        public static (long Total, string Revision, long[] Cursors)? TryReadRecord(string sidecarPath)
        {
            string content;
            try
            {
                if (!File.Exists(sidecarPath))
                {
                    return null;
                }

                content = File.ReadAllText(sidecarPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return null;
            }

            var fields = content.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (fields.Length < 4
                || !string.Equals(fields[0], FormatVersion, StringComparison.Ordinal)
                || !long.TryParse(fields[1], CultureInfo.InvariantCulture, out var total))
            {
                return null;
            }

            var cursors = new long[fields.Length - 3];
            for (var index = 0; index < cursors.Length; index++)
            {
                if (!long.TryParse(fields[index + 3], CultureInfo.InvariantCulture, out cursors[index]) || cursors[index] < 0)
                {
                    return null;
                }
            }

            return (total, fields[2], cursors);
        }

        private static bool IsUsable(long[] cursors, long totalBytes, int chunkCount, long chunkSize)
        {
            for (var index = 0; index < chunkCount; index++)
            {
                var start = index * chunkSize;
                if (cursors[index] > Math.Min(totalBytes, start + chunkSize) - start)
                {
                    return false;
                }
            }

            return true;
        }
    }
}

/// <summary>Outcome of a completed download: the final path, verified size, sha256 (when an OID was exposed), and revision.</summary>
internal sealed record HfDownloadResult(string LocalPath, long SizeBytes, string? Sha256, string ResolvedRevision);
