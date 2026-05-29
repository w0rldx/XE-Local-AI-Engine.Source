namespace XE_Local_AI_Engine.Client.Services.AgentHome;

/// <summary>
///     The resolved worker-local AgentHome layout returned by
///     <see cref="IAgentHomeManifestService.InitializeAsync" />: the absolute <c>agent-home</c> root path on the
///     worker host and the manifest in force after initialization.
/// </summary>
internal sealed record AgentHomeLayout
{
    /// <summary>The absolute path to the <c>agent-home</c> directory on the worker host.</summary>
    public required string RootPath { get; init; }

    /// <summary>The manifest in force after initialization (status <see cref="AgentHomeStatus.Ready" /> on success).</summary>
    public required AgentHomeManifest Manifest { get; init; }
}
