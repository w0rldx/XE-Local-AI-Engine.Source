namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Names the backend a launch actually ran on, from the launch's own receipt. Facts only: the CPU/GPU build it
///     ran, and — for a GPU build — whether the model's layers reached the GPU at all.
/// </summary>
public static class BenchmarkLaunchBackend
{
    /// <summary>A CPU build on macOS, where llama.cpp may or may not have used Metal and the receipt cannot say.</summary>
    public const string MetalUnverified = "metal-unverified";

    /// <summary>A CPU llama.cpp build.</summary>
    public const string Cpu = "cpu";

    /// <summary>A GPU build that placed no layer on the GPU — it served from system RAM.</summary>
    public const string CpuFallback = "cpu-fallback";

    /// <summary>No load banner was observed, so where the work ran was never measured.</summary>
    public const string Unknown = "unknown";

    private const string MacOs = "macos";

    /// <summary>
    ///     The llama.cpp build token used on the wire (<c>cpu</c>/<c>cuda</c>/<c>vulkan</c>). Spelled out rather than
    ///     lower-cased from the enum so a build added later reads as <c>unknown</c> instead of inventing a token the
    ///     wire contract never defined.
    /// </summary>
    public static string VariantName(GpuVariant variant) =>
        variant switch
        {
            GpuVariant.Cpu => Cpu,
            GpuVariant.Cuda => "cuda",
            GpuVariant.Vulkan => "vulkan",
            _ => Unknown
        };

    /// <summary>The backend token for <paramref name="receipt" />.</summary>
    public static string From(LlamaServerLaunchReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (receipt.Variant == GpuVariant.Cpu)
        {
            return string.Equals(receipt.Os, MacOs, StringComparison.Ordinal) ? MetalUnverified : Cpu;
        }

        return receipt.Placement.Outcome switch
        {
            LlamaServerPlacementOutcome.Full or LlamaServerPlacementOutcome.Partial => VariantName(receipt.Variant),
            LlamaServerPlacementOutcome.None => CpuFallback,
            _ => Unknown
        };
    }
}

/// <summary>
///     Builds the durable launch-evidence checkpoint an executor persists: the provider's receipt plus the
///     pre-launch environment capture, canonicalized and hashed once so two runs compare by value.
/// </summary>
internal static class BenchmarkLaunchEvidence
{
    /// <summary>
    ///     The checkpoint command, or <see langword="null" /> when there is nothing to record because the
    ///     environment capture never happened. A <see langword="null" /> <paramref name="receipt" /> is a recorded
    ///     fact — the spawn never reached readiness — not a reason to skip the environment facts.
    /// </summary>
    public static BenchmarkLaunchReceiptCommand? TryBuild(LlamaServerLaunchReceipt? receipt,
        RuntimeEnvironmentFactsV1? environmentFacts,
        string kvCacheTypeSource)
    {
        if (environmentFacts is null)
        {
            return null;
        }

        var environmentJson = BenchmarkCanonicalJson.Serialize(environmentFacts);
        var environmentHash = BenchmarkCanonicalJson.Hash(environmentJson);
        if (receipt is null)
        {
            return new BenchmarkLaunchReceiptCommand(ReceiptJson: null,
                environmentJson,
                environmentHash,
                ReceiptHash: null,
                EffectiveLaunchIdentity: null,
                EffectiveBackend: null,
                PlacementOffloaded: null,
                PlacementTotal: null,
                ExecutableSha256: null,
                HasAuxAssets: null,
                kvCacheTypeSource);
        }

        var receiptJson = BenchmarkCanonicalJson.Serialize(receipt);
        return new BenchmarkLaunchReceiptCommand(receiptJson,
            environmentJson,
            environmentHash,
            BenchmarkCanonicalJson.Hash(receiptJson),
            receipt.LaunchProjection.ComputeIdentity(),
            BenchmarkLaunchBackend.From(receipt),
            receipt.Placement.OffloadedLayers,
            receipt.Placement.TotalLayers,
            receipt.ExecutableSha256,
            receipt.AuxAssets.HasLora || receipt.AuxAssets.HasMmproj || receipt.AuxAssets.HasDraft,
            kvCacheTypeSource);
    }
}
