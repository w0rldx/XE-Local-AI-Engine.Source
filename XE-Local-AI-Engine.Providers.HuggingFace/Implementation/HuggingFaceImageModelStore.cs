namespace XE_Local_AI_Engine.Providers.HuggingFace.Implementation;

using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.Abstractions.Image;
using XE_Local_AI_Engine.Providers.HuggingFace.Options;

/// <summary>
///     <see cref="IImageModelStore" /> over the reused <see cref="HfDownloadClient" /> + <see cref="ImageModelRegistry" />:
///     ensures every part of a diffusion-model file-set is present (download-if-missing, resume, retry, cancel, offline
///     reuse), resolves a model name to its local part paths, lists installed models, and deletes. Mirrors
///     <see cref="HuggingFaceGgufStore" /> but every operation is over a file-<b>set</b>. Serializes concurrent
///     <see cref="EnsureModelAsync" /> for the same model name with a per-name gate.
/// </summary>
internal sealed class HuggingFaceImageModelStore : IImageModelStore
{
    // The image runtime provider name — the agreed constant for the host-process sd-server runtime. Kept as a local
    // constant so this store never takes a compile dependency on the StableDiffusionCpp provider (layer isolation).
    private const string ProviderName = "stable-diffusion.cpp";

    // The default HF revision to pull a part at when the request does not pin one. HfDownloadClient resolves the actual
    // commit and returns it; the registry records the resolved revision.
    private const string DefaultRevision = "main";

    private readonly HfDownloadClient _downloadClient;

    // Per-ModelName gate so two concurrent EnsureModelAsync calls for the same file-set do not both download. Bounded by
    // the node's image-model catalog; entries are intentionally never pruned (the SemaphoreSlims hold no unmanaged
    // handles and the bound is small). Mirrors HuggingFaceGgufStore's per-model gate.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _ensureGates = new(StringComparer.Ordinal);
    private readonly ILogger<HuggingFaceImageModelStore> _logger;
    private readonly ImageModelStoreOptions _options;
    private readonly ImageModelRegistry _registry;

