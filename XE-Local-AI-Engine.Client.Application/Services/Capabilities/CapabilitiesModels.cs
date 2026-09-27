namespace XE_Local_AI_Engine.Client.Services.Capabilities;

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
