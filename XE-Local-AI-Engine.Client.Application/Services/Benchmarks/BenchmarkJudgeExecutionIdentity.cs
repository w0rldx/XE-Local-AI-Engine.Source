namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>One GPU as the execution identity records it. Driver version is included when present, never required.</summary>
public sealed record BenchmarkJudgeExecutionGpuV1(
    [property: JsonPropertyOrder(0)]
    string Name,
    [property: JsonPropertyOrder(1)]
    long? TotalBytes,
    [property: JsonPropertyOrder(2)]
    string? DriverVersion);

/// <summary>
///     A stable, versioned projection of what a judging actually executed on: the effective launch receipt plus the
///     environment facts, reduced to the fields that can change a score's comparability. Deliberately excludes every
///     per-launch diagnostic — capture clock, file mtimes and sizes, pids, paths, timings — so two judgings on an
///     unchanged node produce the same value, while a runtime update, a different KV type or a moved placement do not.
/// </summary>
/// <remarks>
///     <para>
///         Property order is pinned because the record is canonically serialized and hashed into the rank-cohort key.
///         Reordering or renaming a member silently re-keys every cohort already stored.
///     </para>
///     <para>
///         <b>Accepted ceiling:</b> <see cref="RuntimeBundleIdentity" /> is the runtime's cheap identity (file names,
///         sizes, mtimes and sampled validation hashes) plus the executable's full fresh SHA-256. A shared library
///         edited in an unsampled region with size and mtime preserved would alias. That is not an operational path —
///         a runtime update moves the version, the sizes and the mtimes, all of which are in here. The upgrade path is
///         a content-addressed bundle identity computed at install time, swapped in as a v2 field.
///     </para>
/// </remarks>
public sealed record BenchmarkJudgeExecutionIdentityV1(
    [property: JsonPropertyOrder(0)]
    int SchemaVersion,
    [property: JsonPropertyOrder(1)]
    string ExecutableSha256,
    [property: JsonPropertyOrder(2)]
    string ExecutableVersion,
    [property: JsonPropertyOrder(3)]
    string Variant,
    [property: JsonPropertyOrder(4)]
    string EffectiveBackend,
    [property: JsonPropertyOrder(5)]
    string EffectiveLaunchIdentity,
    [property: JsonPropertyOrder(6)]
    string RuntimeBundleIdentity,
    [property: JsonPropertyOrder(7)]
    string LlamaRuntimeVersion,
    [property: JsonPropertyOrder(8)]
    string LlamaRuntimeProvenance,
    [property: JsonPropertyOrder(9)]
    string? LlamaRuntimeSourceCommit,
    [property: JsonPropertyOrder(10)]
    string OsDescription,
    [property: JsonPropertyOrder(11)]
    string Arch,
    [property: JsonPropertyOrder(12)]
    string PlacementOutcome,
    [property: JsonPropertyOrder(13)]
    int? PlacementOffloaded,
    [property: JsonPropertyOrder(14)]
    int? PlacementTotal,
    [property: JsonPropertyOrder(15)]
    IReadOnlyList<BenchmarkJudgeExecutionGpuV1> Gpus,
    [property: JsonPropertyOrder(16)]
    string? CpuModel,
    [property: JsonPropertyOrder(17)]
    int? LogicalCores,
    [property: JsonPropertyOrder(18)]
    long? RamBytes)
{
    public const int CurrentSchemaVersion = 1;
}

