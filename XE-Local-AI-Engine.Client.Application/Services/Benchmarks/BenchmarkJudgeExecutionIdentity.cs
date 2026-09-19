namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>One GPU as the execution identity records it. Driver version is included when present, never required.</summary>
public sealed class BenchmarkJudgeExecutionGpuV1
{
    [JsonPropertyOrder(0)]
    public required string Name { get; init; }

    [JsonPropertyOrder(1)]
    public required long? TotalBytes { get; init; }

    [JsonPropertyOrder(2)]
    public required string? DriverVersion { get; init; }
}

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
public sealed class BenchmarkJudgeExecutionIdentityV1
{
    [JsonPropertyOrder(0)]
    public required int SchemaVersion { get; init; }

    [JsonPropertyOrder(1)]
    public required string ExecutableSha256 { get; init; }

    [JsonPropertyOrder(2)]
    public required string ExecutableVersion { get; init; }

    [JsonPropertyOrder(3)]
    public required string Variant { get; init; }

    [JsonPropertyOrder(4)]
    public required string EffectiveBackend { get; init; }

    [JsonPropertyOrder(5)]
    public required string EffectiveLaunchIdentity { get; init; }

    [JsonPropertyOrder(6)]
    public required string RuntimeBundleIdentity { get; init; }

    [JsonPropertyOrder(7)]
    public required string LlamaRuntimeVersion { get; init; }

    [JsonPropertyOrder(8)]
    public required string LlamaRuntimeProvenance { get; init; }

    [JsonPropertyOrder(9)]
    public required string? LlamaRuntimeSourceCommit { get; init; }

    [JsonPropertyOrder(10)]
    public required string OsDescription { get; init; }

    [JsonPropertyOrder(11)]
    public required string Arch { get; init; }

    [JsonPropertyOrder(12)]
    public required string PlacementOutcome { get; init; }

    [JsonPropertyOrder(13)]
    public required int? PlacementOffloaded { get; init; }

    [JsonPropertyOrder(14)]
    public required int? PlacementTotal { get; init; }

    [JsonPropertyOrder(15)]
    public required IReadOnlyList<BenchmarkJudgeExecutionGpuV1> Gpus { get; init; }

    [JsonPropertyOrder(16)]
    public required string? CpuModel { get; init; }

    [JsonPropertyOrder(17)]
    public required int? LogicalCores { get; init; }

    [JsonPropertyOrder(18)]
    public required long? RamBytes { get; init; }

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
    ///     The cohort key of a judging that ran NO model because every rubric criterion was decided server-side. A
    ///     constant, not a hash: such an attempt has no runtime to describe, and having none is not the same as having
    ///     an incomplete description of one — <c>execution-identity-incomplete</c> would unrank it forever.
    ///     <para>
    ///         <b>Safe only because the judging MODE and every criterion's kind and config are inside
    ///         <c>ComputePolicyHash</c>.</b> That is what makes one policy revision provably one rubric composition, so
    ///         a constant key cannot merge attempts that were graded differently. If any of those ever moved out of the
    ///         policy hash, this sentinel starts joining unlike things and must be revisited with them.
    ///     </para>
    /// </summary>
    public const string VerifiedSentinel = "verified:v1";

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
        var gpus = hardware.Gpus.Select(static gpu => new BenchmarkJudgeExecutionGpuV1 { Name = gpu.Name, TotalBytes = gpu.TotalBytes, DriverVersion = gpu.DriverVersion })
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

        return new BenchmarkJudgeExecutionIdentityV1
        {
            SchemaVersion = BenchmarkJudgeExecutionIdentityV1.CurrentSchemaVersion,
            ExecutableSha256 = executableSha,
            ExecutableVersion = executableVersion,
            Variant = BenchmarkLaunchBackend.VariantName(receipt.Variant),
            EffectiveBackend = backend,
            EffectiveLaunchIdentity = receipt.LaunchProjection.ComputeIdentity(),
            RuntimeBundleIdentity = bundle.Identity,
            LlamaRuntimeVersion = llamaRuntime.Version,
            LlamaRuntimeProvenance = llamaRuntime.Provenance,
            LlamaRuntimeSourceCommit = llamaRuntime.SourceCommit,
            OsDescription = hardware.OsDescription,
            Arch = hardware.Arch,
            PlacementOutcome = placement.Outcome.ToString(),
            PlacementOffloaded = isCpuVariant ? null : placement.OffloadedLayers,
            PlacementTotal = isCpuVariant ? null : placement.TotalLayers,
            Gpus = gpus,
            CpuModel = hardware.CpuModel,
            LogicalCores = hardware.LogicalCores,
            RamBytes = hardware.RamBytes
        };
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
