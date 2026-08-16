namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using System.Runtime.InteropServices;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Inference;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>The host facts a benchmark run was launched on. Aggregates only — no hostnames, serials or paths.</summary>
/// <param name="DriverVersion">
///     Always <see langword="null" /> today: the device audit enumerates through the runtime itself, which reports no
///     driver version. Present so a later probe can fill it without a schema change.
/// </param>
public sealed record BenchmarkGpuFactsV1(string Name, long? TotalBytes, string? DriverVersion);

/// <param name="OsDescription">
///     The host OS as the runtime describes it ("Linux 6.18.33.2-microsoft-standard-WSL2 ..."). Deliberately NOT named
///     <c>os</c>: the receipt already carries a bounded <c>os</c> token, and two same-named fields of different shapes
///     read as a contradiction in a field-by-field diff.
/// </param>
/// <param name="DeviceAuditBackend">What the selected runtime actually enumerated: <c>cuda|vulkan|cpu|unknown</c>.</param>
public sealed record BenchmarkHardwareFactsV1(
    string OsDescription,
    string Arch,
    string? CpuModel,
    int LogicalCores,
    long RamBytes,
    IReadOnlyList<BenchmarkGpuFactsV1> Gpus,
    string DeviceAuditBackend);

/// <param name="Provenance">
///     <c>operator-override</c> | <c>managed-source-build</c> | <c>prebuilt-or-unavailable</c> — the same three values
///     the launch-policy fingerprint commits to.
/// </param>
public sealed record BenchmarkLlamaRuntimeFactsV1(string Version, string Variant, string Provenance, string? SourceCommit);

/// <summary>
///     What the node looked like immediately before a benchmark run's llama-server spawn: the selected runtime bundle,
///     the host hardware, and the installed llama.cpp runtime's provenance. Facts only — this record makes no claim
///     that two runs carrying the same facts are comparable.
/// </summary>
/// <param name="Missing">
///     The parts that could not be captured, by name (<c>runtimeBundle</c>, <c>hardware</c>, <c>llamaRuntime</c>). A
///     capture never fails the run, so an empty list is the only proof that everything was observed. It IS part of the
///     hash: a part that could not be read is itself an environmental fact.
/// </param>
/// <param name="CapturedAtUtc">
///     When this capture was taken. Persisted, but deliberately NOT part of <c>EnvironmentFactsHash</c> — that hash
///     answers "is this the same environment?", and a wall clock would make two runs of an unchanged node differ.
/// </param>
public sealed record RuntimeEnvironmentFactsV1(
    int SchemaVersion,
    RuntimeBundleFactsV1? RuntimeBundle,
    BenchmarkHardwareFactsV1? Hardware,
    BenchmarkLlamaRuntimeFactsV1? LlamaRuntime,
    long CapturedAtUtc,
    IReadOnlyList<string> Missing);

public interface IRuntimeEnvironmentFactsProvider
{
    /// <summary>
    ///     Captures the environment facts for a spawn of <paramref name="variant" />. Never throws for a missing or
    ///     unreadable part (that part is <see langword="null" /> and named in
    ///     <see cref="RuntimeEnvironmentFactsV1.Missing" />); cancellation still propagates.
    /// </summary>
    Task<RuntimeEnvironmentFactsV1> CaptureAsync(GpuVariant variant, CancellationToken ct);
}

/// <inheritdoc />
/// <remarks>
///     Bounded by construction: one directory enumerate plus a guard-sample read per bundle file (the fingerprint's
///     cheap identity mode — no whole-file hashing), the memoized hardware profile and the memoized device audit, and
///     one installed-runtime file read. Registered as a singleton, sharing the launch-policy file-hash cache rather
///     than starting a second set of file-system watchers over the same directory.
/// </remarks>
public sealed class RuntimeEnvironmentFactsProvider(
    ILlamaCppBinaryManager binaryManager,
    IInstalledRuntimeStore installedRuntimeStore,
    IHardwareProfiler hardwareProfiler,
    IRuntimeDeviceAudit deviceAudit,
    LaunchPolicyFileHashCache fileHashCache,
    TimeProvider timeProvider,
    ILogger<RuntimeEnvironmentFactsProvider> logger) : IRuntimeEnvironmentFactsProvider
{
    public const int SchemaVersion = 1;

    private const string BundlePart = "runtimeBundle";
    private const string HardwarePart = "hardware";
    private const string LlamaRuntimePart = "llamaRuntime";

    private readonly ILlamaCppBinaryManager _binaryManager = binaryManager ?? throw new ArgumentNullException(nameof(binaryManager));

    private readonly IInstalledRuntimeStore _installedRuntimeStore =
        installedRuntimeStore ?? throw new ArgumentNullException(nameof(installedRuntimeStore));

    private readonly IHardwareProfiler _hardwareProfiler = hardwareProfiler ?? throw new ArgumentNullException(nameof(hardwareProfiler));
    private readonly IRuntimeDeviceAudit _deviceAudit = deviceAudit ?? throw new ArgumentNullException(nameof(deviceAudit));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly ILogger<RuntimeEnvironmentFactsProvider> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    // Shared with the launch-policy fingerprint: the cache owns a file-system watcher per directory, and a second
    // instance would watch the same runtime directory twice for the same answers. Disposed by the container.
    private readonly LaunchPolicyFileHashCache _fileHashCache = fileHashCache ?? throw new ArgumentNullException(nameof(fileHashCache));

    public async Task<RuntimeEnvironmentFactsV1> CaptureAsync(GpuVariant variant, CancellationToken ct)
    {
        var missing = new List<string>();
        LlamaBinary? binary = null;
        var bundle = await CapturePartAsync(BundlePart,
                async () =>
                {
                    binary = await _binaryManager.EnsureBinaryAsync(variant, ct).ConfigureAwait(false);
                    return await RuntimeBundleIdentityCalculator.ComputeAsync(binary.ServerExecutablePath,
                            (path, token) => RuntimeBundleIdentityCalculator.GetFileValidationIdentityAsync(path, _fileHashCache, token),
                            ct)
                        .ConfigureAwait(false);
                },
                missing,
                ct)
            .ConfigureAwait(false);

        var llamaRuntime = await CapturePartAsync(LlamaRuntimePart,
                async () => ToLlamaRuntimeFacts(await _installedRuntimeStore.ReadAsync(ct).ConfigureAwait(false), binary),
                missing,
                ct)
            .ConfigureAwait(false);

        var hardware = await CapturePartAsync(HardwarePart,
                async () =>
                {
                    var profile = await _hardwareProfiler.GetProfileAsync(forceRefresh: false, ct).ConfigureAwait(false);
                    var audit = await _deviceAudit.GetAuditAsync(forceRefresh: false, ct).ConfigureAwait(false);
                    return new BenchmarkHardwareFactsV1(RuntimeInformation.OSDescription,
                        RuntimeInformation.OSArchitecture.ToString(),
                        TryReadCpuModel(),
                        profile.CpuCores,
                        profile.TotalRamBytes,
                        audit.Devices.Select(static device => new BenchmarkGpuFactsV1(device.Name, device.TotalBytes, DriverVersion: null)).ToArray(),
                        audit.InferenceBackend);
                },
                missing,
                ct)
            .ConfigureAwait(false);

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
            var captured = await capture().ConfigureAwait(false);
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