    public HuggingFaceImageModelStore(HfDownloadClient downloadClient,
        ImageModelRegistry registry,
        ImageModelStoreOptions options,
        ILogger<HuggingFaceImageModelStore> logger)
    {
        ArgumentNullException.ThrowIfNull(downloadClient);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _downloadClient = downloadClient;
        _registry = registry;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ImageModelPart>?> ResolveModelPartsAsync(string modelName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        var entry = await _registry.FindAsync(modelName, ct).ConfigureAwait(false);
        if (entry is null || !entry.Parts.All(part => File.Exists(part.LocalPath)))
        {
            return null;
        }

        return entry.Parts;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LocalModelDescriptor>> ListInstalledModelsAsync(CancellationToken ct)
    {
        var entries = await _registry.ListAsync(ct).ConfigureAwait(false);
        return entries.Select(ToDescriptor).ToList();
    }

    /// <inheritdoc />
    public async Task<ImageModelHandle> EnsureModelAsync(ImageModelRequest request, IProgress<PullProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.RepoId))
        {
            throw new ArgumentException("Image model request must include a repo id.", nameof(request));
        }

        if (request.Parts.Count == 0)
        {
            throw new ArgumentException("Image model request must include at least one weight part.", nameof(request));
        }

        var gate = _ensureGates.GetOrAdd(request.ModelName, static _ => new SemaphoreSlim(initialCount: 1, maxCount: 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Offline reuse — a verified file-set already present needs no download.
            var existing = await _registry.FindAsync(request.ModelName, ct).ConfigureAwait(false);
            if (existing is not null && existing.Parts.All(part => File.Exists(part.LocalPath)))
            {
                progress?.Report(new PullProgress
                {
                    ModelName = request.ModelName,
                    Status = "completed",
                    TotalBytes = existing.SizeBytes,
                    CompletedBytes = existing.SizeBytes
                });
                return ToHandle(existing);
            }

            var revision = string.IsNullOrWhiteSpace(request.Revision) ? DefaultRevision : request.Revision;
            var modelDirectory = SafeModelDirectorySegment(request.ModelName);

            var parts = new List<ImageModelPart>(request.Parts.Count);
            var resolvedRevision = revision;
            long totalBytes = 0;

            var partCount = request.Parts.Count;
            // A set total is only honest when EVERY part declares a size: summing the known ones would report a total
            // the transfer will overshoot, and a progress bar that passes 100% is worse than one that admits it cannot
            // compute a percentage.
            long? knownSetTotal = request.Parts.All(part => part.SizeBytes is > 0)
                ? request.Parts.Sum(part => part.SizeBytes!.Value)
                : null;
            var partIndex = 0;

            try
            {
                foreach (var partRequest in request.Parts)
                {
                    partIndex++;
                    EnsureSafeFileName(partRequest.FileName);

                    // Hard containment guard: every part lands under {ModelsDirectory}/{safe-model-dir}/, never outside it.
                    var relativePath = $"{modelDirectory}/{partRequest.FileName}";
                    var destinationPath = GgufFilePath.ResolveContainedPath(_options.ModelsDirectory, relativePath);

                    // Reuse a part this model already has. The registry entry is only written once the WHOLE set
                    // succeeds, so without this a set that failed on its last part re-downloads every earlier part from
                    // scratch on the next attempt — tens of gigabytes of pointless transfer for a multi-part model.
                    if (TryReuseCompletedPart(destinationPath, partRequest, out var reusedSize))
                    {
                        totalBytes += reusedSize;
                        parts.Add(new ImageModelPart
                        {
                            Role = partRequest.Role,
                            FileName = partRequest.FileName,
                            LocalPath = destinationPath,
                            SizeBytes = reusedSize,
                            Sha256 = partRequest.Sha256
                        });
                        continue;
                    }

                    // Report set-relative bytes so a multi-part download shows one advancing bar instead of a bar that
                    // fills and snaps back to zero once per part.
                    var partProgress = progress is null
                        ? null
                        // totalBytes is the running sum of every part already finished, i.e. exactly the set-relative
                        // offset this part's byte counts must be added to.
                        : new SetProgressAdapter(progress, request.ModelName, totalBytes, knownSetTotal, partIndex, partCount);

                    var result = await _downloadClient.DownloadAsync(request.RepoId,
                        partRequest.FileName,
                        revision,
                        request.ModelName,
                        destinationPath,
                        // A real size makes the pre-flight disk check actually run (it early-returns on 0). Checking per
                        // part is enough for the whole set: each check reads CURRENT free space, and earlier parts have
                        // already been written by the time a later one is checked.
                        partRequest.SizeBytes ?? 0,
                        partRequest.Sha256,
                        partProgress,
                        ct).ConfigureAwait(false);

                    if (!string.IsNullOrEmpty(result.ResolvedRevision))
                    {
                        resolvedRevision = result.ResolvedRevision;
                    }

                    totalBytes += result.SizeBytes;
                    parts.Add(new ImageModelPart
                    {
                        Role = partRequest.Role,
                        FileName = partRequest.FileName,
                        LocalPath = result.LocalPath,
                        SizeBytes = result.SizeBytes,
                        // Only the verified hash — the discovery digest we passed was used for verification, never echoed.
                        Sha256 = result.Sha256
                    });
                }
            }
            catch
            {
                // A download that never wrote a byte (a mistyped weight file 404s) still left the model's directory
                // behind, so a failed attempt accumulated an orphan empty folder under models/images/. Remove it — but
                // ONLY when empty, so a partially-transferred .part file survives for the next attempt to resume from.
                TryDeleteEmptyDirectory(modelDirectory);
                throw;
            }

            var entry = new ImageModelRegistryEntry
            {
                ModelName = request.ModelName,
                RepoId = request.RepoId,
                Family = request.Family,
                Kind = request.Kind,
                Parts = parts,
                SizeBytes = totalBytes,
                SourceRevision = resolvedRevision,
                DownloadedAtUtc = DateTimeOffset.UtcNow
            };

            await _registry.UpsertAsync(entry, ct).ConfigureAwait(false);
            return ToHandle(entry);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task DeleteModelAsync(string modelName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        var entry = await _registry.FindAsync(modelName, ct).ConfigureAwait(false);
        if (entry is not null)
        {
            var paths = entry.Parts.SelectMany(part => new[]
            {
                part.LocalPath,
                part.LocalPath + ".part"
            });
            foreach (var path in paths)
            {
                TryDeleteFile(path);
            }
        }

        await _registry.RemoveAsync(modelName, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> ExistsAsync(string modelName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        var entry = await _registry.FindAsync(modelName, ct).ConfigureAwait(false);
        return entry is not null && entry.Parts.All(part => File.Exists(part.LocalPath));
    }

    private static LocalModelDescriptor ToDescriptor(ImageModelRegistryEntry entry)
    {
        return new LocalModelDescriptor
        {
            ModelName = entry.ModelName,
            ProviderName = ProviderName,
            IsAvailable = entry.Parts.Count > 0 && entry.Parts.All(part => File.Exists(part.LocalPath)),
            SizeBytes = entry.SizeBytes,
            ModifiedAt = entry.DownloadedAtUtc,
            // Image models carry no text-generation context window / chat-template capabilities.
            MaxContextTokens = null
        };
    }

    private static ImageModelHandle ToHandle(ImageModelRegistryEntry entry)
    {
        return new ImageModelHandle(entry.ModelName, entry.Family, entry.Kind, entry.Parts);
    }

    // Untrusted repo input: reject any file name that could escape the models directory before we ever open a handle.
    private static void EnsureSafeFileName(string fileName)
    {
        if (!GgufFilePath.IsSafeRelativePath(fileName))
        {
            throw new ArgumentException("The image model part file name is not a safe relative path.", nameof(fileName));
        }
    }

    // Derives a file-safe, single-segment subdirectory name from the (possibly repo-qualified) model name so each
    // model's parts are isolated and two models can never collide on a shared part file name.
    private static string SafeModelDirectorySegment(string modelName)
    {
        var builder = new StringBuilder(modelName.Length);
        foreach (var ch in modelName)
        {
            builder.Append(char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or '_' ? ch : '_');
        }

        var segment = builder.ToString().Trim('.');
        return string.IsNullOrEmpty(segment) ? "model" : segment;
    }

    // Removes the model's own subdirectory when a failed download left it empty. Non-recursive by construction: an empty
    // directory is all that is deleted, so a resumable .part file (or any already-downloaded part of a multi-part set)
    // keeps the directory alive. Best-effort — cleanup must never mask the download failure that triggered it.
    private void TryDeleteEmptyDirectory(string modelDirectorySegment)
    {
        try
        {
            var directory = GgufFilePath.ResolveContainedPath(_options.ModelsDirectory, modelDirectorySegment);
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _logger.LogDebug(exception, "Could not remove the empty image-model directory after a failed download.");
        }
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
            // Best-effort delete; the registry entry is removed regardless so the model is no longer offered.
        }
    }

    /// <summary>
    ///     Decides whether a part already on disk can be kept instead of re-downloaded, returning its size when it can.
    /// </summary>
    /// <remarks>
    ///     Reuse requires a <b>declared</b> size that matches the file exactly. Without a declared size there is nothing
    ///     to check the file against — a truncated leftover would be indistinguishable from a complete one — so the part
    ///     is re-downloaded, which is the previous behaviour. Length is deliberately the only check: hashing a 13 GB
    ///     diffusion weight to save re-downloading it would cost a large fraction of the transfer it avoids, and the
    ///     download path still verifies the sha of anything it actually fetches.
    /// </remarks>
    private static bool TryReuseCompletedPart(string destinationPath, ImageModelPartRequest partRequest, out long sizeBytes)
    {
        sizeBytes = 0;
        if (partRequest.SizeBytes is not > 0)
        {
            return false;
        }

        try
        {
            var info = new FileInfo(destinationPath);
            if (!info.Exists || info.Length != partRequest.SizeBytes.Value)
            {
                return false;
            }

            sizeBytes = info.Length;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Unreadable for any reason: fall through to a normal download rather than guessing.
            return false;
        }
    }

    /// <summary>
    ///     Re-frames one part's byte counts as progress through the whole file-set.
    /// </summary>
    /// <remarks>
    ///     The underlying download client reports bytes for the single file it is transferring. Forwarding that
    ///     unchanged makes a three-part model fill its bar and snap back to zero twice, which reads as a failure and a
    ///     restart. Offsetting by the bytes already finished — and reporting the set total when every part declared a
    ///     size — turns it into one monotonic bar. <see cref="PullProgress.PartIndex" />/<see cref="PullProgress.PartCount" />
    ///     let the UI name the file being fetched.
    /// </remarks>
    private sealed class SetProgressAdapter : IProgress<PullProgress>
    {
        private readonly long _completedInPriorParts;
        private readonly IProgress<PullProgress> _inner;
        private readonly string _modelName;
        private readonly int _partCount;
        private readonly int _partIndex;
        private readonly long? _setTotalBytes;

        public SetProgressAdapter(IProgress<PullProgress> inner,
            string modelName,
            long completedInPriorParts,
            long? setTotalBytes,
            int partIndex,
            int partCount)
        {
            _inner = inner;
            _modelName = modelName;
            _completedInPriorParts = completedInPriorParts;
            _setTotalBytes = setTotalBytes;
            _partIndex = partIndex;
            _partCount = partCount;
        }

        public void Report(PullProgress value)
        {
            _inner.Report(new PullProgress
            {
                ModelName = _modelName,
                Status = value.Status,
                // Fall back to this part's own total only when the set total is unknown, so the bar is either
                // set-relative throughout or part-relative throughout — never a silent mix of the two.
                TotalBytes = _setTotalBytes ?? value.TotalBytes,
                CompletedBytes = _completedInPriorParts + (value.CompletedBytes ?? 0),
                PartIndex = _partIndex,
                PartCount = _partCount
            });
        }
    }
}
