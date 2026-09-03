namespace XE_Local_AI_Engine.Client.Services.Inference;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;

/// <summary>
///     Captures the versioned identity of every launch-affecting fact that makes a persisted replay comparable. The
///     canonical document deliberately represents llama.cpp-owned batch defaults as an explicit mode rather than
///     treating an absent numeric value as equivalent to any particular default.
/// </summary>
public interface ILaunchPolicyFingerprintProvider
{
    Task<LaunchPolicyFingerprint> CaptureAsync(InferenceProfileFingerprintInput input, CancellationToken ct);

    Task<LaunchPolicyFingerprint> CaptureAsync(InferenceProfileRecord profile, string modelFilePath, CancellationToken ct);

    Task<bool> MatchesAsync(InferenceProfileRecord profile, string modelFilePath, CancellationToken ct);
}

public sealed class LaunchPolicyFingerprintProvider(
    IInstalledRuntimeStore installedRuntimeStore,
    ILlamaCppBinaryManager binaryManager,
    IGgufModelStore modelStore,
    IGgufModelRegistry modelRegistry,
    LlamaServerSupervisorOptions supervisorOptions,
    LlamaServerLaunchPolicyOptions launchPolicyOptions,
    LaunchPolicyFileHashCache fileHashCache) : ILaunchPolicyFingerprintProvider
{
    // 5: the LoRA adapter member joined the model identity — a frozen replay captured before adapters existed cannot
    // prove whether an adapter was applied, so every v4 fingerprint is hard-rejected and re-fitted once.
    public const int CurrentVersion = 5;

    private const char FingerprintSeparator = '.';
    private const int ParallelSlots = 1;
    private const int StableCaptureAttempts = 3;
    private const string RuntimeDefaultMode = "llama-runtime-default";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly IInstalledRuntimeStore _installedRuntimeStore =
        installedRuntimeStore ?? throw new ArgumentNullException(nameof(installedRuntimeStore));

    private readonly ILlamaCppBinaryManager _binaryManager =
        binaryManager ?? throw new ArgumentNullException(nameof(binaryManager));

    private readonly LlamaServerLaunchPolicyOptions _launchPolicyOptions =
        launchPolicyOptions ?? throw new ArgumentNullException(nameof(launchPolicyOptions));

    private readonly LaunchPolicyFileHashCache _fileHashCache = fileHashCache ?? throw new ArgumentNullException(nameof(fileHashCache));
    private readonly IGgufModelStore _modelStore = modelStore ?? throw new ArgumentNullException(nameof(modelStore));

    private readonly IGgufModelRegistry _modelRegistry =
        modelRegistry ?? throw new ArgumentNullException(nameof(modelRegistry));

    private readonly LlamaServerSupervisorOptions _supervisorOptions =
        supervisorOptions ?? throw new ArgumentNullException(nameof(supervisorOptions));

    internal long FullFileHashComputationCount => _fileHashCache.FullHashComputationCount;

    public Task<LaunchPolicyFingerprint> CaptureAsync(InferenceProfileRecord profile, string modelFilePath, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return CaptureAsync(ToInput(profile, modelFilePath), ct);
    }

    public async Task<bool> MatchesAsync(InferenceProfileRecord profile, string modelFilePath, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.LaunchPolicyFingerprintVersion != CurrentVersion
            || !TrySplitFingerprint(profile.LaunchPolicyFingerprint, out var expectedStrongHash, out var expectedValidationHash))
        {
            return false;
        }

        var currentValidationDocumentHash = await CaptureValueAsync(ToInput(profile, modelFilePath),
                includeContentHashes: false,
                ct)
            .ConfigureAwait(false);
        var currentValidationHash = BindValidationHash(expectedStrongHash, currentValidationDocumentHash);
        return string.Equals(currentValidationHash, expectedValidationHash, StringComparison.Ordinal);
    }

    private static InferenceProfileFingerprintInput ToInput(InferenceProfileRecord profile, string modelFilePath)
    {
        return new InferenceProfileFingerprintInput(profile.ModelName,
            profile.Role,
            profile.Backend,
            modelFilePath,
            profile.CtxSize,
            profile.NGpuLayers,
            profile.TensorSplit,
            profile.OverrideTensor,
            profile.KvTypeK,
            profile.KvTypeV,
            profile.FlashAttn);
    }

    public async Task<LaunchPolicyFingerprint> CaptureAsync(InferenceProfileFingerprintInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.ModelName))
        {
            throw new ArgumentException("Model name must be non-empty.", nameof(input));
        }

        if (string.IsNullOrWhiteSpace(input.Backend))
        {
            throw new ArgumentException("Backend must be non-empty.", nameof(input));
        }

        if (string.IsNullOrWhiteSpace(input.ModelFilePath))
        {
            throw new ArgumentException("Model file path must be non-empty.", nameof(input));
        }

        ct.ThrowIfCancellationRequested();

        for (var attempt = 0; attempt < StableCaptureAttempts; attempt++)
        {
            var validationBefore = await CaptureValueAsync(input, includeContentHashes: false, ct).ConfigureAwait(false);
            var strongHash = await CaptureValueAsync(input, includeContentHashes: true, ct).ConfigureAwait(false);
            var validationAfter = await CaptureValueAsync(input, includeContentHashes: false, ct).ConfigureAwait(false);
            if (string.Equals(validationBefore, validationAfter, StringComparison.Ordinal))
            {
                var boundValidationHash = BindValidationHash(strongHash, validationAfter);
                return new LaunchPolicyFingerprint(CurrentVersion,
                    string.Concat(strongHash, FingerprintSeparator, boundValidationHash));
            }
        }

        throw new IOException("The model or selected llama.cpp runtime changed while its launch-policy fingerprint was being captured. Retry after file updates finish.");
    }

    private async Task<string> CaptureValueAsync(InferenceProfileFingerprintInput input,
        bool includeContentHashes,
        CancellationToken ct)
    {
        var file = new FileInfo(input.ModelFilePath);
        file.Refresh();
        if (!file.Exists)
        {
            throw new FileNotFoundException("The local GGUF file no longer exists.", input.ModelFilePath);
        }

        var modelIdentity = await ResolveModelIdentityAsync(input.ModelName,
                input.ModelFilePath,
                file,
                includeContentHashes,
                ct)
            .ConfigureAwait(false);
        var runtime = await _installedRuntimeStore.ReadAsync(ct).ConfigureAwait(false);
        var requestedVariant = input.Backend.ToUpperInvariant() switch
        {
            "CUDA" => GpuVariant.Cuda,
            "VULKAN" => GpuVariant.Vulkan,
            "CPU" => GpuVariant.Cpu,
            _ => throw new ArgumentException("Backend is not a supported llama.cpp variant.", nameof(input))
        };
        var binary = await _binaryManager.EnsureBinaryAsync(requestedVariant, ct).ConfigureAwait(false);
        var executableIdentity = includeContentHashes
            ? await _fileHashCache.GetSha256Async(binary.ServerExecutablePath, ct).ConfigureAwait(false)
            : await RuntimeBundleIdentityCalculator.GetFileValidationIdentityAsync(binary.ServerExecutablePath,
                                                       _fileHashCache,
                                                       ct)
                                                   .ConfigureAwait(false);
        var runtimeBundle = await RuntimeBundleIdentityCalculator.ComputeAsync(binary.ServerExecutablePath,
                                                                     (path, token) => includeContentHashes
                                                                         ? _fileHashCache.GetSha256Async(path, token)
                                                                         : RuntimeBundleIdentityCalculator.GetFileValidationIdentityAsync(path, _fileHashCache, token),
                                                                     ct)
                                                                 .ConfigureAwait(false);
        var isOperatorOverride = string.Equals(binary.Version, "override", StringComparison.OrdinalIgnoreCase);
        var isManagedSourceBuild = !isOperatorOverride && runtime?.SourceBuildPath is not null;
        var runtimeProvenance = "prebuilt-or-unavailable";
        if (isOperatorOverride)
        {
            runtimeProvenance = "operator-override";
        }
        else if (isManagedSourceBuild)
        {
            runtimeProvenance = "managed-source-build";
        }

        var role = Enum.IsDefined(typeof(ModelRole), input.Role)
            ? (ModelRole)input.Role
            : (ModelRole?)null;
        var roleIdentity = role?.ToString() ?? $"unknown:{input.Role}";
        var effectivePerSequenceContext = ResolvePerSequenceContext(input.CtxSize, ParallelSlots, kvUnified: false);
        var chatLaunch = await ResolveChatLaunchIdentityAsync(role, includeContentHashes, ct).ConfigureAwait(false);
        var cpuThreadPolicy = ResolveCpuThreadPolicyIdentity(requestedVariant);

        var canonical = new
        {
            schemaVersion = CurrentVersion,
            contentIdentityMode = includeContentHashes ? "strong-sha256" : "validation-stamp-v1",
            runtime = new
            {
                tag = isOperatorOverride ? "not-applicable" : runtime?.Tag ?? "unavailable",
                asset = isOperatorOverride ? "not-applicable" : runtime?.Asset ?? "unavailable",
                archiveSha256 = isOperatorOverride ? "not-applicable" : runtime?.Sha256 ?? "unavailable",
                variant = isOperatorOverride ? "not-applicable" : runtime?.Variant.ToString() ?? "unavailable",
                provenance = runtimeProvenance,
                sourceRepository = isManagedSourceBuild ? runtime?.SourceRepository : null,
                sourceCommit = isManagedSourceBuild ? runtime?.SourceCommit : null,
                selectedExecutable = new
                {
                    version = binary.Version,
                    variant = binary.Variant.ToString(),
                    contentIdentity = executableIdentity,
                    runtimeBundleIdentity = runtimeBundle.Identity,
                    runtimeBundle.FileCount,
                    binary.IsPinnedFallback
                }
            },
            model = new
            {
                identity = input.ModelName,
                identitySource = modelIdentity.Source,
                contentIdentity = modelIdentity.ContentIdentity,
                guardSha256 = modelIdentity.GuardSha256,
                fileLengthBytes = file.Length,
                fileLastWriteUtcTicks = file.LastWriteTimeUtc.Ticks,
                adapter = await ResolveAdapterIdentityAsync(input.ModelName, ct).ConfigureAwait(false)
            },
            launch = new
            {
                contextAllocation = new
                {
                    policyVersion = LlamaServerLaunchPolicyOptions.ContextAllocationPolicyVersion,
                    precedence = "frozen-profile>deterministic-override>hardware-tier",
                    chatTiers = LlamaServerLaunchPolicyOptions.ChatContextTiers,
                    roleDefaults = new
                    {
                        providerFallbackChatTokens = _launchPolicyOptions.ChatContextTokens,
                        embeddingTokens = _launchPolicyOptions.EmbeddingContextTokens,
                        rerankerTokens = _launchPolicyOptions.RerankerContextTokens
                    },
                    deterministicOverrideTokens = _launchPolicyOptions.DeterministicContextTokensOverride,
                    trainCeilingSafetyMarginTokens = _launchPolicyOptions.ContextSafetyMarginTokens,
                    contextAlignmentTokens = LlamaServerLaunchPolicyOptions.ContextAlignmentTokens,
                    gpuReserve = new
                    {
                        fraction = LlamaServerLaunchPolicyOptions.GpuReserveFraction,
                        minimumBytes = LlamaServerLaunchPolicyOptions.MinimumGpuReserveBytes
                    },
                    ramReserve = new
                    {
                        fraction = LlamaServerLaunchPolicyOptions.RamReserveFraction,
                        minimumBytes = LlamaServerLaunchPolicyOptions.MinimumRamReserveBytes
                    },
                    kvFootprintBaseline = "f16-conservative",
                    admission = "global-free-gpu-and-available-ram",
                    processAllocationEvidence = "stable-total-or-process-budget-no-live-free-sample"
                },
                role = roleIdentity,
                backend = input.Backend.ToUpperInvariant(),
                parallel = ParallelSlots,
                batch = new
                {
                    mode = RuntimeDefaultMode,
                    value = (int?)null
                },
                microBatch = new
                {
                    mode = RuntimeDefaultMode,
                    value = (int?)null
                },
                requestedTotalContextTokens = input.CtxSize,
                effectiveTotalContextTokens = effectivePerSequenceContext * ParallelSlots,
                perSequenceContext = new
                {
                    derivation = "ceil((requested-total / parallel) / 256) * 256",
                    tokens = effectivePerSequenceContext
                },
                kvUnified = false,
                noWarmup = true,
                input.NGpuLayers,
                input.TensorSplit,
                input.OverrideTensor,
                input.KvTypeK,
                input.KvTypeV,
                input.FlashAttn,
                chat = chatLaunch,
                cpuThreadPolicy
            }
        };

        var json = JsonSerializer.Serialize(canonical, SerializerOptions);

        // D13 — the node's SELECTED KV-cache type is part of a profile's identity, so a frozen profile explored under a
        // different type goes stale and re-explores instead of replaying a type the operator has since changed. The KV
        // pair inside the canonical document above is the profile's OWN frozen one (MatchesAsync rebuilds it from the
        // same row, so it can never mismatch); this is the node's current selection, which is a different fact.
        //
        // It is APPENDED after a complete JSON document rather than added as a member: the canonical shape is an
        // anonymous type serialized with JsonSerializerDefaults.Web, which writes nulls, so no member can be omitted
        // conditionally and adding one would change every hash ever produced. A fixed literal suffix can never be read
        // as document content. BindValidationHash already combines values outside the JSON the same way.
        //
        // And it is FOLDED at the default: a node that never touched the knob hashes exactly the bytes it has always
        // hashed, so shipping this invalidates no stored profile. A CPU spawn never quantizes KV, so a CPU row must not
        // go stale for a knob that cannot reach it.
        var selectedKv = ResolveSelectedKvCacheIdentity();
        if (requestedVariant != GpuVariant.Cpu
            && !string.Equals(selectedKv, LlamaServerKvCacheTypes.Q8_0, StringComparison.Ordinal))
        {
            json = string.Concat(json, "|selectedKvCacheType=", selectedKv);
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    // The KV-cache type this node would launch with today. EnableGpuKvCacheQuantization is what the launch policy
    // actually tests, so the flag and the type collapse into one token here: quantization off reads as f16 whatever the
    // type string says.
    private string ResolveSelectedKvCacheIdentity() =>
        _launchPolicyOptions.EnableGpuKvCacheQuantization
            ? _launchPolicyOptions.KvCacheType
            : LlamaServerKvCacheTypes.F16;

    /// <summary>
    ///     The adapter member of a LoRA model's identity — <see langword="null" /> for an ordinary model, so an entry
    ///     without an adapter contributes a stable absent marker rather than a shifting shape. Uses the registry's
    ///     recorded member fingerprint (not a file read): it already commits to the adapter's bytes and size, and the
    ///     entry's own RegistryRevision commits to it in turn.
    /// </summary>
    private async Task<object?> ResolveAdapterIdentityAsync(string modelName, CancellationToken ct)
    {
        var entry = await _modelRegistry.FindAsync(modelName, ct).ConfigureAwait(false);
        if (entry?.AdapterFileName is null)
        {
            return null;
        }

        return new
        {
            fileName = entry.AdapterFileName,
            memberFingerprint = entry.AdapterMemberFingerprint,
            sizeBytes = entry.AdapterSizeBytes,
            baseModelName = entry.BaseModelName
        };
    }

    private async Task<object?> ResolveChatLaunchIdentityAsync(ModelRole? role,
        bool includeContentHashes,
        CancellationToken ct)
    {
        if (role != ModelRole.Chat)
        {
            return null;
        }

        var speculative = _supervisorOptions.Speculative;
        var mode = speculative.NormalizedMode;
        if (!speculative.IsEnabled)
        {
            return new
            {
                cacheReuseTokens = _supervisorOptions.ChatCacheReuse > 0
                    ? _supervisorOptions.ChatCacheReuse
                    : (int?)null,
                speculative = new
                {
                    mode
                }
            };
        }

        if (!speculative.RequiresExternalDraftModel)
        {
            // No second GGUF to identify: ngram-* self-speculates and draft-mtp drafts from heads in the main model.
            // draft-mtp does still honour --spec-draft-n-max, so that knob stays part of the launch identity for it.
            var draftMaxTokens = speculative.DraftMaxTokens > 0 ? speculative.DraftMaxTokens : (int?)null;
            object identity = speculative.ModeClass is SpeculativeModeClass.MainModelHeads
                ? new
                {
                    mode,
                    draftMaxTokens
                }
                : new
                {
                    mode
                };

            return new
            {
                cacheReuseTokens = _supervisorOptions.ChatCacheReuse > 0
                    ? _supervisorOptions.ChatCacheReuse
                    : (int?)null,
                speculative = identity
            };
        }

        var draftModelPath = speculative.DraftModelPath;
        if (string.IsNullOrWhiteSpace(draftModelPath)
            && !string.IsNullOrWhiteSpace(_supervisorOptions.SpeculativeDraftModelName))
        {
            draftModelPath = await _modelStore.ResolveModelFilePathAsync(_supervisorOptions.SpeculativeDraftModelName,
                                                  ct)
                                              .ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(draftModelPath) || !File.Exists(draftModelPath))
        {
            throw new FileNotFoundException("The configured speculative-decoding draft GGUF file no longer exists.",
                draftModelPath);
        }

        return new
        {
            cacheReuseTokens = _supervisorOptions.ChatCacheReuse > 0
                ? _supervisorOptions.ChatCacheReuse
                : (int?)null,
            speculative = new
            {
                mode,
                draftModel = new
                {
                    configuredName = _supervisorOptions.SpeculativeDraftModelName,
                    resolvedFileName = Path.GetFileName(draftModelPath),
                    identity = await ResolveModelIdentityAsync(_supervisorOptions.SpeculativeDraftModelName,
                            draftModelPath,
                            new FileInfo(draftModelPath),
                            includeContentHashes,
                            ct)
                        .ConfigureAwait(false)
                },
                draftMaxTokens = speculative.DraftMaxTokens > 0 ? speculative.DraftMaxTokens : (int?)null,
                speculative.DraftGpuLayers
            }
        };
    }

    private object? ResolveCpuThreadPolicyIdentity(GpuVariant variant)
    {
        if (variant != GpuVariant.Cpu)
        {
            return null;
        }

        var logicalProcessorCount = Environment.ProcessorCount;
        var estimatedPhysicalProcessorCount =
            _launchPolicyOptions.AssumeSimultaneousMultithreading && logicalProcessorCount >= 2
                ? logicalProcessorCount / 2
                : logicalProcessorCount;
        estimatedPhysicalProcessorCount = Math.Max(estimatedPhysicalProcessorCount, 1);

        var threads = _launchPolicyOptions.EnableCpuThreadPolicy
            ? _launchPolicyOptions.CpuThreadCount
              ?? Math.Max(estimatedPhysicalProcessorCount - _launchPolicyOptions.CpuThreadReserve, 1)
            : (int?)null;
        var threadsBatch = _launchPolicyOptions.EnableCpuThreadPolicy
            ? _launchPolicyOptions.CpuThreadsBatchCount ?? estimatedPhysicalProcessorCount
            : (int?)null;

        return new
        {
            _launchPolicyOptions.EnableCpuThreadPolicy,
            _launchPolicyOptions.AssumeSimultaneousMultithreading,
            _launchPolicyOptions.CpuThreadReserve,
            _launchPolicyOptions.CpuThreadCount,
            _launchPolicyOptions.CpuThreadsBatchCount,
            logicalProcessorCount,
            estimatedPhysicalProcessorCount,
            resolvedThreads = threads,
            resolvedThreadsBatch = threadsBatch
        };
    }

    private static int ResolvePerSequenceContext(int requestedTotalContext, int parallel, bool kvUnified)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(requestedTotalContext, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(parallel, 1);

        if (kvUnified)
        {
            return requestedTotalContext;
        }

        const int contextAlignment = 256;
        var divided = Math.Max(1, requestedTotalContext / parallel);
        return checked(((divided + contextAlignment - 1) / contextAlignment) * contextAlignment);
    }

    private async Task<ModelContentIdentity> ResolveModelIdentityAsync(string? modelName,
        string filePath,
        FileInfo file,
        bool includeContentHashes,
        CancellationToken ct)
    {
        file.Refresh();
        if (!file.Exists)
        {
            throw new FileNotFoundException("The local GGUF file no longer exists.", filePath);
        }

        if (!string.IsNullOrWhiteSpace(modelName))
        {
            var entry = await _modelRegistry.FindAsync(modelName, ct).ConfigureAwait(false);
            if (entry is not null
                && PathsEqual(entry.LocalPath, file.FullName)
                && entry.SizeBytes == file.Length
                && TryNormalizeSha256(entry.Sha256, out var registrySha256))
            {
                var guard = await _fileHashCache.GetGuardSha256Async(file.FullName, ct).ConfigureAwait(false);
                return includeContentHashes
                    ? new ModelContentIdentity("verified-registry-sha256", registrySha256, guard)
                    : new ModelContentIdentity("validation-stamp-v1",
                        RuntimeBundleIdentityCalculator.BuildValidationIdentity(file, guard, registrySha256),
                        guard);
            }
        }

        if (includeContentHashes)
        {
            return new ModelContentIdentity("memoized-local-file-sha256",
                await _fileHashCache.GetSha256Async(file.FullName, ct).ConfigureAwait(false),
                GuardSha256: null);
        }

        var validationGuard = await _fileHashCache.GetGuardSha256Async(file.FullName, ct).ConfigureAwait(false);
        return new ModelContentIdentity("validation-stamp-v1",
            RuntimeBundleIdentityCalculator.BuildValidationIdentity(file, validationGuard, authoritySha256: null),
            validationGuard);
    }

    private static bool PathsEqual(string left, string right)
    {
        return string.Equals(Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static bool TryNormalizeSha256(string? value, out string normalized)
    {
        if (value is { Length: 64 } && value.All(Uri.IsHexDigit))
        {
            normalized = value.ToUpperInvariant();
            return true;
        }

        normalized = string.Empty;
        return false;
    }

    private static bool TrySplitFingerprint(string? value,
        out string strongHash,
        out string validationHash)
    {
        strongHash = string.Empty;
        validationHash = string.Empty;
        if (value is not { Length: 129 } || value[64] != FingerprintSeparator)
        {
            return false;
        }

        var candidateStrong = value[..64];
        var candidateValidation = value[65..];
        if (!candidateStrong.All(Uri.IsHexDigit) || !candidateValidation.All(Uri.IsHexDigit))
        {
            return false;
        }

        strongHash = candidateStrong;
        validationHash = candidateValidation;
        return true;
    }

    private static string BindValidationHash(string strongHash, string validationDocumentHash)
    {
        var canonical = string.Concat(strongHash, ":", validationDocumentHash);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private sealed record ModelContentIdentity(string Source, string ContentIdentity, string? GuardSha256);
}
