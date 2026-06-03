namespace XE_Local_AI_Engine.Client.Services.Agents;

/// <summary>
///     Resolves the node-local "Default Assistant" definition id (mode-off persona) once and memoizes it for the process
///     lifetime. The slug is fixed and the seeded row's id never changes within a boot, so the chat send/regenerate hot
///     paths consult this instead of issuing a <c>GetBySeedSlugAsync</c> DB round-trip on every mode-off send. A
///     delete→re-seed across boots naturally produces a fresh process (and thus a fresh cache). Returns <c>null</c> when
///     the seed row is absent (e.g. seeding failed at startup); the caller then degrades to the embedded default persona.
/// </summary>
public interface IDefaultAgentProvider
{
    /// <summary>The memoized Default Assistant definition id, or <c>null</c> when no seeded row exists yet.</summary>
    Task<Guid?> GetDefaultAgentIdAsync(CancellationToken cancellationToken = default);
}
