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

    /// <summary>
    ///     Whether the user holds a live refresh token created at exactly <paramref name="createdAtUtc" />.
    /// </summary>
    /// <remarks>
    ///     Nothing records WHY a token was revoked, so its successor is the discriminator: rotation stamps the
    ///     revocation and the replacement from one instant, and only rotation issues anything at all.
    /// </remarks>
    Task<bool> HasActiveTokenCreatedAtAsync(string userId, DateTime createdAtUtc, DateTime now, CancellationToken cancellationToken);

    /// <summary>Stamps one token revoked at <paramref name="now" /> and saves.</summary>
    Task RevokeAsync(NodeRefreshToken token, DateTime now, CancellationToken cancellationToken);

    /// <summary>Persists a newly issued refresh token.</summary>
    Task AddRefreshTokenAsync(NodeRefreshToken token, CancellationToken cancellationToken);

    /// <summary>Revokes every live refresh token of the user, stamping <paramref name="now" /> on each.</summary>
    /// <remarks>
    ///     The caller supplies the instant so that rotation's revoke-and-reissue share one — the clock read that
    ///     <see cref="HasActiveTokenCreatedAtAsync" /> reads back as "replaced, not logged out".
    /// </remarks>
    Task RevokeActiveTokensAsync(string userId, DateTime now, CancellationToken cancellationToken);

    /// <summary>Applies pending identity migrations. Startup bootstrap, before anything reads the schema.</summary>
    Task MigrateAsync(CancellationToken cancellationToken);

    /// <summary>
    ///     Seeds the named role when it is absent, and reports whether this call inserted it.
    /// </summary>
    Task<bool> EnsureRoleAsync(string roleName, CancellationToken cancellationToken);
}
