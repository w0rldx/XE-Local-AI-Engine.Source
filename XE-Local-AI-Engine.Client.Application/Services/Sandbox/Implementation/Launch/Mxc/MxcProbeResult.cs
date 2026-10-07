namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Mxc;

/// <summary>What <see cref="MxcProbe.Measure" /> found on this host.</summary>
public sealed class MxcProbeResult
{
    /// <summary>The marker MXC 1.0.0 puts in its AppContainer + DACL host-prep warnings (<c>wxc-host-prep prepare-…</c>).</summary>
    internal const string HostPrepMarker = "wxc-host-prep";

    /// <summary>ProcessContainer is available and accepts the engine's policy.</summary>
    public required bool Supported { get; init; }

    /// <summary>The SDK <c>IsolationTier</c> name (e.g. <c>AppContainerDacl</c>) when supported.</summary>
    public string? Tier { get; init; }

    /// <summary>MXC's warnings verbatim, including host-prep recommendations.</summary>
    public required IReadOnlyList<string> Warnings { get; init; }

    /// <summary>Why the mechanism is unavailable, when it is.</summary>
    public string? Reason { get; init; }

    /// <summary>
    ///     MXC warned that one-time admin host prep (system-drive metadata ACEs, or the per-boot NUL device descriptor) is missing;
    ///     children may then fail to start. The product never runs the prep itself.
    /// </summary>
    public bool HostPrepMissing => Warnings.Any(warning => warning.Contains(HostPrepMarker, StringComparison.OrdinalIgnoreCase));
}
