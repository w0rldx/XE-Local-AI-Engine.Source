namespace XE_Local_AI_Engine.Client.Services.Inference;

using System.Buffers.Binary;
using System.Globalization;
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

/// <summary>Inputs needed before an explored profile has been persisted.</summary>
public sealed record InferenceProfileFingerprintInput(
    string ModelName,
    int Role,
    string Backend,
    string ModelFilePath,
    int CtxSize,
    int? NGpuLayers,
    string? TensorSplit,
    string? OverrideTensor,
    string? KvTypeK,
    string? KvTypeV,
    bool FlashAttn);

/// <summary>
///     Persisted launch-policy identity: schema version plus a strong capture hash and a cheap validation hash. The
///     validation half lets cold-spawn staleness checks avoid streaming multi-gigabyte model/runtime files.
/// </summary>
public sealed record LaunchPolicyFingerprint(int Version, string Value);

public sealed class LaunchPolicyFingerprintProvider(
    IInstalledRuntimeStore installedRuntimeStore,
    ILlamaCppBinaryManager binaryManager,
    IGgufModelStore modelStore,
    IGgufModelRegistry modelRegistry,
    LlamaServerSupervisorOptions supervisorOptions,
    LlamaServerLaunchPolicyOptions launchPolicyOptions) : ILaunchPolicyFingerprintProvider, IDisposable
{
    public const int CurrentVersion = 4;

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

    private readonly LaunchPolicyFileHashCache _fileHashCache = new();
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
            : await GetFileValidationIdentityAsync(binary.ServerExecutablePath, ct).ConfigureAwait(false);
        var runtimeBundle = await ComputeRuntimeBundleIdentityAsync(binary.ServerExecutablePath,
                includeContentHashes,
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
                    runtimeBundleIdentity = runtimeBundle.Sha256,
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
                fileLastWriteUtcTicks = file.LastWriteTimeUtc.Ticks
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
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    public void Dispose()
    {
        _fileHashCache.Dispose();
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

        if (!speculative.IsDraftMode)
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
                        BuildValidationIdentity(file, guard, registrySha256),
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
            BuildValidationIdentity(file, validationGuard, authoritySha256: null),
            validationGuard);
    }

    private async Task<RuntimeBundleIdentity> ComputeRuntimeBundleIdentityAsync(string serverExecutablePath,
        bool includeContentHashes,
        CancellationToken ct)
    {
        var executablePath = Path.GetFullPath(serverExecutablePath);
        var directory = Path.GetDirectoryName(executablePath)
                        ?? throw new InvalidOperationException("The selected llama-server path has no parent directory.");
        var files = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                             .Where(path => IsRuntimeBundleFile(path, executablePath))
                             .OrderBy(Path.GetFileName, StringComparer.Ordinal)
                             .ToArray();
        if (files.Length == 0)
        {
            throw new FileNotFoundException("The selected llama-server runtime bundle no longer exists.", executablePath);
        }

        using var bundleHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in files)
        {
            ct.ThrowIfCancellationRequested();
            var nameBytes = Encoding.UTF8.GetBytes(Path.GetFileName(path));
            var nameLength = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(nameLength, nameBytes.Length);
            var fileLength = new byte[sizeof(long)];
            var file = new FileInfo(path);
            file.Refresh();
            BinaryPrimitives.WriteInt64LittleEndian(fileLength, file.Length);
            bundleHash.AppendData(nameLength);
            bundleHash.AppendData(nameBytes);
            bundleHash.AppendData(fileLength);
            var contentIdentity = includeContentHashes
                ? await _fileHashCache.GetSha256Async(path, ct).ConfigureAwait(false)
                : await GetFileValidationIdentityAsync(path, ct).ConfigureAwait(false);
            bundleHash.AppendData(Convert.FromHexString(contentIdentity));
        }

        return new RuntimeBundleIdentity(Convert.ToHexStringLower(bundleHash.GetHashAndReset()), files.Length);
    }

    private static bool IsRuntimeBundleFile(string path, string executablePath)
    {
        if (string.Equals(Path.GetFullPath(path), executablePath, StringComparison.Ordinal))
        {
            return true;
        }

        var name = Path.GetFileName(path);
        return name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(".dylib", StringComparison.OrdinalIgnoreCase)
               || name.Contains(".so", StringComparison.OrdinalIgnoreCase);
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

    private async Task<string> GetFileValidationIdentityAsync(string filePath, CancellationToken ct)
    {
        var file = new FileInfo(filePath);
        file.Refresh();
        if (!file.Exists)
        {
            throw new FileNotFoundException("The fingerprinted file no longer exists.", filePath);
        }

        var guard = await _fileHashCache.GetGuardSha256Async(file.FullName, ct).ConfigureAwait(false);
        return BuildValidationIdentity(file, guard, authoritySha256: null);
    }

    private static string BuildValidationIdentity(FileInfo file, string guardSha256, string? authoritySha256)
    {
        var canonical = string.Create(CultureInfo.InvariantCulture,
            $"{file.Length}:{file.LastWriteTimeUtc.Ticks}:{file.CreationTimeUtc.Ticks}:{guardSha256}:{authoritySha256 ?? "unavailable"}");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
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

    private sealed record RuntimeBundleIdentity(string Sha256, int FileCount);

    private sealed record ModelContentIdentity(string Source, string ContentIdentity, string? GuardSha256);
}