/// <summary>
///     Builds the rank-cohort key for one judging: <c>SHA-256(policyHash + canonical(identity))</c>.
/// </summary>
/// <remarks>
///     <b>Fail closed.</b> The key is computed only when every field the effective backend requires is present. Anything
///     missing yields <see langword="null" />, which makes the attempt permanently unranked with
///     <c>execution-identity-incomplete</c> — never a partially-described execution silently sharing a cohort with a
///     fully-described one.
/// </remarks>
public static class BenchmarkJudgeExecutionKey
{
    /// <summary>
    ///     The identity for this judging, or <see langword="null" /> when it cannot be completed. Returns null for an
    ///     <c>unknown</c> backend (where the work ran was never measured) and for any launch that loaded a LoRA,
    ///     projector or draft model (the adapter/base closure is not identified yet, so two such launches cannot be
    ///     shown to be the same execution).
    /// </summary>
    public static BenchmarkJudgeExecutionIdentityV1? TryBuild(LlamaServerLaunchReceipt? receipt, RuntimeEnvironmentFactsV1? environment)
    {
        if (receipt is null || environment is null)
        {
            return null;
        }

        // Aux assets fail closed: the receipt records only that something extra was loaded, not what, so a LoRA judging
        // and a bare judging would otherwise key identically.
        if (receipt.AuxAssets.HasLora || receipt.AuxAssets.HasMmproj || receipt.AuxAssets.HasDraft)
        {
            return null;
        }

        var backend = BenchmarkLaunchBackend.From(receipt);
        if (string.Equals(backend, BenchmarkLaunchBackend.Unknown, StringComparison.Ordinal))
        {
            return null;
        }

        if (receipt.ExecutableSha256 is not { Length: > 0 } executableSha
            || receipt.ExecutableVersion is not { Length: > 0 } executableVersion
            || environment.RuntimeBundle is not { Identity.Length: > 0 } bundle
            || environment.LlamaRuntime is not { } llamaRuntime
            || environment.Hardware is not { } hardware
            || string.IsNullOrEmpty(hardware.OsDescription)
            || string.IsNullOrEmpty(hardware.Arch))
        {
            return null;
        }

        var placement = receipt.Placement;
        var gpus = hardware.Gpus.Select(static gpu => new BenchmarkJudgeExecutionGpuV1(gpu.Name, gpu.TotalBytes, gpu.DriverVersion))
                           .OrderBy(static gpu => gpu.Name, StringComparer.Ordinal)
                           .ThenBy(static gpu => gpu.TotalBytes)
                           .ToArray();

        // A CPU-variant spawn runs without a placement sniffer, so counts are legitimately absent — the backend token
        // (cpu vs metal-unverified) already separates those cohorts. Everything that IS a GPU build, including one
        // that placed nothing, must carry its counts and at least one GPU identity or it cannot be compared.
        var isCpuVariant = receipt.Variant == GpuVariant.Cpu;
        if (!isCpuVariant && (placement.OffloadedLayers is null || placement.TotalLayers is null || gpus.Length == 0))
        {
            return null;
        }

        return new BenchmarkJudgeExecutionIdentityV1(BenchmarkJudgeExecutionIdentityV1.CurrentSchemaVersion,
            executableSha,
            executableVersion,
            BenchmarkLaunchBackend.VariantName(receipt.Variant),
            backend,
            receipt.LaunchProjection.ComputeIdentity(),
            bundle.Identity,
            llamaRuntime.Version,
            llamaRuntime.Provenance,
            llamaRuntime.SourceCommit,
            hardware.OsDescription,
            hardware.Arch,
            placement.Outcome.ToString(),
            isCpuVariant ? null : placement.OffloadedLayers,
            isCpuVariant ? null : placement.TotalLayers,
            gpus,
            hardware.CpuModel,
            hardware.LogicalCores,
            hardware.RamBytes);
    }

    /// <summary>
    ///     The cohort key, or <see langword="null" /> when the identity is incomplete. Bound to the policy hash so two
    ///     different policies can never share a cohort even on an identical machine.
    /// </summary>
    public static string? TryCompute(string policyHash, LlamaServerLaunchReceipt? receipt, RuntimeEnvironmentFactsV1? environment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyHash);
        return TryBuild(receipt, environment) is { } identity ? Compute(policyHash, identity) : null;
    }

    /// <summary>The cohort key for an already-built identity.</summary>
    public static string Compute(string policyHash, BenchmarkJudgeExecutionIdentityV1 identity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyHash);
        ArgumentNullException.ThrowIfNull(identity);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(policyHash + BenchmarkCanonicalJson.Serialize(identity))));
    }
}
