namespace XE_Local_AI_Engine.HostAgent.Linux.Docker;

/// <summary>
///     Server-side configuration for the narrow model-fit utility runner (plan Marker 2). Bound from the
///     <c>HostAgent:ModelFitUtility</c> section. These values are the trust boundary the
///     <c>ModelFitUtilityControlService</c> enforces: only an image whose repository is on
///     <see cref="AllowedImageRepositories" /> may run, the benchmark path attaches only the
///     <see cref="RuntimeNetworkName" /> managed network, and runs are bounded by the operation-specific max-runtime
///     ceiling. There is intentionally no setting that would let the wire choose the image, the argv, or an arbitrary
///     network.
/// </summary>
public sealed class ModelFitUtilityOptions
{
    public const string SectionName = "HostAgent:ModelFitUtility";

    /// <summary>The single approved llmfit repository the Marker 0 process sanctioned. Used as the default allowlist entry.</summary>
    public const string DefaultAllowedRepository = "ghcr.io/alexsjones/llmfit";

    /// <summary>
    ///     The exact repositories (ordinal match) a utility image reference may carry. Defense in depth on top of the
    ///     node-side validation: an image whose repository is not listed is rejected without running.
    /// </summary>
    public IReadOnlyList<string> AllowedImageRepositories { get; set; } = [DefaultAllowedRepository];

    /// <summary>
    ///     The managed runtime network the benchmark path attaches to so the provider DNS name (e.g.
    ///     <c>http://ollama:11434</c>) resolves. Mirrors <c>HostAgent:Runtime:RuntimeNetwork</c>.
    /// </summary>
    public string RuntimeNetworkName { get; set; } = "xe-engine-net";

    /// <summary>Default maximum runtime for a recommendation run when the request supplies no positive timeout.</summary>
    public int DefaultMaxRuntimeSeconds { get; set; } = 600;

    /// <summary>Default maximum runtime for a benchmark run when the request supplies no positive timeout.</summary>
    public int BenchmarkMaxRuntimeSeconds { get; set; } = 1800;

    /// <summary>Development-only: keep a failed utility container instead of removing it, for debugging. Default off.</summary>
    public bool RetainFailedContainersForDebug { get; set; }

    /// <summary>The upper bound the recommend <c>--limit</c> is clamped to HostAgent-side (defense in depth).</summary>
    public int MaxRecommendLimit { get; set; } = 50;
}
