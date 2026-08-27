namespace XE_Local_AI_Engine.Client.Services.ExternalProviders;

using XE_Local_AI_Engine.Providers.Abstractions.External;

/// <summary>
///     Persistence boundary for the operator's external OpenAI-compatible connections.
/// </summary>
/// <remarks>
///     <para>
///         The write surface is deliberately per-connection rather than "save the whole config": a whole-config write
///         from a UI that rendered a stale list would silently delete a connection added in another tab, and it would
///         force every caller to carry every other connection's API key just to edit one display name.
///     </para>
///     <para>
///         Both writers are compare-and-swap on <see cref="StoredExternalProviderConfig.Revision" />. A caller that
///         passes <see langword="null" /> is asserting "I do not care what is there" and wins unconditionally; a caller
///         that passes the revision it read is rejected with <see cref="ExternalProviderWriteResult.Superseded" /> when
///         the file moved underneath it.
///     </para>
/// </remarks>
public interface IExternalProviderStore
{
    /// <summary>
    ///     The whole stored configuration. Returns an EMPTY config — never <see langword="null" /> — when no file
    ///     exists, which is the shipped default; an unreadable or undecryptable file also reads as empty after the
    ///     store has quarantined it, because a node whose external store is corrupt has no connections, not unknown ones.
    /// </summary>
    Task<StoredExternalProviderConfig> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Inserts or replaces one connection by its canonical id, preserving the stored API key unless the request
    ///     supplies a new one or explicitly clears it.
    /// </summary>
    /// <exception cref="ExternalProviderValidationException">The request violates the stored shape's contract.</exception>
    Task<ExternalProviderWriteResult> SaveConnectionAsync(ExternalProviderConnectionSaveRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Removes one connection and every model registered on it. Removing a connection that is already absent is a
    ///     success with no change, so a retried delete after a partial failure is not an error.
    /// </summary>
    Task<ExternalProviderWriteResult> DeleteConnectionAsync(string connectionId, string? expectedRevision, CancellationToken cancellationToken = default);
}

/// <summary>The outcome of a store write.</summary>
public abstract record ExternalProviderWriteResult
{
    private ExternalProviderWriteResult()
    {
    }

    /// <summary>The write landed; <paramref name="Config" /> is the configuration as it now stands on disk.</summary>
    public sealed record Committed(StoredExternalProviderConfig Config, bool Changed) : ExternalProviderWriteResult;

    /// <summary>
    ///     The write was refused because the file had moved past <c>ExpectedRevision</c>. <paramref name="Current" /> is
    ///     what is actually stored, so the caller can re-render rather than guess.
    /// </summary>
    public sealed record Superseded(StoredExternalProviderConfig Current) : ExternalProviderWriteResult;
}

/// <summary>
///     One connection as an operator's save presents it: raw (not yet normalized) base URL, and an API key whose
///     ABSENCE means "keep what is stored" rather than "clear it".
/// </summary>
public sealed record ExternalProviderConnectionSaveRequest
{
    /// <summary>The connection slug; canonicalized and validated by the store.</summary>
    public required string Id { get; init; }

    /// <summary>The operator's label.</summary>
    public required string DisplayName { get; init; }

    /// <summary>The operator-entered endpoint; normalized to its canonical <c>…/v1/</c> form exactly once, here.</summary>
    public required string BaseUrl { get; init; }

    /// <summary>The declared trust locality.</summary>
    public required ExternalProviderLocality Locality { get; init; }

    /// <summary>
    ///     A NEW API key, or <see langword="null" />/blank to keep whatever is stored. The masked round-trip an editor
    ///     performs sends no key back, so treating blank as "clear" would silently de-authenticate a working connection
    ///     the first time the operator renamed it.
    /// </summary>
    public string? ApiKey { get; init; }

    /// <summary>
    ///     Explicitly removes the stored key, which is the only way to go from authenticated back to keyless. Takes
    ///     precedence over <see cref="ApiKey" /> so the intent can never be ambiguous.
    /// </summary>
    public bool ClearApiKey { get; init; }

    /// <summary>Per-connection network timeout in seconds, or <see langword="null" /> for the transport default.</summary>
    public int? TimeoutSeconds { get; init; }

    /// <summary>The models registered on the connection. May be empty: a probe-then-pick flow saves the connection first.</summary>
    public IReadOnlyList<ExternalProviderModelSaveRequest> Models { get; init; } = [];

    /// <summary>The revision the caller read, or <see langword="null" /> to write unconditionally.</summary>
    public string? ExpectedRevision { get; init; }
}

/// <summary>One model registration as an operator's save presents it.</summary>
public sealed record ExternalProviderModelSaveRequest
{
    /// <summary>The backing model id on the remote server.</summary>
    public required string WireId { get; init; }

    /// <summary>Optional friendly label.</summary>
    public string? DisplayName { get; init; }

    /// <summary>The declared context window in tokens.</summary>
    public int? ContextLength { get; init; }

    /// <summary>Whether the model may be offered tools.</summary>
    public bool SupportsTools { get; init; }

    /// <summary>Whether the model accepts image input.</summary>
    public bool SupportsVision { get; init; }

    /// <summary>Whether the model produces a reasoning channel.</summary>
    public bool SupportsReasoning { get; init; }

    /// <summary>Whether the endpoint honours a top-level <c>reasoning_effort</c> body field.</summary>
    public bool SupportsReasoningEffort { get; init; }

    /// <summary>The effort applied when the turn selects none; validated against the chat reasoning vocabulary.</summary>
    public string? DefaultReasoningEffort { get; init; }
}

/// <summary>
///     A save was refused because the request itself is not storable. Separate from a transport or IO failure so the
///     endpoint layer can map it to a 400 rather than a 500, and so a validation bug can never read as node trouble.
/// </summary>
public sealed class ExternalProviderValidationException : Exception
{
    public ExternalProviderValidationException()
    {
    }

    public ExternalProviderValidationException(string message) : base(message)
    {
    }

    public ExternalProviderValidationException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
