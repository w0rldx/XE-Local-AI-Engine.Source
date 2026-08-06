namespace XE_Local_AI_Engine.Client.Services.Capabilities;

/// <summary>The model the runtime currently reports as active/loaded.</summary>
/// <param name="Name">Normalized active-model name, or <c>null</c> when none is loaded.</param>
/// <param name="ExpiresAt">When the loaded model is scheduled for eviction, when reported.</param>
internal sealed record ActiveModelInfo(string? Name, DateTimeOffset? ExpiresAt)
{
    /// <summary>Sentinel representing "no active model".</summary>
    public static ActiveModelInfo None { get; } = new(Name: null, ExpiresAt: null);
}

/// <summary>Detected GPU facts used when composing capability reports.</summary>
/// <param name="GpuName">GPU model name as reported by nvidia-smi.</param>
/// <param name="VramMb">Total VRAM in MB, when parseable.</param>
/// <param name="CudaAvailable">True once a CUDA-capable GPU has been confirmed.</param>
internal sealed record GpuInfo(string GpuName, long? VramMb, bool CudaAvailable);

/// <summary>Local hardware facts gathered once per capability report.</summary>
/// <param name="RamMb">Total system RAM in MB, when detectable.</param>
/// <param name="GpuInfo">Primary GPU facts, or <c>null</c> when no GPU was detected.</param>
/// <param name="CpuClass">Human-readable CPU description (model + logical core count).</param>
internal sealed record HardwareSnapshot(long? RamMb, GpuInfo? GpuInfo, string? CpuClass);

/// <summary>One installed-model inventory entry resolved by <see cref="ModelCapabilityProber" />.</summary>
/// <param name="Name">Normalized model name/tag.</param>
/// <param name="Digest">Content digest when discovered from the runtime; <c>null</c> for configured fallbacks.</param>
/// <param name="IsDiscovered">True when the runtime reported the model; false for configured-name fallbacks.</param>
internal sealed record InstalledModelInfo(string Name, string? Digest, bool IsDiscovered);

/// <summary>Result of an installed-model inventory probe.</summary>
/// <param name="Models">Discovered + configured-fallback models, normalized, deduped and ordered.</param>
/// <param name="OllamaQuerySucceeded">True when the runtime inventory query succeeded (false on transport failure).</param>
/// <param name="Diagnostics">Diagnostics raised while probing (for example runtime-unreachable).</param>
internal sealed record InstalledModelInventoryResult(IReadOnlyList<InstalledModelInfo> Models, bool OllamaQuerySucceeded, IReadOnlyList<string> Diagnostics);

/// <summary>Result of a model-runtime reachability/version probe.</summary>
/// <param name="Reachable">True when the runtime endpoint responded as running.</param>
/// <param name="Version">Normalized runtime version string when reachable; otherwise <c>null</c>.</param>
/// <param name="Diagnostics">Diagnostics raised while probing (for example runtime-unreachable).</param>
internal sealed record OllamaRuntimeStatus(bool Reachable, string? Version, IReadOnlyList<string> Diagnostics);
