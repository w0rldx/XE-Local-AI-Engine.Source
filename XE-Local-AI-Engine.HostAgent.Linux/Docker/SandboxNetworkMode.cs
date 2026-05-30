namespace XE_Local_AI_Engine.HostAgent.Linux.Docker;

/// <summary>
///     Host-side network posture for a sandbox container (Marker J-local plan §4.1). Mirrors the proto
///     <c>SandboxNetworkMode</c> and the provider-neutral <c>SandboxNetworkPolicy</c>: <see cref="None" /> maps to the
///     Docker <c>--network none</c> posture (the secure default).
/// </summary>
public enum SandboxNetworkMode
{
    /// <summary>No network access (the secure default).</summary>
    None = 0,

    /// <summary>A restricted, provider-defined network posture.</summary>
    Restricted = 1
}
