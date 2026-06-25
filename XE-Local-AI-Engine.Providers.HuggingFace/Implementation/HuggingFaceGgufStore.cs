namespace XE_Local_AI_Engine.Providers.HuggingFace.Implementation;

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.HuggingFace.Options;

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

    // Caches the per-file header facts (context length + detected capabilities) read from each installed GGUF header so
    // the model-list endpoint (which hits ListInstalledModelsAsync often) never re-reads the file. Keyed by
    // (LocalPath, SizeBytes, DownloadedAtUtc) so a re-download (new size/timestamp) naturally invalidates the entry. A
    // null/empty result is cached too — a model whose header carries no metadata must not be re-read on every list.
    private readonly ConcurrentDictionary<HeaderFactsCacheKey, GgufHeaderFacts> _headerFactsCache = new();

    // Caches the per-file memory-footprint header inputs (param/block/head/embedding/context counts) read from each
    // installed GGUF header so the capacity gate (which can probe per spawn) never re-reads the file. Keyed identically
    // to the header-facts cache so a re-download (new size/timestamp) naturally invalidates the entry; a null-bearing
    // result is cached too so a header that carries no estimator inputs is read at most once.
    private readonly ConcurrentDictionary<HeaderFactsCacheKey, GgufHeaderFootprintInputs> _footprintFactsCache = new();
    private readonly GgufHeaderReader _headerReader;
    private readonly ILogger<HuggingFaceGgufStore> _logger;
    private readonly HuggingFaceOptions _options;
    private readonly GgufModelRegistry _registry;

    public HuggingFaceGgufStore(HfDownloadClient downloadClient,
        IHuggingFaceGgufDiscovery discovery,
        GgufModelRegistry registry,
        GgufHeaderReader headerReader,
        HuggingFaceOptions options,
        ILogger<HuggingFaceGgufStore> logger)
    {
        ArgumentNullException.ThrowIfNull(downloadClient);
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(headerReader);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _downloadClient = downloadClient;
        _discovery = discovery;
        _registry = registry;
        _headerReader = headerReader;
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
    public async Task<GgufModelFootprintFacts?> ResolveModelFootprintFactsAsync(string modelName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        var entry = await _registry.FindAsync(modelName, ct).ConfigureAwait(false);
        if (entry is null || !File.Exists(entry.LocalPath))
        {
            return null;
        }

        // Quant + file size come from the registry entry (authoritative); the weight/KV inputs come from one tolerant
        // header read. A header failure yields all-null inputs, so the consumer degrades to the file-size weights term.
        var inputs = await ResolveFootprintInputsAsync(entry, ct).ConfigureAwait(false);
        return new GgufModelFootprintFacts(entry.Quant,
            entry.SizeBytes,
            inputs.ParamCount,
            inputs.BlockCount,
            inputs.AttentionHeadCount,
            inputs.AttentionHeadCountKV,
            inputs.EmbeddingLength,
            inputs.ContextLength);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LocalModelDescriptor>> ListInstalledModelsAsync(CancellationToken ct)
    {
        var entries = await _registry.ListAsync(ct).ConfigureAwait(false);

        var descriptors = new List<LocalModelDescriptor>(entries.Count);
        foreach (var entry in entries)
        {
            descriptors.Add(await ToDescriptorAsync(entry, ct).ConfigureAwait(false));
        }

        return descriptors;
    }

    /// <inheritdoc />
    public async Task<string> ResolveModelNameAsync(GgufModelRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.RepoId))
        {
            throw new ArgumentException("Model request must include a repo id.", nameof(request));
        }

        // Same resolution EnsureModelAsync runs, so the returned identity matches what a download will register under.
        var (_, quant, _, _, _) = await ResolveTargetAsync(request, ct).ConfigureAwait(false);
        return GgufModelName.Format(request.RepoId, quant);
    }

    /// <inheritdoc />
    public async Task<GgufModelHandle> EnsureModelAsync(GgufModelRequest request, IProgress<PullProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.RepoId))
        {
            throw new ArgumentException("Model request must include a repo id.", nameof(request));
        }

        // Resolve the target file + quant BEFORE keying the gate so the ModelName (repo:quant) is stable.
        var (fileName, quant, fileSizeBytes, fileSha, revision) = await ResolveTargetAsync(request, ct).ConfigureAwait(false);
        var modelName = GgufModelName.Format(request.RepoId, quant);

        var gate = _ensureGates.GetOrAdd(modelName, _ => new SemaphoreSlim(initialCount: 1, maxCount: 1));
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

    private async Task<LocalModelDescriptor> ToDescriptorAsync(GgufModelRegistryEntry entry, CancellationToken ct)
    {
        var facts = await ResolveHeaderFactsAsync(entry, ct).ConfigureAwait(false);

        return new LocalModelDescriptor
        {
            ModelName = entry.ModelName,
            ProviderName = ProviderName,
            IsAvailable = File.Exists(entry.LocalPath),
            SizeBytes = entry.SizeBytes,
            ModifiedAt = entry.DownloadedAtUtc,
            MaxContextTokens = facts.MaxContextTokens,
            IsToolCapable = facts.IsToolCapable,
            IsReasoningCapable = facts.IsReasoningCapable,
            Capabilities = facts.Capabilities
        };
    }

    // Reads (and caches) the GGUF header facts (context_length + chat-template-derived capabilities) for one installed
    // model in a SINGLE header read. A read failure for one model must never fail the whole list — any error yields the
    // empty facts (unknown context window, no extra capabilities).
    private async Task<GgufHeaderFacts> ResolveHeaderFactsAsync(GgufModelRegistryEntry entry, CancellationToken ct)
    {
        if (!File.Exists(entry.LocalPath))
        {
            return GgufHeaderFacts.Empty;
        }

        var key = new HeaderFactsCacheKey(entry.LocalPath, entry.SizeBytes, entry.DownloadedAtUtc);
        if (_headerFactsCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        GgufHeaderFacts resolved;
        try
        {
            var metadata = await _headerReader.ReadHeaderFromFileAsync(entry.LocalPath, ct).ConfigureAwait(false);
            var capabilities = GgufCapabilityDetector.Detect(metadata.ChatTemplate);
            resolved = new GgufHeaderFacts(ClampContextLength(metadata.ContextLength),
                capabilities.IsToolCapable,
                capabilities.IsReasoningCapable,
                capabilities.Capabilities);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Defense in depth — the reader is already tolerant, but never let one model's header sink the list.
            _logger.LogDebug(ex, "Failed to read GGUF header for an installed model; reporting unknown context window and no extra capabilities.");
            resolved = GgufHeaderFacts.Empty;
        }

        _headerFactsCache[key] = resolved;
        return resolved;
    }

    // Reads (and caches) the GGUF header weight/KV inputs for one installed model in a SINGLE header read. A read
    // failure yields the empty inputs (all-null) so the footprint consumer falls back to the on-disk file size for the
    // weights term — a model is never reported "unknown" purely for a header that could not be parsed when its size is
    // known. Cancellation propagates; every other failure degrades.
    private async Task<GgufHeaderFootprintInputs> ResolveFootprintInputsAsync(GgufModelRegistryEntry entry, CancellationToken ct)
    {
        var key = new HeaderFactsCacheKey(entry.LocalPath, entry.SizeBytes, entry.DownloadedAtUtc);
        if (_footprintFactsCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        GgufHeaderFootprintInputs resolved;
        try
        {
            var metadata = await _headerReader.ReadHeaderFromFileAsync(entry.LocalPath, ct).ConfigureAwait(false);
            resolved = new GgufHeaderFootprintInputs(metadata.ParamCount,
                metadata.BlockCount,
                metadata.AttentionHeadCount,
                metadata.AttentionHeadCountKV,
                metadata.EmbeddingLength,
                metadata.ContextLength);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to read GGUF header for an installed model's footprint inputs; degrading to file-size weights.");
            resolved = GgufHeaderFootprintInputs.Empty;
        }

        _footprintFactsCache[key] = resolved;
        return resolved;
    }

    // GGUF context_length is a non-negative long; the descriptor exposes int?. Drop non-positive or out-of-range values.
    private static int? ClampContextLength(long? contextLength)
    {
        return contextLength is > 0 and <= int.MaxValue ? (int)contextLength.Value : null;
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

    // Identity for a downloaded GGUF file: a re-download changes the size and/or download timestamp, so a stale header
    // result (e.g. for a replaced quant at the same path) can never be served from the cache.
    private readonly record struct HeaderFactsCacheKey(string LocalPath, long SizeBytes, DateTimeOffset DownloadedAtUtc);

    // The per-file header facts surfaced onto the descriptor, derived from one tolerant header read.
    private readonly record struct GgufHeaderFacts(
        int? MaxContextTokens,
        bool IsToolCapable,
        bool IsReasoningCapable,
        IReadOnlyList<string> Capabilities)
    {
        public static GgufHeaderFacts Empty { get; } = new(MaxContextTokens: null, IsToolCapable: false, IsReasoningCapable: false, []);
    }

    // The per-file GGUF header inputs the memory-fit estimator consumes (weights param count + KV-cache dimensions),
    // derived from one tolerant header read. All-null when the header could not be parsed → file-size weights fallback.
    private sealed record GgufHeaderFootprintInputs(
        long? ParamCount,
        long? BlockCount,
        long? AttentionHeadCount,
        long? AttentionHeadCountKV,
        long? EmbeddingLength,
        long? ContextLength)
    {
        public static GgufHeaderFootprintInputs Empty { get; } = new(ParamCount: null, BlockCount: null, AttentionHeadCount: null, AttentionHeadCountKV: null,
            EmbeddingLength: null, ContextLength: null);
    }
}
