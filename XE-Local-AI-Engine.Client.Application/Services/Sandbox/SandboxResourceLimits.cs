namespace XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>
///     Provider-neutral resource ceiling for a sandbox. Every member is optional; a provider
///     applies only the limits it advertises via <see cref="SandboxProviderCapabilities.SupportsResourceLimits" />
///     and ignores the rest. No provider SDK type appears here.
/// </summary>
public sealed record SandboxResourceLimits
{
    /// <summary>Maximum CPU cores the sandbox may use (maps to a provider-specific limit such as <c>--cpus</c>).</summary>
    public double? CpuCount { get; init; }

    /// <summary>Maximum resident memory in megabytes.</summary>
    public int? MemoryMb { get; init; }

    /// <summary>Maximum number of processes/threads (maps to a provider-specific limit such as <c>--pids-limit</c>).</summary>
    public int? PidsLimit { get; init; }
}
