namespace XE_Local_AI_Engine.Client.Services.Capabilities;

/// <summary>The model the runtime currently reports as active/loaded.</summary>
internal sealed class ActiveModelInfo
{
    /// <summary>Normalized active-model name, or <c>null</c> when none is loaded.</summary>
    public required string? Name { get; init; }

    /// <summary>When the loaded model is scheduled for eviction, when reported.</summary>
    public required DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>Sentinel representing "no active model".</summary>
    public static ActiveModelInfo None { get; } = new()
    {
        Name = null,
        ExpiresAt = null
    };
}

/// <summary>Detected GPU facts used when composing capability reports.</summary>
internal sealed record GpuInfo
{
    /// <summary>GPU model name as reported by nvidia-smi.</summary>
    public required string GpuName { get; init; }

    /// <summary>Total VRAM in MB, when parseable.</summary>
    public required long? VramMb { get; init; }

    /// <summary>True once a CUDA-capable GPU has been confirmed.</summary>
    public required bool CudaAvailable { get; init; }
}

/// <summary>Local hardware facts gathered once per capability report.</summary>
internal sealed class HardwareSnapshot
{
    /// <summary>Total system RAM in MB, when detectable.</summary>
    public required long? RamMb { get; init; }

    /// <summary>Primary GPU facts, or <c>null</c> when no GPU was detected.</summary>
    public required GpuInfo? GpuInfo { get; init; }

    /// <summary>Human-readable CPU description (model + logical core count).</summary>
    public required string? CpuClass { get; init; }
}

/// <summary>One installed-model inventory entry resolved by <see cref="ModelCapabilityProber" />.</summary>
internal sealed class InstalledModelInfo
{
    /// <summary>Normalized model name/tag.</summary>
    public required string Name { get; init; }

    /// <summary>Content digest when discovered from the runtime; <c>null</c> for configured fallbacks.</summary>
    public required string? Digest { get; init; }

    /// <summary>True when the runtime reported the model; false for configured-name fallbacks.</summary>
    public required bool IsDiscovered { get; init; }
}

/// <summary>Result of an installed-model inventory probe.</summary>
internal sealed class InstalledModelInventoryResult
{
    /// <summary>Discovered + configured-fallback models, normalized, deduped and ordered.</summary>
    public required IReadOnlyList<InstalledModelInfo> Models { get; init; }

    /// <summary>True when the runtime inventory query succeeded (false on transport failure).</summary>
    public required bool OllamaQuerySucceeded { get; init; }

    /// <summary>Diagnostics raised while probing (for example runtime-unreachable).</summary>
    public required IReadOnlyList<string> Diagnostics { get; init; }
}

/// <summary>Result of a model-runtime reachability/version probe.</summary>
internal sealed class OllamaRuntimeStatus
{
    /// <summary>True when the runtime endpoint responded as running.</summary>
    public required bool Reachable { get; init; }

    /// <summary>Normalized runtime version string when reachable; otherwise <c>null</c>.</summary>
    public required string? Version { get; init; }

    /// <summary>Diagnostics raised while probing (for example runtime-unreachable).</summary>
    public required IReadOnlyList<string> Diagnostics { get; init; }
}
