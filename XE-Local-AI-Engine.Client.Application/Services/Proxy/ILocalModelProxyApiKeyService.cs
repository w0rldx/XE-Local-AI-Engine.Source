namespace XE_Local_AI_Engine.Client.Services.Proxy;

/// <summary>
///     Owns the lifecycle of the single bearer credential that authenticates an EXTERNAL tool against this node's
///     inbound OpenAI-compatible model proxy: generation, retrieval for display, revocation, and the constant-time
///     comparison the authentication handler performs.
///     <para>
///         Because the proxy is gated purely on key presence (a node with no key authenticates nobody), generating a
///         key IS how an operator turns the proxy on and revoking it is how they turn it off — there is no separate
///         enabled flag to keep in sync. This mirrors <see cref="IMcpServerApiKeyService" />, which guards the MCP tool
///         surface on the same terms and shares nothing else with this one.
///     </para>
/// </summary>
public interface ILocalModelProxyApiKeyService
{
    /// <summary>
    ///     Mints a fresh key, REPLACING any existing one, and returns it in full. This is the ONLY time the plaintext
    ///     key exists outside the caller that presents it: only its SHA-256 digest is persisted, so a key not captured
    ///     from this return value is gone and can only be replaced by generating another. Every other surface —
    ///     <see cref="GetAsync" /> included — sees only the prefix.
    /// </summary>
    Task<GeneratedLocalModelProxyApiKey> GenerateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Returns the current credential's non-secret metadata, or <see langword="null" /> when none has been
    ///     generated. Deliberately cannot return the key: the node stores only a one-way digest, so a lost key is
    ///     unrecoverable and the operator must generate a replacement and reconfigure every client.
    /// </summary>
    Task<LocalModelProxyApiKeyView?> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>Revokes the credential. Returns <see langword="true" /> when one existed. The proxy then authenticates nobody.</summary>
    Task<bool> RevokeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Returns <see langword="true" /> when <paramref name="presented" /> matches the stored key. Compares in
    ///     constant time and stamps last-used on success. A node with no key never authenticates, so an absent key is a
    ///     closed door rather than an open one.
    /// </summary>
    Task<bool> ValidateAsync(string? presented, CancellationToken cancellationToken = default);
}

/// <summary>
///     The credential as shown to the operator. Carries no secret by construction — the key is not recoverable from
///     the node — so this shape is safe to return from any Operator-gated surface.
/// </summary>
public sealed record LocalModelProxyApiKeyView(string Prefix, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt);

/// <summary>
///     A freshly minted credential: the one-time plaintext <see cref="Key" /> plus the metadata that will remain
///     retrievable afterwards. Separate from <see cref="LocalModelProxyApiKeyView" /> so the type system — not a comment —
///     is what stops the secret being returned from a retrieval path. Never log it, never persist it, never put it in
///     an audit record or an error body: once this value is dropped, the key is gone.
/// </summary>
public sealed record GeneratedLocalModelProxyApiKey(string Key, LocalModelProxyApiKeyView View);
