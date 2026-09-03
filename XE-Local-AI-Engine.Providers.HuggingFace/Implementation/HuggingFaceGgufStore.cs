namespace XE_Local_AI_Engine.Providers.HuggingFace.Implementation;

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
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

    // Ollama-style capability token surfaced for a model with a local mmproj projector — mirrors the tokens the GGUF
    // capability detector emits (completion/tools/thinking) so a vision model classifies consistently across the system.
    private const string VisionCapability = "vision";
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
    public async Task<string?> ResolveProjectorFilePathAsync(string modelName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        var entry = await _registry.FindAsync(modelName, ct).ConfigureAwait(false);
        if (entry?.ProjectorLocalPath is null || !File.Exists(entry.ProjectorLocalPath))
        {
            return null;
        }

        return entry.ProjectorLocalPath;
    }

    /// <inheritdoc />
    public async Task<GgufAdapterLaunch?> ResolveAdapterLaunchAsync(string modelName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        var entry = await _registry.FindAsync(modelName, ct).ConfigureAwait(false);
        if (entry?.AdapterFileName is null || !File.Exists(entry.LocalPath))
        {
            return null;
        }

        if (entry.BaseModelName is not { Length: > 0 } baseModelName)
        {
            throw new GgufAdapterBaseModelMissingException("The installed adapter does not record the base model it applies to.");
        }

        var baseEntry = await _registry.FindAsync(baseModelName, ct).ConfigureAwait(false);
        if (baseEntry is null || !File.Exists(baseEntry.LocalPath))
        {
            throw new GgufAdapterBaseModelMissingException("The base model this adapter applies to is not installed. Reinstall the base model or delete the adapter.");
        }

        return new GgufAdapterLaunch(baseEntry.LocalPath, entry.LocalPath, entry.AdapterSizeBytes ?? entry.SizeBytes);
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
            inputs.ContextLength,
            inputs.AttentionKeyLength,
            inputs.AttentionValueLength,
            inputs.SlidingWindow,
            inputs.SlidingWindowPattern,
            ContentIdentity: entry.Sha256 ?? $"{entry.SourceRevision}:{entry.FileName}:{entry.SizeBytes}",
            inputs.Architecture,
            inputs.ExpertCount,
            inputs.ExpertUsedCount,
            inputs.AttentionKeyLengthMla,
            inputs.AttentionValueLengthMla);
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
                // Projector backfill is intentionally fail-closed here. Retrofitting a projector changes the complete
                // member set, aggregate fingerprint, universal sidecar, and registry revision and therefore belongs to
                // the coordinated acquisition transaction rather than this legacy offline-reuse fast path.

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
                fileSha,
                progress,
                ct).ConfigureAwait(false);

            // A vision repo ships an mmproj projector companion the model needs for image input; pull it alongside the
            // weights (auto-pair). A text-only repo has none — FindProjectorAsync returns null and nothing extra is
            // fetched. A projector failure never fails the model: it loads text-only. Skipped for a draft file — a
            // speculative drafter is never the chat model that would consume a projector.
            var projector = GgufDraftModel.IsDraftQuant(quant)
                ? ProjectorDownloadResult.None
                : await TryEnsureProjectorAsync(request.RepoId, fileName, revision, ct).ConfigureAwait(false);

            var weightHash = await GgufAcquisitionSidecar.ComputeSha256Async(result.LocalPath, ct).ConfigureAwait(false);
            var weightFingerprint = GgufMemberFingerprint.Compute(weightHash, result.SizeBytes);
            var weightRelativePath = GgufFilePath.GetRelativeContainedPath(_options.ModelsDirectory, result.LocalPath);
            var contentMembers = new List<GgufModelContentMember>
            {
                new(weightRelativePath, InstalledModelPhysicalMemberRole.Weight, result.SizeBytes, weightHash, [modelName])
            };
            if (projector.LocalPath is not null)
            {
                contentMembers.Add(new GgufModelContentMember(projector.RelativePath!,
                    InstalledModelPhysicalMemberRole.Projector,
                    projector.SizeBytes!.Value,
                    projector.ContentSha256!,
                    [modelName]));
            }

            var modelContentFingerprint = GgufModelContentFingerprint.ComputeV1(contentMembers);
            var acquiredAt = DateTimeOffset.UtcNow;

            var role = request.Role == GgufRole.Unknown ? GgufRole.Chat : request.Role;
            if (GgufDraftModel.IsDraftQuant(quant))
            {
                role = GgufRole.Draft;
            }

            var sourceDisplayName = Path.GetFileName(fileName);
            var entry = new GgufModelRegistryEntry
            {
                ModelName = modelName,
                RepoId = request.RepoId,
                FileName = Path.GetFileName(result.LocalPath),
                Quant = quant,
                LocalPath = result.LocalPath,
                SizeBytes = result.SizeBytes,
                // The download verified the content against the resolve OID or, failing that, the discovery digest we
                // just passed. Persist ONLY that verified hash — never echo an unverified digest, which would be
                // indistinguishable from a real integrity guarantee.
                Sha256 = weightHash,
                SourceRevision = string.IsNullOrEmpty(result.ResolvedRevision) ? revision : result.ResolvedRevision,
                DownloadedAtUtc = acquiredAt,
                // A speculative-decoding drafter is a draft whatever the caller hinted — the picker offers the whole
                // repo through one download action, so a drafter arrives on the same Chat/Unknown-role request as the
                // base weights. The resolved quant carries the marker discovery stamped on it, so it is authoritative.
                Role = role,
                ProjectorFileName = projector.SourceDisplayName,
                ProjectorLocalPath = projector.LocalPath,
                ProjectorSizeBytes = projector.SizeBytes,
                ProjectorSha256 = projector.ContentSha256,
                Origin = LocalModelOrigin.HuggingFace,
                SourceDisplayName = sourceDisplayName,
                MetadataSchemaVersion = GgufAcquisitionMetadata.CurrentSchemaVersion,
                ModelContentFingerprint = modelContentFingerprint
            };

            var registryRevision = GgufRegistryRevision.ComputeV1(entry, _options.ModelsDirectory);
            entry = entry with
            {
                RegistryRevision = registryRevision
            };
            var sidecar = new GgufAcquisitionMetadata
            {
                SchemaVersion = GgufAcquisitionMetadata.CurrentSchemaVersion,
                RegistryRevision = registryRevision,
                ModelName = modelName,
                Origin = LocalModelOrigin.HuggingFace,
                LocalFileName = entry.FileName,
                Quantization = entry.Quant,
                WeightContentSha256 = weightHash,
                WeightSizeBytes = entry.SizeBytes,
                WeightMemberFingerprint = weightFingerprint,
                SourceDisplayName = sourceDisplayName,
                AcquiredAtUtc = acquiredAt,
                RegistryRepoId = entry.RepoId,
                RegistrySourceRevision = entry.SourceRevision,
                Role = entry.Role,
                ProjectorRelativePath = projector.RelativePath,
                ProjectorSourceDisplayName = projector.SourceDisplayName,
                ProjectorSourceSha256 = projector.SourceSha256,
                ProjectorSourceSizeBytes = projector.SourceSizeBytes,
                ProjectorContentSha256 = projector.ContentSha256,
                ProjectorContentSizeBytes = projector.SizeBytes,
                ProjectorMemberFingerprint = projector.MemberFingerprint,
                ModelContentFingerprint = modelContentFingerprint
            };
            await GgufAcquisitionSidecar.WriteAsync(result.LocalPath + GgufAcquisitionSidecar.Suffix, sidecar, ct).ConfigureAwait(false);

            await _registry.UpsertAsync(entry, ct).ConfigureAwait(false);
            return ToHandle(entry);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public Task DeleteModelAsync(string modelName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        ct.ThrowIfCancellationRequested();
        throw new NotSupportedException("Installed GGUF deletion must use the coordinated journaled deletion service.");
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
    private async Task<ResolvedTarget> ResolveTargetAsync(GgufModelRequest request,
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
            return new ResolvedTarget(byName.FileName, byName.Quant, byName.SizeBytes, byName.Sha256, request.Revision ?? byName.Revision);
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
        return new ResolvedTarget(byQuant.FileName, byQuant.Quant, byQuant.SizeBytes, byQuant.Sha256, request.Revision ?? byQuant.Revision);
    }

    // Downloads the repo's mmproj projector companion (when it ships one) next to the model and returns its filename +
    // local path. Stored under a NON-scanned "projectors/" subdirectory keyed by the model's file stem: the projector
    // name embeds the model's quant token, so a top-level placement would be mis-registered as a phantom model by the
    // registry rescan (which parses a quant from every top-level *.gguf). A projector download must never fail the model
    // — any failure degrades to text-only (null path). Cancellation propagates.
    private const string ProjectorSubdirectory = "projectors";

    private async Task<ProjectorDownloadResult> TryEnsureProjectorAsync(string repoId,
        string modelFileName,
        string weightsRevision,
        CancellationToken ct)
    {
        try
        {
            var projector = await _discovery.FindProjectorAsync(repoId, ct).ConfigureAwait(false);
            if (projector is null || !GgufFilePath.IsSafeRelativePath(projector.FileName))
            {
                return ProjectorDownloadResult.None;
            }

            var localRelativePath = $"{ProjectorSubdirectory}/{Path.GetFileNameWithoutExtension(modelFileName)}.mmproj.gguf";
            var destinationPath = GgufFilePath.ResolveContainedPath(_options.ModelsDirectory, localRelativePath);

            // Pin the projector to the SAME commit as the weights: a pinned older model must not pair with a newer,
            // possibly incompatible projector from the repo head. Fall back to the projector's own (head) revision only
            // when the weights revision is unknown (e.g. a rescanned entry). The discovery sha is for the head file, so
            // it is dropped when the pinned revision differs — the download then verifies against that revision's own OID.
            var pinnedRevision = string.IsNullOrWhiteSpace(weightsRevision) ? projector.Revision : weightsRevision;
            var expectedSha = string.Equals(pinnedRevision, projector.Revision, StringComparison.Ordinal) ? projector.Sha256 : null;

            // No progress reporter: the pull UI keys progress by model name and the main weights already reported
            // completion; a second stream under the same name would flip the bar back to "downloading".
            var result = await _downloadClient.DownloadAsync(repoId,
                projector.FileName,
                pinnedRevision,
                $"{repoId} (vision projector)",
                destinationPath,
                projector.SizeBytes,
                expectedSha,
                progress: null,
                ct).ConfigureAwait(false);

            _logger.LogInformation("Downloaded the multimodal projector {ProjectorFile} for {RepoId}; image input is available.",
                projector.FileName, repoId);
            var contentSha = await GgufAcquisitionSidecar.ComputeSha256Async(result.LocalPath, ct).ConfigureAwait(false);
            return new ProjectorDownloadResult(projector.FileName,
                localRelativePath,
                result.LocalPath,
                result.SizeBytes,
                contentSha,
                NormalizeSha256(projector.Sha256),
                projector.SizeBytes,
                GgufMemberFingerprint.Compute(contentSha, result.SizeBytes));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to download the multimodal projector for {RepoId}; the model will load as text-only.", repoId);
            return ProjectorDownloadResult.None;
        }
    }

    [SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase",
        Justification = "Acquisition metadata requires the canonical lowercase SHA-256 representation validated by the universal sidecar.")]
    private static string? NormalizeSha256(string? sha256) =>
        sha256?.ToLowerInvariant();

    private async Task<LocalModelDescriptor> ToDescriptorAsync(GgufModelRegistryEntry entry, CancellationToken ct)
    {
        var facts = await ResolveHeaderFactsAsync(entry, ct).ConfigureAwait(false);

        // A model is multimodal (accepts image input) exactly when its mmproj projector companion is present locally —
        // the same file that gates the llama-server --mmproj argument, so the flag never over-claims. Surface the vision
        // capability token too, alongside the chat-template-derived tokens.
        var isMultimodalCapable = entry.ProjectorLocalPath is not null && File.Exists(entry.ProjectorLocalPath);
        var capabilities = isMultimodalCapable
            ? facts.Capabilities.Append(VisionCapability).ToArray()
            : facts.Capabilities;

        return new LocalModelDescriptor
        {
            ModelName = entry.ModelName,
            ProviderName = ProviderName,
            IsAvailable = File.Exists(entry.LocalPath),
            SizeBytes = entry.SizeBytes,
            ModifiedAt = entry.DownloadedAtUtc,
            RevisionFingerprint = entry.Sha256 ?? entry.SourceRevision,
            Origin = entry.Origin,
            ModelContentFingerprint = entry.ModelContentFingerprint,
            MaxContextTokens = facts.MaxContextTokens,
            IsToolCapable = facts.IsToolCapable,
            IsReasoningCapable = facts.IsReasoningCapable,
            IsNativeReasoningCapable = facts.IsNativeReasoningCapable,
            ReasoningBudgetEnforceable = facts.ReasoningBudgetEnforceable,
            IsMultimodalCapable = isMultimodalCapable,
            Capabilities = capabilities
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
                capabilities.IsNativeReasoningCapable,
                capabilities.Capabilities,
                capabilities.ReasoningBudgetEnforceable);
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
                metadata.ContextLength,
                metadata.AttentionKeyLength,
                metadata.AttentionValueLength,
                metadata.SlidingWindow,
                metadata.SlidingWindowPattern,
                metadata.Architecture,
                metadata.ExpertCount,
                metadata.ExpertUsedCount,
                metadata.AttentionKeyLengthMla,
                metadata.AttentionValueLengthMla);
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

    // Identity for a downloaded GGUF file: a re-download changes the size and/or download timestamp, so a stale header
    // result (e.g. for a replaced quant at the same path) can never be served from the cache.
    private readonly record struct HeaderFactsCacheKey(string LocalPath, long SizeBytes, DateTimeOffset DownloadedAtUtc);

    private sealed record ProjectorDownloadResult(
        string? SourceDisplayName,
        string? RelativePath,
        string? LocalPath,
        long? SizeBytes,
        string? ContentSha256,
        string? SourceSha256,
        long? SourceSizeBytes,
        string? MemberFingerprint)
    {
        public static ProjectorDownloadResult None { get; } = new(null, null, null, null, null, null, null, null);
    }

    // The per-file header facts surfaced onto the descriptor, derived from one tolerant header read.
    private readonly record struct GgufHeaderFacts(
        int? MaxContextTokens,
        bool IsToolCapable,
        bool IsReasoningCapable,
        bool IsNativeReasoningCapable,
        IReadOnlyList<string> Capabilities,
        bool ReasoningBudgetEnforceable = true)
    {
        // An unreadable header keeps the reasoning-budget flag at its inert TRUE default: the model is also reported
        // non-reasoning here, so nothing reads it, and a false would be a silent instruction to drop the cap.
        public static GgufHeaderFacts Empty { get; } = new(MaxContextTokens: null,
            IsToolCapable: false,
            IsReasoningCapable: false,
            IsNativeReasoningCapable: false,
            [],
            ReasoningBudgetEnforceable: true);
    }

    // The per-file GGUF header inputs the memory-fit estimator consumes (weights param count + KV-cache dimensions),
    // derived from one tolerant header read. All-null when the header could not be parsed → file-size weights fallback.
    private sealed record GgufHeaderFootprintInputs(
        long? ParamCount,
        long? BlockCount,
        long? AttentionHeadCount,
        long? AttentionHeadCountKV,
        long? EmbeddingLength,
        long? ContextLength,
        long? AttentionKeyLength = null,
        long? AttentionValueLength = null,
        long? SlidingWindow = null,
        long? SlidingWindowPattern = null,
        string? Architecture = null,
        long? ExpertCount = null,
        long? ExpertUsedCount = null,
        long? AttentionKeyLengthMla = null,
        long? AttentionValueLengthMla = null)
    {
        public static GgufHeaderFootprintInputs Empty { get; } = new(ParamCount: null, BlockCount: null, AttentionHeadCount: null, AttentionHeadCountKV: null,
            EmbeddingLength: null, ContextLength: null, AttentionKeyLength: null, AttentionValueLength: null, SlidingWindow: null, SlidingWindowPattern: null,
            Architecture: null, ExpertCount: null, ExpertUsedCount: null, AttentionKeyLengthMla: null, AttentionValueLengthMla: null);
    }

    /// <summary>The concrete repo file a model request resolves to, with the revision the download will pin.</summary>
    private sealed record ResolvedTarget(string FileName, string Quant, long SizeBytes, string? Sha256, string Revision);
}
