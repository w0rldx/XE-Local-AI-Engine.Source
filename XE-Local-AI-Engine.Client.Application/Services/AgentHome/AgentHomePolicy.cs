namespace XE_Local_AI_Engine.Client.Services.AgentHome;

/// <summary>
///     The <c>policy.json</c> contents. Minimal shape: network posture and mount rules.
///     Writable host mounts are never permitted; read-only mounts remain gated by provider capabilities and sandbox policy.
/// </summary>
internal sealed record AgentHomePolicy
{
    /// <summary>The policy schema version written by this build.</summary>
    public const int CurrentVersion = 1;

    /// <summary>The policy schema version this file was written with.</summary>
    public required int Version { get; init; }

    /// <summary>
    ///     The network posture for the sandbox. The live process provider does not isolate the network, so this is
    ///     <c>"unrestricted"</c> (the child shares the host network); it becomes <c>"disabled"</c>/<c>"restricted"</c>
    ///     only under a provider that actually enforces egress confinement.
    /// </summary>
    public required string NetworkPolicy { get; init; }

    /// <summary>Whether read-only host mounts may be requested (gated elsewhere).</summary>
    public required bool AllowReadOnlyMounts { get; init; }

    /// <summary>Whether writable host mounts are permitted. Always <see langword="false" />.</summary>
    public required bool WritableMounts { get; init; }
}
