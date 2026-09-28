namespace XE_Local_AI_Engine.Client.Services.Benchmarks.Implementation;

using System.Runtime.InteropServices;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Inference;
using XE_Local_AI_Engine.Client.Services.Inference.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <inheritdoc />
/// <remarks>
///     Bounded by construction: one directory enumerate plus a guard-sample read per bundle file (the fingerprint's
///     cheap identity mode — no whole-file hashing), the memoized hardware profile and the memoized device audit, and
///     one installed-runtime file read. Registered as a singleton, sharing the launch-policy file-hash cache rather
///     than starting a second set of file-system watchers over the same directory.
/// </remarks>
public sealed class RuntimeEnvironmentFactsProvider : IRuntimeEnvironmentFactsProvider
{
    public const int SchemaVersion = 1;

    private const string BundlePart = "runtimeBundle";
    private const string HardwarePart = "hardware";
    private const string LlamaRuntimePart = "llamaRuntime";

    private readonly ILlamaCppBinaryManager _binaryManager;
    private readonly IInstalledRuntimeStore _installedRuntimeStore;
    private readonly IHardwareProfiler _hardwareProfiler;
    private readonly IRuntimeDeviceAudit _deviceAudit;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RuntimeEnvironmentFactsProvider> _logger;

    // Shared with the launch-policy fingerprint: the cache owns a file-system watcher per directory, and a second
    // instance would watch the same runtime directory twice for the same answers. Disposed by the container.
    private readonly LaunchPolicyFileHashCache _fileHashCache;

    public RuntimeEnvironmentFactsProvider(ILlamaCppBinaryManager binaryManager,
        IInstalledRuntimeStore installedRuntimeStore,
        IHardwareProfiler hardwareProfiler,
        IRuntimeDeviceAudit deviceAudit,
        LaunchPolicyFileHashCache fileHashCache,
        TimeProvider timeProvider,
        ILogger<RuntimeEnvironmentFactsProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(binaryManager);
        ArgumentNullException.ThrowIfNull(installedRuntimeStore);
        ArgumentNullException.ThrowIfNull(hardwareProfiler);
        ArgumentNullException.ThrowIfNull(deviceAudit);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(fileHashCache);
        _binaryManager = binaryManager;
        _installedRuntimeStore = installedRuntimeStore;
        _hardwareProfiler = hardwareProfiler;
        _deviceAudit = deviceAudit;
        _timeProvider = timeProvider;
        _logger = logger;
        _fileHashCache = fileHashCache;
    }

    public async Task<RuntimeEnvironmentFactsV1> CaptureAsync(GpuVariant variant, CancellationToken ct)
    {
        var missing = new List<string>();
        LlamaBinary? binary = null;
        var bundle = await CapturePartAsync(BundlePart,
            async () =>
            {
                binary = await _binaryManager.EnsureBinaryAsync(variant, ct);
                return await RuntimeBundleIdentityCalculator.ComputeAsync(binary.ServerExecutablePath,
                    (path, token) => RuntimeBundleIdentityCalculator.GetFileValidationIdentityAsync(path, _fileHashCache, token),
                    ct);
            },
            missing,
            ct);

        var llamaRuntime = await CapturePartAsync(LlamaRuntimePart,
            async () => ToLlamaRuntimeFacts(await _installedRuntimeStore.ReadAsync(ct), binary),
            missing,
            ct);

        var hardware = await CapturePartAsync(HardwarePart,
            async () =>
            {
                var profile = await _hardwareProfiler.GetProfileAsync(forceRefresh: false, ct);
                var audit = await _deviceAudit.GetAuditAsync(forceRefresh: false, ct);
                return new BenchmarkHardwareFactsV1(RuntimeInformation.OSDescription,
                    RuntimeInformation.OSArchitecture.ToString(),
                    TryReadCpuModel(),
                    profile.CpuCores,
                    profile.TotalRamBytes,
                    audit.Devices.Select(static device => new BenchmarkGpuFactsV1(device.Name, device.TotalBytes, DriverVersion: null)).ToArray(),
                    audit.InferenceBackend);
            },
            missing,
            ct);

        return new RuntimeEnvironmentFactsV1(SchemaVersion,
            bundle,
            hardware,
            llamaRuntime,
            _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            missing);
    }

    private static BenchmarkLlamaRuntimeFactsV1? ToLlamaRuntimeFacts(InstalledRuntimeState? runtime, LlamaBinary? binary)
    {
        if (runtime is null && binary is null)
        {
            return null;
        }

        var isOperatorOverride = string.Equals(binary?.Version, "override", StringComparison.OrdinalIgnoreCase);
        var isManagedSourceBuild = !isOperatorOverride && runtime?.SourceBuildPath is not null;
        var provenance = "prebuilt-or-unavailable";
        if (isOperatorOverride)
        {
            provenance = "operator-override";
        }
        else if (isManagedSourceBuild)
        {
            provenance = "managed-source-build";
        }

        var variant = binary?.Variant ?? runtime?.Variant;
        return new BenchmarkLlamaRuntimeFactsV1(binary?.Version ?? runtime?.Tag ?? "unavailable",
            variant is null ? "unavailable" : BenchmarkLaunchBackend.VariantName(variant.Value),
            provenance,
            isManagedSourceBuild ? runtime?.SourceCommit : null);
    }

    private static string? TryReadCpuModel()
    {
        if (OperatingSystem.IsWindows())
        {
            return Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER");
        }

        const string cpuInfoPath = "/proc/cpuinfo";
        if (!File.Exists(cpuInfoPath))
        {
            return null;
        }

        foreach (var line in File.ReadLines(cpuInfoPath))
        {
            var separator = line.IndexOf(':', StringComparison.Ordinal);
            if (separator > 0 && line.StartsWith("model name", StringComparison.Ordinal))
            {
                return line[(separator + 1)..].Trim();
            }
        }

        return null;
    }

    private async Task<T?> CapturePartAsync<T>(string part, Func<Task<T?>> capture, List<string> missing, CancellationToken ct)
        where T : class
    {
        try
        {
            var captured = await capture();
            if (captured is null)
            {
                missing.Add(part);
            }

            return captured;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "Benchmark environment facts: the {Part} part could not be captured.", part);
            missing.Add(part);
            return null;
        }
    }
}
