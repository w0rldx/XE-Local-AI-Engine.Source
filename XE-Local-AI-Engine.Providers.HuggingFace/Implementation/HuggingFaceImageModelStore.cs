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

            foreach (var partRequest in request.Parts)
            {
                EnsureSafeFileName(partRequest.FileName);

                // Hard containment guard: every part lands under {ModelsDirectory}/{safe-model-dir}/, never outside it.
                var relativePath = $"{modelDirectory}/{partRequest.FileName}";
                var destinationPath = GgufFilePath.ResolveContainedPath(_options.ModelsDirectory, relativePath);

                var result = await _downloadClient.DownloadAsync(request.RepoId,
                    partRequest.FileName,
                    revision,
                    request.ModelName,
                    destinationPath,
                    expectedSizeBytes: 0,
                    progress,
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
                    Sha256 = result.Sha256 ?? partRequest.Sha256
                });
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
            var paths = entry.Parts.SelectMany(part => new[] { part.LocalPath, part.LocalPath + ".part" });
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
}
