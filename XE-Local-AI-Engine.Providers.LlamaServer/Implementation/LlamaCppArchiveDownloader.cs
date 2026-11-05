namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using System.Formats.Tar;
using System.IO.Compression;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.ProcessSupervision;
using AcquisitionReporter = LlamaCppBinaryManager.AcquisitionReporter;

/// <summary>
///     The stateless archive pipeline of <see cref="LlamaCppBinaryManager" />: bounded HTTP download with a stall watchdog, size and SHA256 checks, and extraction into a staging dir.
/// </summary>
/// <remarks>Reports progress through the caller's <see cref="LlamaCppBinaryManager.AcquisitionReporter" />, but never closes it: writing the latched terminal status on every exit stays with the callers.</remarks>
internal sealed class LlamaCppArchiveDownloader
{
    /// <summary>
    ///     How long a runtime download may go without receiving a single byte before it is abandoned as stalled.
    /// </summary>
    /// <remarks>
    ///     Any byte resets the window, so a slow link is never cut off; only a connection that delivers NOTHING for a full
    ///     minute is. Without it a half-open connection holds the acquisition (and the banner) at a fixed percentage forever.
    /// </remarks>
    private static readonly TimeSpan DownloadStallTimeout = TimeSpan.FromSeconds(60);

    /// <summary>The user-safe reason a stalled download surfaces with; also the marker that keeps it through the retry wrapper.</summary>
    internal const string DownloadStalledMessage =
        "The llama.cpp runtime download stalled: no data arrived for 60 seconds. Check the network connection and try again.";

    /// <summary>
    ///     Absolute hard ceiling on a single runtime download: a disk-exhaustion guard against a hostile or buggy server
    ///     streaming an unbounded body.
    /// </summary>
    /// <remarks>
    ///     A prebuilt llama.cpp asset is well under this. Enforced on every download path; the size-aware
    ///     <see cref="LlamaCppBinaryManager.InstallTagAsync(string, string, string, long, GpuVariant, CancellationToken)" /> path tightens it further with the catalog-reported size.
    /// </remarks>
    internal const long MaxDownloadBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>Slack added to the catalog-reported size before aborting an oversized stream (still capped at the ceiling).</summary>
    private const long DownloadSizeSlackBytes = 1L * 1024 * 1024;

    private readonly HttpClient _httpClient;
    private readonly TimeProvider _timeProvider;

    public LlamaCppArchiveDownloader(HttpClient httpClient, TimeProvider timeProvider)
    {
        _httpClient = httpClient;
        _timeProvider = timeProvider;
    }

    /// <summary>
    ///     Shared download, SHA256-verify and extract pipeline; a transient failure or a hash mismatch is discarded and
    ///     retried exactly once. Returns the UNPUBLISHED staging dir (a sibling of <paramref name="variantDir" />).
    /// </summary>
    /// <remarks>
    ///     The expected digest is supplied by the caller — the pinned hash (<see cref="LlamaCppBinaryManager.EnsureBinaryAsync(GpuVariant, CancellationToken)" />) or the
    ///     live publisher digest (<see cref="LlamaCppBinaryManager.InstallTagAsync(string, string, string, long, GpuVariant, CancellationToken)" />) — so both acquisition paths run identical
    ///     verification logic. The caller completes the staged tree (the CUDA cudart pairing), publishes it with
    ///     <see cref="StagedDirectoryPublish.PublishAsync" /> and deletes the staging dir on failure; nothing is visible at
    ///     <paramref name="variantDir" /> until then.
    /// </remarks>
    internal async Task<string> DownloadVerifyExtractAsync(Uri url, string assetName, string expectedSha256, long expectedSize, string variantDir, AcquisitionReporter? reporter, int stepIndex,
        CancellationToken ct)
    {
        var (firstStaging, _) = await DownloadVerifyExtractAttemptAsync(url, assetName, expectedSha256, expectedSize, variantDir, reporter, stepIndex, ct).ConfigureAwait(false);
        if (firstStaging is not null)
        {
            return firstStaging;
        }

        var (secondStaging, secondError) = await DownloadVerifyExtractAttemptAsync(url, assetName, expectedSha256, expectedSize, variantDir, reporter, stepIndex, ct).ConfigureAwait(false);
        if (secondStaging is not null)
        {
            return secondStaging;
        }

        var error = secondError ?? throw new InvalidOperationException("A download attempt without a staging dir must carry its error.");
        throw new LlamaRuntimeException(IsDownloadStall(error)
                ? DownloadStalledMessage
                : "The llama.cpp runtime could not be downloaded or failed integrity verification after a retry. Check the network connection and try again.",
            error);
    }

    /// <summary>True for the stall failure <see cref="DownloadToFileAsync" /> raises, so the retry wrapper keeps its reason instead of a generic one.</summary>
    internal static bool IsDownloadStall(Exception error)
    {
        return error is LlamaRuntimeException { Message: DownloadStalledMessage };
    }

