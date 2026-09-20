namespace XE_Local_AI_Engine.Client.Services.Agents;

/// <summary>
///     Resolves the node-local "Default Assistant" definition id once and memoizes it for the process lifetime.
/// </summary>
/// <remarks>
///     The slug is fixed and the seeded row's id never changes within a boot, so the send and regenerate hot paths
///     consult this instead of a <c>GetBySeedSlugAsync</c> round-trip per mode-off send; a delete and re-seed across
///     boots produces a fresh process and therefore a fresh cache. <c>null</c> when the seed row is absent, which
///     degrades the caller to the embedded default persona.
/// </remarks>
public interface IDefaultAgentProvider
{
    /// <summary>The memoized Default Assistant definition id, or <c>null</c> when no seeded row exists yet.</summary>
    Task<Guid?> GetDefaultAgentIdAsync(CancellationToken cancellationToken = default);
}
