namespace XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>
///     An opaque reference to a live AgentHome sandbox. Carries the provider name, the
///     provider's sandbox/container id, the <see cref="SandboxAttachKey" /> it was created/attached under, its
///     creation time, and the manifest version in force. Immutable: liveness is owned by the provider, so an
///     operation against a killed sandbox throws <see cref="SandboxHandleInvalidException" /> rather than reading a
///     stale flag off the handle.
/// </summary>
public sealed record SandboxHandle
{
    /// <summary>The provider that owns this sandbox.</summary>
    public required string ProviderName { get; init; }

    /// <summary>The provider's sandbox/container id.</summary>
    public required string SandboxId { get; init; }

    /// <summary>The attach key the sandbox was created or attached under.</summary>
    public required SandboxAttachKey AttachKey { get; init; }

    /// <summary>When the sandbox was created (from the provider's <see cref="TimeProvider" />).</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>The AgentHome manifest version in force for this sandbox.</summary>
    public required int ManifestVersion { get; init; }
}
