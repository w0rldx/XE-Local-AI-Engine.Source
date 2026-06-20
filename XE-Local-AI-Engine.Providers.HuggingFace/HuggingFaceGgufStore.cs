namespace XE_Local_AI_Engine.Providers.HuggingFace;

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;

/// <summary>
///     <see cref="IGgufModelStore" /> over <see cref="HfDownloadClient" /> + <see cref="IHuggingFaceGgufDiscovery" /> +
///     <see cref="GgufModelRegistry" />: ensures a selected GGUF is present (download-if-missing, resume, retry, cancel,
///     offline reuse), resolves model name → file path, lists installed models, and deletes. Serializes concurrent
///     <see cref="EnsureModelAsync" /> for the same model name with a per-name gate.
/// </summary>
internal sealed class HuggingFaceGgufStore : IGgufModelStore
{
    // The Hugging Face provider must not depend on the LlamaServer project; the descriptor provider name is the agreed
    // constant for the host-process llama-server runtime (LlamaServerProviderConstants.ProviderName).
    private const string ProviderName = "llamacpp";
    private readonly IHuggingFaceGgufDiscovery _discovery;

    private readonly HfDownloadClient _downloadClient;

    // Per-ModelName gate so two concurrent EnsureModelAsync calls for the same file do not both download. The set is
    // bounded by the node's model catalog (one entry per distinct model ever ensured for the process lifetime), so
    // entries are intentionally never pruned or disposed — the SemaphoreSlims hold no unmanaged handles and the bound
    // is small. Mirrors OllamaLocalModelProvider's per-pull gate.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _ensureGates = new(StringComparer.Ordinal);
    private readonly ILogger<HuggingFaceGgufStore> _logger;
    private readonly HuggingFaceOptions _options;
    private readonly GgufModelRegistry _registry;

