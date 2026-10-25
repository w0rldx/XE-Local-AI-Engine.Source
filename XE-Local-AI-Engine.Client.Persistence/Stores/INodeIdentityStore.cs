namespace XE_Local_AI_Engine.Client.Persistence.Stores;

using Microsoft.EntityFrameworkCore.Storage;
using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     Persistence boundary for the node identity database: the single administrator account, the refresh-token
///     ledger, and the one-time migrate-and-seed the host runs at startup.
/// </summary>
/// <remarks>
///     Scoped, and deliberately sharing its scope's context with ASP.NET Identity's own <c>UserManager</c> and
///     <c>SignInManager</c> stores: that shared context is what puts an Identity write inside the serializable
///     transaction <see cref="BeginSerializableTransactionAsync" /> opens. Split them across two contexts and
///     first-run setup could commit an administrator whose role assignment rolled back.
/// </remarks>
public interface INodeIdentityStore
{
    /// <summary>Whether an account has completed first-run setup.</summary>
    Task<bool> HasCompletedSetupAsync(CancellationToken cancellationToken);

    /// <summary>
    ///     The single completed-setup administrator, or <see langword="null" /> before first-run setup.
    /// </summary>
    /// <remarks>
    ///     Tracked, because the caller hands the result to <c>UserManager</c> to change a password or clear a lockout
    ///     and those writes go through this same context.
    /// </remarks>
    Task<NodeUser?> FindCompletedSetupUserAsync(CancellationToken cancellationToken);

    /// <summary>Opens a serializable transaction over the identity context, covering Identity's own writes too.</summary>
    Task<IDbContextTransaction> BeginSerializableTransactionAsync(CancellationToken cancellationToken);

    /// <summary>Re-reads one tracked user from the database, overwriting the entity's current values.</summary>
    /// <remarks>
    ///     Authentication can load the user into this scope's context before a caller reaches its own serialization
    ///     lock, so a refresh is what makes a serialized read-modify-write merge against the latest row and
    ///     concurrency stamp rather than the snapshot captured while parallel requests were authorizing.
    /// </remarks>
    Task ReloadAsync(NodeUser user, CancellationToken cancellationToken);

    /// <summary>The stored refresh token with this hash, tracked, or <see langword="null" /> when none matches.</summary>
    Task<NodeRefreshToken?> FindRefreshTokenAsync(string tokenHash, CancellationToken cancellationToken);

    /// <summary>The stored refresh token with this id, tracked, or <see langword="null" /> when none matches.</summary>
    Task<NodeRefreshToken?> FindRefreshTokenByIdAsync(string id, CancellationToken cancellationToken);

    /// <summary>
    ///     Revokes <paramref name="presented" /> at <paramref name="now" />, links it to <paramref name="successor" /> and
    ///     persists the successor, in one save.
    /// </summary>
    /// <remarks>
    ///     The link is what tells rotation apart from every other revoke path: logout, a password change and a reset
    ///     revoke without naming a successor, so only a rotated token can ever qualify for the reuse grace.
    /// </remarks>
    Task RotateAsync(NodeRefreshToken presented, NodeRefreshToken successor, DateTime now, CancellationToken cancellationToken);

    /// <summary>Persists a newly issued refresh token that replaces nothing: a sign-in, or a grace refresh.</summary>
    Task AddRefreshTokenAsync(NodeRefreshToken token, CancellationToken cancellationToken);

    /// <summary>Revokes every live session and refresh token of the user — every signed-in client — stamping <paramref name="now" /> on each.</summary>
    /// <remarks>
    ///     Two set-based updates (tokens, then sessions) that join an open transaction, so whatever a concurrent refresh
    ///     committed earlier is caught and a later one finds its token revoked. They bypass the change tracker: an entity
    ///     tracked here keeps a stale value, but EF saves only modified columns, so a later save never un-revokes a row.
    /// </remarks>
    Task RevokeActiveTokensAsync(string userId, DateTime now, CancellationToken cancellationToken);

    /// <summary>Revokes one refresh token that belongs to no session (issued before sessions existed).</summary>
    Task RevokeRefreshTokenAsync(NodeRefreshToken token, DateTime now, CancellationToken cancellationToken);

    /// <summary>Persists a new sign-in session.</summary>
    Task AddSessionAsync(NodeAuthSession session, CancellationToken cancellationToken);

    /// <summary>The session with this id, tracked, or <see langword="null" /> when none matches.</summary>
    /// <remarks>Tracked so a <see cref="NodeAuthSession.LastUsedAtUtc" /> bump rides the next token save of this scope.</remarks>
    Task<NodeAuthSession?> FindSessionAsync(string id, CancellationToken cancellationToken);

    /// <summary>Whether session <paramref name="sessionId" /> exists, belongs to <paramref name="userId" /> and is not revoked.</summary>
    Task<bool> IsSessionLiveAsync(string sessionId, string userId, CancellationToken cancellationToken);

    /// <summary>The user's unrevoked sessions that still hold an unexpired, unrevoked refresh token, most recently used first.</summary>
    Task<IReadOnlyList<NodeAuthSession>> ListLiveSessionsAsync(string userId, DateTime now, CancellationToken cancellationToken);

    /// <summary>Revokes one of the user's sessions and its live refresh tokens.</summary>
    /// <returns><see langword="false" /> when the user owns no session with that id.</returns>
    Task<bool> RevokeSessionAsync(string userId, string sessionId, DateTime now, CancellationToken cancellationToken);

    /// <summary>Applies pending identity migrations. Startup bootstrap, before anything reads the schema.</summary>
    Task MigrateAsync(CancellationToken cancellationToken);

    /// <summary>
    ///     Seeds the named role when it is absent, and reports whether this call inserted it.
    /// </summary>
    Task<bool> EnsureRoleAsync(string roleName, CancellationToken cancellationToken);
}
