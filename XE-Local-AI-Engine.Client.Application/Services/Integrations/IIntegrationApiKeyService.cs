namespace XE_Local_AI_Engine.Client.Services.Integrations;

/// <summary>
///     One credential as an operator sees it. Carries no secret by construction — the node keeps only a digest — so it
///     is safe to return from any Operator-gated surface. <see cref="AllowedTriggerIds" /> is <see langword="null" />
///     when the key may invoke every trigger.
/// </summary>
public sealed record IntegrationApiKeyView(
    Guid Id,
    Guid PrincipalId,
    string KeyPrefix,
    string Label,
    IReadOnlyList<Guid>? AllowedTriggerIds,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? RevokedAt);

/// <summary>
///     A freshly minted credential: the one-time plaintext <see cref="Key" /> plus the metadata that stays
///     retrievable. Separate from <see cref="IntegrationApiKeyView" /> so the type system — not a comment — is what
///     stops the secret leaking out of a retrieval path. Never log it and never persist it.
/// </summary>
public sealed record GeneratedIntegrationApiKey(string Key, IntegrationApiKeyView View);

/// <summary>
///     Trusted metadata from a successful validation. <see cref="PrincipalId" /> comes FIRST because it is the
///     identity every ownership, uniqueness and fingerprint decision keys on (ruling R4-6);
///     <see cref="KeyPrefix" /> only names which credential was used and carries no authority.
/// </summary>
public sealed record IntegrationApiKeyValidation(Guid PrincipalId, string KeyPrefix, IReadOnlyList<Guid>? AllowedTriggerIds);

/// <summary>
///     Owns the lifecycle of the <c>xeint_</c> bearer credentials that authenticate an external integrator against the
///     hand-mapped integration API: generation, listing, soft revocation, and the constant-time comparison the
///     authentication handler performs.
///     <para>
///         Unlike the inbound-MCP and model-proxy credentials this node holds MANY of these, and several may share one
///         <c>PrincipalId</c> — that is how a credential is rotated or a second one is issued for the same integrator
///         without stranding the sessions and in-flight executions the first one owns.
///     </para>
/// </summary>
public interface IIntegrationApiKeyService
{
    /// <summary>
    ///     Mints a fresh key and returns it in full. This is the ONLY time the plaintext exists outside the caller that
    ///     presents it: only its SHA-256 digest is persisted, so a key not captured here is gone.
    ///     <paramref name="allowedTriggerIds" /> <see langword="null" /> means "every trigger";
    ///     <paramref name="principalId" /> <see langword="null" /> mints a NEW integrator identity, and a supplied
    ///     value adds or rotates a credential for an existing one.
    /// </summary>
    Task<GeneratedIntegrationApiKey> GenerateAsync(string label,
        IReadOnlyList<Guid>? allowedTriggerIds,
        Guid? principalId,
        CancellationToken cancellationToken = default);

    /// <summary>Every credential, revoked ones included — a revoked row is history an operator still needs to read.</summary>
    Task<IReadOnlyList<IntegrationApiKeyView>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Soft revoke: stamps <c>RevokedAtUtc</c> and deletes nothing, because execution rows and kind-3 audit rows
    ///     reference the prefix. Returns <see langword="false" /> when no row matched.
    /// </summary>
    Task<bool> RevokeAsync(Guid keyId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Returns trusted metadata when <paramref name="presented" /> matches a live credential, otherwise
    ///     <see langword="null" />. A malformed value, an unknown prefix, a digest mismatch and a REVOKED key are all
    ///     the same <see langword="null" />, so the caller can never tell them apart (ruling R2-6).
    /// </summary>
    Task<IntegrationApiKeyValidation?> ValidateAsync(string? presented, CancellationToken cancellationToken = default);
}
