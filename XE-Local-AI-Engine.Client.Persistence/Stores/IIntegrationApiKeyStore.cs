namespace XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     One credential as a reader sees it. <see cref="KeyHash" /> travels as <see cref="ReadOnlyMemory{T}" /> — a
///     carrier, never compared by value here; the constant-time comparison belongs to the authentication handler.
///     <see cref="PrincipalId" /> is the integrator identity every ownership question keys on;
///     <see cref="KeyPrefix" /> only names which credential was used.
/// </summary>
public sealed record IntegrationApiKeySnapshot(
    Guid Id,
    Guid PrincipalId,
    string KeyPrefix,
    ReadOnlyMemory<byte> KeyHash,
    string Label,
    string? AllowedTriggerIdsJson,
    long CreatedAtUtc,
    long? LastUsedAtUtc,
    long? RevokedAtUtc);

/// <summary>
///     Everything a generate needs. The caller mints both ids: <c>PrincipalId</c> is fresh for a new integrator and
///     reused when rotating or adding a credential for an existing one, which is the whole point of separating it from
///     <c>KeyId</c>.
/// </summary>
public sealed record IntegrationApiKeyCreateCommand(
    Guid KeyId,
    Guid PrincipalId,
    string KeyPrefix,
    ReadOnlyMemory<byte> KeyHash,
    string Label,
    string? AllowedTriggerIdsJson);

/// <summary>
///     Persistence boundary for the <c>xeint_</c> credentials. Unlike the two singleton key stores in this schema, a
///     node holds many of these rows and several may share one <c>PrincipalId</c>.
/// </summary>
public interface IIntegrationApiKeyStore
{
    /// <summary>Inserts a credential and returns it as stored. A duplicate <c>KeyPrefix</c> surfaces as <c>DbUpdateException</c>.</summary>
    Task<IntegrationApiKeySnapshot> CreateAsync(IntegrationApiKeyCreateCommand command, CancellationToken cancellationToken = default);

    /// <summary>Every credential, revoked ones included, ordered <c>CreatedAtUtc</c> then <c>Id</c> descending.</summary>
    Task<IReadOnlyList<IntegrationApiKeySnapshot>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>The authentication lookup: resolves a presented prefix to its row, revoked or not, so the caller can tell the two apart.</summary>
    Task<IntegrationApiKeySnapshot?> GetByPrefixAsync(string keyPrefix, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Records a successful authentication timestamp. Deliberately touches ONLY <c>last_used_at_utc</c>, so a
    ///     last-used stamp never rewrites — or re-seals — the credential digest on the authentication hot path.
    ///     Returns <see langword="false" /> when no row matched.
    /// </summary>
    Task<bool> TouchLastUsedAsync(Guid keyId, long atUtc, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Soft revoke: stamps <c>revoked_at_utc</c> and deletes nothing, because execution and audit rows reference the
    ///     credential's prefix. Returns <see langword="false" /> when no row matched.
    /// </summary>
    Task<bool> RevokeAsync(Guid keyId, long atUtc, CancellationToken cancellationToken = default);
}
