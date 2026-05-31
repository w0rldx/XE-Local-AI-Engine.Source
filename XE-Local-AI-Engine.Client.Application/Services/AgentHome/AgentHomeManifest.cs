namespace XE_Local_AI_Engine.Client.Services.AgentHome;

/// <summary>
///     The <c>manifest.json</c> contents for a worker-local AgentHome. Carries the schema
///     version, lifecycle status, the owner/node it belongs to, and creation/update timestamps (sourced from the
///     injected <see cref="TimeProvider" />). A change of <see cref="OwnerUserId" /> forbids reuse of the layout.
/// </summary>
internal sealed record AgentHomeManifest
{
    /// <summary>The manifest schema version written by this build.</summary>
    public const int CurrentVersion = 1;

    /// <summary>The manifest schema version this layout was written with.</summary>
    public required int Version { get; init; }

    /// <summary>The lifecycle state of the layout.</summary>
    public required AgentHomeStatus Status { get; init; }

    /// <summary>The owner the layout belongs to. A change forbids reuse.</summary>
    public required string OwnerUserId { get; init; }

    /// <summary>The node the layout belongs to.</summary>
    public required string NodeId { get; init; }

    /// <summary>The sandbox provider in force when the layout was created.</summary>
    public required string ProviderName { get; init; }

    /// <summary>The runtime profile in force when the layout was created.</summary>
    public required string RuntimeProfile { get; init; }

    /// <summary>When the layout was first created.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>When the manifest was last written.</summary>
    public required DateTimeOffset UpdatedAt { get; init; }
}