    /// <summary>
    ///     Runs one download → SHA256 verify → extract pass. Returns the staging dir on success, or the non-fatal
    ///     failure cause to drive a single retry. Cancellation propagates rather than being swallowed.
    /// </summary>
    private async Task<DownloadAttempt> DownloadVerifyExtractAttemptAsync(Uri url, string assetName, string expectedSha256, long expectedSize, string variantDir,
        AcquisitionReporter? reporter, int stepIndex,
        CancellationToken ct)
    {
        // Defense-in-depth: even though assetName is allow-list-validated upstream, strip any directory component before
        // it composes a temp path so a future caller can never traverse out of the temp dir.
        var tempArchive = Path.Combine(Path.GetTempPath(), $"llamacpp-{Guid.NewGuid():N}-{Path.GetFileName(assetName)}");
        try
        {
            // Announced before the request so the UI narrates the wait for the response headers too, not just the body.
            reporter?.Report(RuntimeAcquisitionPhase.Downloading, stepIndex);
            await DownloadToFileAsync(url, tempArchive, expectedSize, reporter, stepIndex, ct).ConfigureAwait(false);

            // When the catalog reported a size, the on-disk length must match it exactly before we trust+hash the file.
            if (expectedSize > 0 && new FileInfo(tempArchive).Length != expectedSize)
            {
                return new DownloadAttempt(null, new LlamaRuntimeException("The llama.cpp runtime download did not match its expected size."));
            }

            // Verification and extraction of a few-hundred-MB archive are not instant; without their own phases the UI
            // would sit at 100 % of the byte counter with nothing to explain the remaining wait.
            reporter?.Report(RuntimeAcquisitionPhase.Verifying, stepIndex);
            if (!await FileSha256.MatchesAsync(tempArchive, expectedSha256, ct).ConfigureAwait(false))
            {
                return new DownloadAttempt(null, new LlamaRuntimeException("The llama.cpp runtime download failed integrity verification."));
            }

            reporter?.Report(RuntimeAcquisitionPhase.Extracting, stepIndex);
            return new DownloadAttempt(await ExtractArchiveAsync(tempArchive, assetName, variantDir, ct).ConfigureAwait(false), null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new DownloadAttempt(null, exception);
        }
        finally
        {
            BestEffortFile.TryDeleteFile(tempArchive);
        }
    }

    /// <summary>One download pass: the staging dir on success, otherwise the failure cause.</summary>
    private readonly record struct DownloadAttempt(string? StagingDir, Exception? Error);

    internal async Task DownloadToFileAsync(Uri url, string destination, long expectedSize, AcquisitionReporter? reporter, int stepIndex, CancellationToken ct)
    {
        // Inactivity watchdog: re-armed on every chunk, so it fires only after DownloadStallTimeout with no bytes at all (headers included).
        using var stallCts = new CancellationTokenSource(DownloadStallTimeout, _timeProvider);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, stallCts.Token);
        try
        {
            await DownloadToFileCoreAsync(url, destination, expectedSize, reporter, stepIndex, stallCts, linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (stallCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // A stall is a FAILURE with a reason, never a silent cancel: the caller did not ask to stop.
            throw new LlamaRuntimeException(DownloadStalledMessage, exception);
        }
    }

    private async Task DownloadToFileCoreAsync(Uri url, string destination, long expectedSize, AcquisitionReporter? reporter, int stepIndex, CancellationTokenSource stallCts,
        CancellationToken ct)
    {
        using var response = await _httpClient
                                   .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                                   .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        // Bound the write: when a size is known, allow it plus a small slack; otherwise fall back to the absolute
        // ceiling. A stream that exceeds the bound (a hostile/buggy server) is aborted and the partial file discarded.
        var limit = expectedSize > 0
            ? Math.Min(expectedSize + DownloadSizeSlackBytes, MaxDownloadBytes)
            : MaxDownloadBytes;

        // The determinate total comes from the response headers; the pinned path has no catalog-reported size, so it is
        // simply absent (null) until — and unless — a Content-Length lands, and the UI stays indeterminate meanwhile.
        var totalBytes = response.Content.Headers.ContentLength;

        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

        try
        {
            await using var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);

            var buffer = new byte[81920];
            long written = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                stallCts.CancelAfter(DownloadStallTimeout);
                written += read;
                if (written > limit)
                {
                    throw new LlamaRuntimeException("The llama.cpp runtime download exceeded the maximum allowed size.");
                }

                await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);

                // Reported unconditionally per ~80 KB chunk: the registry owns the push throttle, and duplicating it here
                // would only make the hydrate snapshot lag the bytes actually on disk.
                reporter?.Report(RuntimeAcquisitionPhase.Downloading, stepIndex, written, totalBytes);
            }
        }
        catch
        {
            BestEffortFile.TryDeleteFile(destination);
            throw;
        }
    }

    // Extracts into a temp sibling of variantDir (same volume, so the later publish is an atomic move) and returns it; a partial extract is deleted here.
    private static async Task<string> ExtractArchiveAsync(string archivePath, string assetName, string variantDir, CancellationToken ct)
    {
        var stagingDir = $"{variantDir}.{Guid.NewGuid():N}.tmp";
        Directory.CreateDirectory(stagingDir);
        try
        {
            if (assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                await ZipFile.ExtractToDirectoryAsync(archivePath, stagingDir, ct).ConfigureAwait(false);
            }
            else if (assetName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
            {
                await ExtractTarGzAsync(archivePath, stagingDir, ct).ConfigureAwait(false);
            }
            else
            {
                throw new LlamaRuntimeException("The llama.cpp runtime archive format is not supported.");
            }

            return stagingDir;
        }
        catch
        {
            BestEffortFile.TryDeleteDirectory(stagingDir);
            throw;
        }
    }

    internal static async Task ExtractTarGzAsync(string archivePath, string destination, CancellationToken ct)
    {
        await using var fileStream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using var gzip = new GZipStream(fileStream, CompressionMode.Decompress);
        await TarFile.ExtractToDirectoryAsync(gzip, destination, overwriteFiles: true, ct).ConfigureAwait(false);
    }
}
