namespace XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>
///     Provider-neutral network posture requested for a sandbox. The AgentHome default is <see cref="None" /> (no
///     network) per the security defaults (AgentHome plan §6.2.1, §11). A provider that does not enforce a policy
///     advertises the absence of <see cref="SandboxProviderCapabilities.SupportsNetworkPolicy" />.
/// </summary>
public enum SandboxNetworkPolicy
{
    /// <summary>No network access (the secure default).</summary>
    None = 0,

    /// <summary>A provider-defined restricted policy (e.g. an egress allow-list). Honored only when the provider advertises support.</summary>
    Restricted = 1
}
