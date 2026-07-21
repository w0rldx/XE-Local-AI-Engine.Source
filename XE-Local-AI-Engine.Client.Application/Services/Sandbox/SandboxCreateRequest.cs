namespace XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>
///     Request to create a new AgentHome sandbox or attach to the existing one for the same
///     <see cref="SandboxAttachKey" />. Provider-neutral: resource and network preferences are
///     expressed as neutral values that a provider applies only when it advertises the matching capability.
/// </summary>
public sealed record SandboxCreateRequest
{
    /// <summary>Owner/node-scoped identity for the sandbox.</summary>
    public required SandboxAttachKey AttachKey { get; init; }

    /// <summary>The runtime profile to create the sandbox with (e.g. <c>"dotnet-agent-home"</c>).</summary>
    public required string RuntimeProfile { get; init; }

    /// <summary>Optional resource ceiling; applied only when the provider supports resource limits.</summary>
    public SandboxResourceLimits? ResourceLimits { get; init; }

    /// <summary>Requested network posture; defaults to no network.</summary>
    public SandboxNetworkPolicy NetworkPolicy { get; init; } = SandboxNetworkPolicy.None;

    /// <summary>Optional provider-neutral labels/metadata to associate with the sandbox.</summary>
    public IReadOnlyDictionary<string, string>? Labels { get; init; }

    /// <summary>
    ///     Optional engine-managed trusted host workspace. Providers must either confine the sandbox to this root and
    ///     preserve it on kill/restart, or reject the request fail-closed.
    /// </summary>
    public SandboxTrustedHostWorkspace? TrustedHostWorkspace { get; init; }
}