    public HuggingFaceGgufStore(HfDownloadClient downloadClient,
        IHuggingFaceGgufDiscovery discovery,
        GgufModelRegistry registry,
        HuggingFaceOptions options,
        ILogger<HuggingFaceGgufStore> logger)
    {
        ArgumentNullException.ThrowIfNull(downloadClient);
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _downloadClient = downloadClient;
        _discovery = discovery;
        _registry = registry;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<string?> ResolveModelFilePathAsync(string modelName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        var entry = await _registry.FindAsync(modelName, ct).ConfigureAwait(false);
        if (entry is null || !File.Exists(entry.LocalPath))
        {
            return null;
        }

        return entry.LocalPath;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LocalModelDescriptor>> ListInstalledModelsAsync(CancellationToken ct)
    {
        var entries = await _registry.ListAsync(ct).ConfigureAwait(false);
        return entries.Select(ToDescriptor).ToList();
    }

    /// <inheritdoc />
    public async Task<string> ResolveModelNameAsync(GgufModelRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RepoId);

        // Same resolution EnsureModelAsync runs, so the returned identity matches what a download will register under.
        var (_, quant, _, _, _) = await ResolveTargetAsync(request, ct).ConfigureAwait(false);
        return GgufModelName.Format(request.RepoId, quant);
    }

    /// <inheritdoc />
    public async Task<GgufModelHandle> EnsureModelAsync(GgufModelRequest request, IProgress<PullProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RepoId);

        // Resolve the target file + quant BEFORE keying the gate so the ModelName (repo:quant) is stable.
        var (fileName, quant, fileSizeBytes, fileSha, revision) = await ResolveTargetAsync(request, ct).ConfigureAwait(false);
        var modelName = GgufModelName.Format(request.RepoId, quant);

        var gate = _ensureGates.GetOrAdd(modelName, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Offline reuse — a verified file already present needs no download.
            var existing = await _registry.FindAsync(modelName, ct).ConfigureAwait(false);
            if (existing is not null && File.Exists(existing.LocalPath))
            {
                progress?.Report(new PullProgress
                {
                    ModelName = modelName,
                    Status = "completed",
                    TotalBytes = existing.SizeBytes,
                    CompletedBytes = existing.SizeBytes
                });
                return ToHandle(existing);
            }

            // Hard containment guard (defense in depth — discovery already filters): never write outside ModelsDirectory.
            var destinationPath = GgufFilePath.ResolveContainedPath(_options.ModelsDirectory, fileName);
            var result = await _downloadClient.DownloadAsync(request.RepoId,
                fileName,
                revision,
                modelName,
                destinationPath,
                fileSizeBytes,
                progress,
                ct).ConfigureAwait(false);

            var entry = new GgufModelRegistryEntry
            {
                ModelName = modelName,
                RepoId = request.RepoId,
                FileName = fileName,
                Quant = quant,
                LocalPath = result.LocalPath,
                SizeBytes = result.SizeBytes,
                // Prefer the verified download hash; fall back to the inspected file hash when the OID was not on the resolve response.
                Sha256 = result.Sha256 ?? fileSha,
                SourceRevision = string.IsNullOrEmpty(result.ResolvedRevision) ? revision : result.ResolvedRevision,
                DownloadedAtUtc = DateTimeOffset.UtcNow,
                Role = request.Role
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
            TryDeleteFile(entry.LocalPath);
            TryDeleteFile(entry.LocalPath + ".part");
        }

        await _registry.RemoveAsync(modelName, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> ExistsAsync(string modelName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        var entry = await _registry.FindAsync(modelName, ct).ConfigureAwait(false);
        return entry is not null && File.Exists(entry.LocalPath);
    }

    // Resolves the concrete .gguf file for the request: an explicit FileName is used verbatim; otherwise the repo is
    // inspected and the file whose quant matches (request.Quant ?? DefaultQuant) is selected.
    private async Task<(string FileName, string Quant, long SizeBytes, string? Sha256, string Revision)> ResolveTargetAsync(GgufModelRequest request,
        CancellationToken ct)
    {
        // The header-free listing suffices — resolution only needs file name / quant / size / sha / revision, never the
        // per-file GGUF header metadata, so this avoids N range reads before a download.
        var detail = await _discovery.ListRepoFilesAsync(request.RepoId, ct).ConfigureAwait(false);

        if (request.FileName is not null)
        {
            var byName = detail.Files.FirstOrDefault(file =>
                string.Equals(file.FileName, request.FileName, StringComparison.OrdinalIgnoreCase));
            if (byName is null)
            {
                throw new HuggingFaceDownloadException(HuggingFaceDownloadFailure.NotFound,
                    "The requested model file was not found in the repository.");
            }

            EnsureSafeFileName(byName.FileName);
            return (byName.FileName, byName.Quant, byName.SizeBytes, byName.Sha256, request.Revision ?? byName.Revision);
        }

        var targetQuant = request.Quant ?? _options.DefaultQuant;
        var byQuant = detail.Files.FirstOrDefault(file =>
            string.Equals(file.Quant, targetQuant, StringComparison.OrdinalIgnoreCase));

        // A bare base quant (e.g. the default Q4_K_M, or an explicit Q4_K_XL) also resolves to an Unsloth Dynamic
        // file (UD-Q4_K_M) when no exact match exists, so default/base requests still succeed against UD-only repos.
        // An explicit UD- request stays exact — it must not silently fall through to a plain quant.
        if (byQuant is null && !GgufQuantParser.IsDynamic(targetQuant))
        {
            byQuant = detail.Files.FirstOrDefault(file =>
                string.Equals(GgufQuantParser.StripDynamicPrefix(file.Quant), targetQuant, StringComparison.OrdinalIgnoreCase));
        }

        if (byQuant is null)
        {
            throw new HuggingFaceDownloadException(HuggingFaceDownloadFailure.NotFound,
                "No GGUF file with the requested quantization was found in the repository.");
        }

        EnsureSafeFileName(byQuant.FileName);
        return (byQuant.FileName, byQuant.Quant, byQuant.SizeBytes, byQuant.Sha256, request.Revision ?? byQuant.Revision);
    }

    private static LocalModelDescriptor ToDescriptor(GgufModelRegistryEntry entry)
    {
        return new LocalModelDescriptor
        {
            ModelName = entry.ModelName,
            ProviderName = ProviderName,
            IsAvailable = File.Exists(entry.LocalPath),
            SizeBytes = entry.SizeBytes,
            ModifiedAt = entry.DownloadedAtUtc,
            MaxContextTokens = null
        };
    }

    private static GgufModelHandle ToHandle(GgufModelRegistryEntry entry)
    {
        return new GgufModelHandle(entry.ModelName,
            entry.LocalPath,
            entry.Quant,
            entry.SizeBytes,
            entry.Sha256,
            entry.SourceRevision,
            entry.Role);
    }

    // Untrusted repo input: reject any file name that could escape the models directory before we ever open a handle.
    private static void EnsureSafeFileName(string fileName)
    {
        if (!GgufFilePath.IsSafeRelativePath(fileName))
        {
            throw new HuggingFaceDownloadException(HuggingFaceDownloadFailure.NotFound,
                "The repository returned an unsafe model file path.");
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
}
