namespace XE_Local_AI_Engine.Client.Services.Inference;

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Stores;
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

/// <summary>Persisted launch-policy identity: schema version plus lowercase SHA-256 of the canonical document.</summary>
public sealed record LaunchPolicyFingerprint(int Version, string Value);

public sealed class LaunchPolicyFingerprintProvider(
    IInstalledRuntimeStore installedRuntimeStore,
    ILlamaCppBinaryManager binaryManager) : ILaunchPolicyFingerprintProvider
{
    public const int CurrentVersion = 2;

    private const int ParallelSlots = 1;
    private const string RuntimeDefaultMode = "llama-runtime-default";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly IInstalledRuntimeStore _installedRuntimeStore =
        installedRuntimeStore ?? throw new ArgumentNullException(nameof(installedRuntimeStore));
    private readonly ILlamaCppBinaryManager _binaryManager =
        binaryManager ?? throw new ArgumentNullException(nameof(binaryManager));

    public Task<LaunchPolicyFingerprint> CaptureAsync(InferenceProfileRecord profile, string modelFilePath, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return CaptureAsync(new InferenceProfileFingerprintInput(profile.ModelName,
                profile.Role,
                profile.Backend,
                modelFilePath,
                profile.CtxSize,
                profile.NGpuLayers,
                profile.TensorSplit,
                profile.OverrideTensor,
                profile.KvTypeK,
                profile.KvTypeV,
                profile.FlashAttn),
            ct);
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

        var file = new FileInfo(input.ModelFilePath);
        file.Refresh();
        if (!file.Exists)
        {
            throw new FileNotFoundException("The local GGUF file no longer exists.", input.ModelFilePath);
        }

        var runtime = await _installedRuntimeStore.ReadAsync(ct).ConfigureAwait(false);
        var requestedVariant = input.Backend.ToUpperInvariant() switch
        {
            "CUDA" => GpuVariant.Cuda,
            "VULKAN" => GpuVariant.Vulkan,
            "CPU" => GpuVariant.Cpu,
            _ => throw new ArgumentException("Backend is not a supported llama.cpp variant.", nameof(input))
        };
        var binary = await _binaryManager.EnsureBinaryAsync(requestedVariant, ct).ConfigureAwait(false);
        var executableSha256 = await ComputeFileSha256Async(binary.ServerExecutablePath, ct).ConfigureAwait(false);
        var runtimeBundle = await ComputeRuntimeBundleIdentityAsync(binary.ServerExecutablePath, ct).ConfigureAwait(false);
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
            ? ((ModelRole)input.Role).ToString()
            : $"unknown:{input.Role}";
        var effectivePerSequenceContext = ResolvePerSequenceContext(input.CtxSize, ParallelSlots, kvUnified: false);

        var canonical = new
        {
            schemaVersion = CurrentVersion,
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
                    sha256 = executableSha256,
                    runtimeBundleSha256 = runtimeBundle.Sha256,
                    runtimeBundle.FileCount,
                    binary.IsPinnedFallback
                }
            },
            model = new
            {
                identity = input.ModelName,
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
                    auxiliaryRoleCapTokens = 2048,
                    trainCeilingSafetyMarginTokens = 256,
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
                role,
                backend = input.Backend.ToUpperInvariant(),
                parallel = ParallelSlots,
                batch = new { mode = RuntimeDefaultMode, value = (int?)null },
                microBatch = new { mode = RuntimeDefaultMode, value = (int?)null },
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
                input.FlashAttn
            }
        };

        var json = JsonSerializer.Serialize(canonical, SerializerOptions);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        return new LaunchPolicyFingerprint(CurrentVersion, hash);
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

    private static async Task<string> ComputeFileSha256Async(string filePath, CancellationToken ct)
    {
        await using var stream = new FileStream(filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
    }

    private static async Task<RuntimeBundleIdentity> ComputeRuntimeBundleIdentityAsync(
        string serverExecutablePath,
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
        var buffer = new byte[128 * 1024];
        foreach (var path in files)
        {
            ct.ThrowIfCancellationRequested();
            var nameBytes = Encoding.UTF8.GetBytes(Path.GetFileName(path));
            var nameLength = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(nameLength, nameBytes.Length);
            var fileLength = new byte[sizeof(long)];
            BinaryPrimitives.WriteInt64LittleEndian(fileLength, new FileInfo(path).Length);
            bundleHash.AppendData(nameLength);
            bundleHash.AppendData(nameBytes);
            bundleHash.AppendData(fileLength);

            await using var stream = new FileStream(path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                buffer.Length,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            while (true)
            {
                var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                bundleHash.AppendData(buffer, 0, read);
            }
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

    private sealed record RuntimeBundleIdentity(string Sha256, int FileCount);
}
